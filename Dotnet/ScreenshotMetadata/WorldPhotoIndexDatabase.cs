#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VRCX
{
    public sealed class WorldPhotoIndexDatabase
    {
        private const int SchemaVersion = 1;
        private const int MaximumPageSize = 100;
        private readonly string _databasePath;
        private readonly object _writerLock = new();

        public WorldPhotoIndexDatabase(string databasePath)
        {
            _databasePath = Path.GetFullPath(databasePath);
            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            InitializeWithRecovery();
        }

        public string DatabasePath => _databasePath;

        public WorldPhotoRoot SetCurrentRoot(string rootPath)
        {
            var canonicalPath = CanonicalizeDirectory(rootPath);
            var pathKey = CreatePathKey(canonicalPath);

            lock (_writerLock)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                long? currentRootId = null;
                using (var current = new SQLiteCommand("SELECT id FROM world_photo_roots WHERE is_current = 1 LIMIT 1;", connection, transaction))
                {
                    var value = current.ExecuteScalar();
                    if (value != null && value != DBNull.Value)
                        currentRootId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                }
                ExecuteNonQuery(connection, transaction, "UPDATE world_photo_roots SET is_current = 0 WHERE is_current <> 0;");

                using (var upsert = new SQLiteCommand(@"
                    INSERT INTO world_photo_roots (canonical_path, path_key, is_current, created_at_utc, last_seen_at_utc)
                    VALUES (@path, @pathKey, 1, @now, @now)
                    ON CONFLICT(path_key) DO UPDATE SET
                        canonical_path = excluded.canonical_path,
                        is_current = 1,
                        last_seen_at_utc = excluded.last_seen_at_utc;", connection, transaction))
                {
                    upsert.Parameters.AddWithValue("@path", canonicalPath);
                    upsert.Parameters.AddWithValue("@pathKey", pathKey);
                    upsert.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.UtcTicks);
                    upsert.ExecuteNonQuery();
                }

                long rootId;
                using (var select = new SQLiteCommand("SELECT id FROM world_photo_roots WHERE path_key = @pathKey;", connection, transaction))
                {
                    select.Parameters.AddWithValue("@pathKey", pathKey);
                    rootId = Convert.ToInt64(select.ExecuteScalar(), CultureInfo.InvariantCulture);
                }

                if (currentRootId.HasValue && currentRootId.Value != rootId)
                {
                    using var invalidate = new SQLiteCommand("DELETE FROM world_photos WHERE root_id = @rootId;", connection, transaction);
                    invalidate.Parameters.AddWithValue("@rootId", rootId);
                    invalidate.ExecuteNonQuery();
                }

                using (var state = new SQLiteCommand(@"
                    INSERT INTO world_photo_index_state (singleton, schema_version, current_root_id, generation_sequence, lifecycle_state)
                    VALUES (1, @schemaVersion, @rootId, 0, 'idle')
                    ON CONFLICT(singleton) DO UPDATE SET current_root_id = excluded.current_root_id;", connection, transaction))
                {
                    state.Parameters.AddWithValue("@schemaVersion", SchemaVersion);
                    state.Parameters.AddWithValue("@rootId", rootId);
                    state.ExecuteNonQuery();
                }

                transaction.Commit();
                return new WorldPhotoRoot { Id = rootId, CanonicalPath = canonicalPath, PathKey = pathKey, IsCurrent = true };
            }
        }

        public WorldPhotoRoot? GetCurrentRoot()
        {
            using var connection = OpenConnection();
            using var command = new SQLiteCommand(@"
                SELECT id, canonical_path, path_key
                FROM world_photo_roots
                WHERE is_current = 1
                LIMIT 1;", connection);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new WorldPhotoRoot
                {
                    Id = reader.GetInt64(0),
                    CanonicalPath = reader.GetString(1),
                    PathKey = reader.GetString(2),
                    IsCurrent = true
                }
                : null;
        }

        public long BeginReconciliation(long rootId)
        {
            lock (_writerLock)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                using var command = new SQLiteCommand(@"
                    UPDATE world_photo_index_state
                    SET generation_sequence = generation_sequence + 1,
                        lifecycle_state = 'scanning'
                    WHERE singleton = 1 AND current_root_id = @rootId;
                    SELECT generation_sequence FROM world_photo_index_state
                    WHERE singleton = 1 AND current_root_id = @rootId;", connection, transaction);
                command.Parameters.AddWithValue("@rootId", rootId);
                var value = command.ExecuteScalar();
                if (value == null || value == DBNull.Value)
                    throw new InvalidOperationException("The requested photo root is not current.");
                transaction.Commit();
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
        }

        public WorldPhotoIndexRecord Upsert(long rootId, WorldPhotoIndexWrite write)
        {
            return UpsertBatch(rootId, new[] { write })[0];
        }

        public IReadOnlyList<WorldPhotoIndexRecord> UpsertBatch(long rootId, IReadOnlyList<WorldPhotoIndexWrite> writes)
        {
            if (writes.Count == 0)
                return Array.Empty<WorldPhotoIndexRecord>();
            lock (_writerLock)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                EnsureCurrentRoot(connection, transaction, rootId, null);
                long currentGeneration;
                using (var generationCommand = new SQLiteCommand(@"
                    SELECT generation_sequence
                    FROM world_photo_index_state
                    WHERE singleton = 1 AND current_root_id = @rootId;", connection, transaction))
                {
                    generationCommand.Parameters.AddWithValue("@rootId", rootId);
                    currentGeneration = Convert.ToInt64(generationCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
                }
                var results = new List<WorldPhotoIndexRecord>(writes.Count);
                foreach (var write in writes)
                {
                    var canonicalPath = Path.GetFullPath(write.FilePath);
                    var pathKey = CreatePathKey(canonicalPath);
                    var worldId = write.ParseState == WorldPhotoParseState.Valid && !string.IsNullOrWhiteSpace(write.WorldId)
                        ? write.WorldId
                        : null;
                    if (write.ParseState == WorldPhotoParseState.Valid && worldId == null)
                        throw new ArgumentException("A valid indexed photo requires an exact world ID.", nameof(writes));
                    EnsureCurrentRoot(connection, transaction, rootId, canonicalPath);

                    var existing = FindByPath(connection, transaction, rootId, pathKey);
                    var publicToken = existing != null && existing.Fingerprint == write.Fingerprint
                        ? existing.PublicToken
                        : CreatePublicToken();
                    var capturedAt = write.CapturedAt == default ? new DateTimeOffset(write.Fingerprint.LastWriteUtcTicks, TimeSpan.Zero) : write.CapturedAt.ToUniversalTime();

                    using var command = new SQLiteCommand(@"
                    INSERT INTO world_photos (
                        public_token, root_id, file_path, path_key, world_id, captured_at_utc,
                        last_write_utc, file_size, parse_state, last_error_code, seen_generation,
                        retry_after_utc, thumbnail_key, thumbnail_path, thumbnail_state)
                    VALUES (
                        @token, @rootId, @filePath, @pathKey, @worldId, @capturedAt,
                        @lastWrite, @fileSize, @parseState, @errorCode, @seenGeneration,
                        @retryAfter, NULL, NULL, @thumbnailState)
                    ON CONFLICT(root_id, path_key) DO UPDATE SET
                        public_token = excluded.public_token,
                        file_path = excluded.file_path,
                        world_id = excluded.world_id,
                        captured_at_utc = excluded.captured_at_utc,
                        last_write_utc = excluded.last_write_utc,
                        file_size = excluded.file_size,
                        parse_state = excluded.parse_state,
                        last_error_code = excluded.last_error_code,
                        seen_generation = MAX(world_photos.seen_generation, excluded.seen_generation),
                        retry_after_utc = excluded.retry_after_utc,
                        thumbnail_key = CASE WHEN world_photos.public_token = excluded.public_token THEN world_photos.thumbnail_key ELSE NULL END,
                        thumbnail_path = CASE WHEN world_photos.public_token = excluded.public_token THEN world_photos.thumbnail_path ELSE NULL END,
                        thumbnail_state = CASE WHEN world_photos.public_token = excluded.public_token THEN world_photos.thumbnail_state ELSE @thumbnailState END;", connection, transaction);
                    command.Parameters.AddWithValue("@token", publicToken);
                    command.Parameters.AddWithValue("@rootId", rootId);
                    command.Parameters.AddWithValue("@filePath", canonicalPath);
                    command.Parameters.AddWithValue("@pathKey", pathKey);
                    command.Parameters.AddWithValue("@worldId", (object?)worldId ?? DBNull.Value);
                    command.Parameters.AddWithValue("@capturedAt", capturedAt.UtcTicks);
                    command.Parameters.AddWithValue("@lastWrite", write.Fingerprint.LastWriteUtcTicks);
                    command.Parameters.AddWithValue("@fileSize", write.Fingerprint.FileSize);
                    command.Parameters.AddWithValue("@parseState", (int)write.ParseState);
                    command.Parameters.AddWithValue("@errorCode", (object?)write.ErrorCode ?? DBNull.Value);
                    command.Parameters.AddWithValue("@seenGeneration", Math.Max(write.SeenGeneration, currentGeneration));
                    command.Parameters.AddWithValue("@retryAfter", write.RetryAfter?.UtcTicks ?? (object)DBNull.Value);
                    command.Parameters.AddWithValue("@thumbnailState", (int)WorldPhotoThumbnailState.Missing);
                    command.ExecuteNonQuery();
                    results.Add(FindByPath(connection, transaction, rootId, pathKey)!);
                }
                transaction.Commit();
                return results;
            }
        }

        public int CompleteReconciliation(long rootId, long generation)
        {
            lock (_writerLock)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                EnsureCurrentRoot(connection, transaction, rootId, null);
                using var delete = new SQLiteCommand(@"
                    DELETE FROM world_photos
                    WHERE root_id = @rootId AND seen_generation < @generation;", connection, transaction);
                delete.Parameters.AddWithValue("@rootId", rootId);
                delete.Parameters.AddWithValue("@generation", generation);
                var removed = delete.ExecuteNonQuery();
                using var state = new SQLiteCommand(@"
                    UPDATE world_photo_index_state
                    SET lifecycle_state = 'idle', last_successful_reconcile_utc = @now
                    WHERE singleton = 1 AND current_root_id = @rootId AND generation_sequence = @generation;", connection, transaction);
                state.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.UtcTicks);
                state.Parameters.AddWithValue("@rootId", rootId);
                state.Parameters.AddWithValue("@generation", generation);
                if (state.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("Only the current reconciliation generation may remove stale rows.");
                transaction.Commit();
                return removed;
            }
        }

        public WorldPhotoPage GetWorldPhotos(string worldId, string? cursor, int limit)
        {
            if (string.IsNullOrWhiteSpace(worldId))
                return new WorldPhotoPage();

            var pageSize = Math.Clamp(limit, 1, MaximumPageSize);
            var decodedCursor = DecodeCursor(cursor);
            using var connection = OpenConnection();
            using var command = new SQLiteCommand(@"
                SELECT p.id, p.public_token, p.captured_at_utc, p.thumbnail_path, p.thumbnail_state
                FROM world_photos p
                INNER JOIN world_photo_roots r ON r.id = p.root_id AND r.is_current = 1
                WHERE p.parse_state = @validState
                  AND p.world_id = @worldId
                  AND (@cursorTicks IS NULL OR p.captured_at_utc < @cursorTicks
                       OR (p.captured_at_utc = @cursorTicks AND p.id < @cursorId))
                ORDER BY p.captured_at_utc DESC, p.id DESC
                LIMIT @limit;", connection);
            command.Parameters.AddWithValue("@validState", (int)WorldPhotoParseState.Valid);
            command.Parameters.AddWithValue("@worldId", worldId);
            command.Parameters.AddWithValue("@cursorTicks", decodedCursor?.CapturedAtTicks ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@cursorId", decodedCursor?.Id ?? 0);
            command.Parameters.AddWithValue("@limit", pageSize + 1);

            var rows = new List<(long Id, long CapturedAtTicks, WorldPhotoPageItem Item)>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((
                    reader.GetInt64(0),
                    reader.GetInt64(2),
                    new WorldPhotoPageItem
                    {
                        PublicToken = reader.GetString(1),
                        CapturedAt = new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero),
                        ThumbnailPath = reader.IsDBNull(3) ? null : reader.GetString(3),
                        ThumbnailState = (WorldPhotoThumbnailState)reader.GetInt32(4)
                    }));
            }

            var hasMore = rows.Count > pageSize;
            if (hasMore)
                rows.RemoveAt(rows.Count - 1);
            var items = new List<WorldPhotoPageItem>(rows.Count);
            foreach (var row in rows)
                items.Add(row.Item);
            var last = rows.Count == 0 ? default : rows[^1];
            return new WorldPhotoPage
            {
                Items = items,
                NextCursor = hasMore ? EncodeCursor(last.CapturedAtTicks, last.Id) : null
            };
        }

        public WorldPhotoIndexRecord? FindByPublicToken(string publicToken)
        {
            if (string.IsNullOrWhiteSpace(publicToken))
                return null;
            using var connection = OpenConnection();
            using var command = new SQLiteCommand(@"
                SELECT p.id, p.public_token, p.root_id, p.file_path, p.path_key, p.world_id,
                       p.captured_at_utc, p.last_write_utc, p.file_size, p.parse_state,
                       p.last_error_code, p.seen_generation, p.retry_after_utc, p.thumbnail_path, p.thumbnail_state
                FROM world_photos p
                INNER JOIN world_photo_roots r ON r.id = p.root_id AND r.is_current = 1
                WHERE p.public_token = @token AND p.parse_state = @validState
                LIMIT 1;", connection);
            command.Parameters.AddWithValue("@token", publicToken);
            command.Parameters.AddWithValue("@validState", (int)WorldPhotoParseState.Valid);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadRecord(reader) : null;
        }

        public WorldPhotoIndexRecord? FindByPath(long rootId, string filePath)
        {
            var pathKey = CreatePathKey(Path.GetFullPath(filePath));
            using var connection = OpenConnection();
            using var command = new SQLiteCommand(@"
                SELECT id, public_token, root_id, file_path, path_key, world_id, captured_at_utc,
                       last_write_utc, file_size, parse_state, last_error_code, seen_generation,
                       retry_after_utc, thumbnail_path, thumbnail_state
                FROM world_photos WHERE root_id = @rootId AND path_key = @pathKey LIMIT 1;", connection);
            command.Parameters.AddWithValue("@rootId", rootId);
            command.Parameters.AddWithValue("@pathKey", pathKey);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadRecord(reader) : null;
        }

        public IReadOnlyList<string> GetDueRetryPaths(long rootId, DateTimeOffset now, int limit)
        {
            var batchSize = Math.Clamp(limit, 1, MaximumPageSize);
            using var connection = OpenConnection();
            using var command = new SQLiteCommand(@"
                SELECT p.file_path
                FROM world_photos p
                INNER JOIN world_photo_roots r ON r.id = p.root_id AND r.is_current = 1
                WHERE p.root_id = @rootId
                  AND p.parse_state = @retryableState
                  AND p.retry_after_utc IS NOT NULL
                  AND p.retry_after_utc <= @now
                ORDER BY p.retry_after_utc, p.id
                LIMIT @limit;", connection);
            command.Parameters.AddWithValue("@rootId", rootId);
            command.Parameters.AddWithValue("@retryableState", (int)WorldPhotoParseState.Retryable);
            command.Parameters.AddWithValue("@now", now.UtcTicks);
            command.Parameters.AddWithValue("@limit", batchSize);
            var paths = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                paths.Add(reader.GetString(0));
            return paths;
        }

        public bool UpdateThumbnail(string publicToken, WorldPhotoThumbnailState state, string? thumbnailKey, string? thumbnailPath)
        {
            lock (_writerLock)
            {
                using var connection = OpenConnection();
                using var command = new SQLiteCommand(@"
                    UPDATE world_photos
                    SET thumbnail_state = @state, thumbnail_key = @key, thumbnail_path = @path
                    WHERE public_token = @token
                      AND root_id = (SELECT id FROM world_photo_roots WHERE is_current = 1);", connection);
                command.Parameters.AddWithValue("@state", (int)state);
                command.Parameters.AddWithValue("@key", (object?)thumbnailKey ?? DBNull.Value);
                command.Parameters.AddWithValue("@path", (object?)thumbnailPath ?? DBNull.Value);
                command.Parameters.AddWithValue("@token", publicToken);
                return command.ExecuteNonQuery() == 1;
            }
        }

        public WorldPhotoIndexStatistics GetStatistics()
        {
            using var connection = OpenConnection();
            using var command = new SQLiteCommand(@"
                SELECT COUNT(*),
                       SUM(CASE WHEN p.parse_state = 1 THEN 1 ELSE 0 END),
                       SUM(CASE WHEN p.parse_state = 2 THEN 1 ELSE 0 END),
                       SUM(CASE WHEN p.parse_state = 3 THEN 1 ELSE 0 END),
                       SUM(CASE WHEN p.parse_state = 4 THEN 1 ELSE 0 END)
                FROM world_photos p
                INNER JOIN world_photo_roots r ON r.id = p.root_id AND r.is_current = 1;", connection);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return new WorldPhotoIndexStatistics();
            return new WorldPhotoIndexStatistics
            {
                Total = reader.GetInt64(0),
                Valid = reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                Invalid = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                Retryable = reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                Error = reader.IsDBNull(4) ? 0 : reader.GetInt64(4)
            };
        }

        public void Reset()
        {
            lock (_writerLock)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                ExecuteNonQuery(connection, transaction, "UPDATE world_photo_index_state SET current_root_id = NULL, generation_sequence = 0, lifecycle_state = 'idle', last_successful_reconcile_utc = NULL WHERE singleton = 1; DELETE FROM world_photos; DELETE FROM world_photo_roots;");
                transaction.Commit();
            }
        }

        internal IReadOnlyList<string> GetWorldQueryPlan(string worldId)
        {
            using var connection = OpenConnection();
            using var command = new SQLiteCommand(@"
                EXPLAIN QUERY PLAN
                SELECT p.id FROM world_photos p
                INNER JOIN world_photo_roots r ON r.id = p.root_id AND r.is_current = 1
                WHERE p.parse_state = @validState AND p.world_id = @worldId
                ORDER BY p.captured_at_utc DESC, p.id DESC LIMIT 50;", connection);
            command.Parameters.AddWithValue("@validState", (int)WorldPhotoParseState.Valid);
            command.Parameters.AddWithValue("@worldId", worldId);
            var result = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                result.Add(reader.GetString(3));
            return result;
        }

        public static string CanonicalizeDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A photo root is required.", nameof(path));
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public static string CreatePathKey(string path)
        {
            var fullPath = Path.GetFullPath(path).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            return OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
        }

        private void InitializeWithRecovery()
        {
            try
            {
                Initialize();
            }
            catch (SQLiteException)
            {
                SQLiteConnection.ClearAllPools();
                QuarantineDatabase();
                Initialize();
            }
        }

        private void Initialize()
        {
            using var connection = OpenConnection();
            ExecuteNonQuery(connection, null, "PRAGMA journal_mode=WAL;");
            ExecuteNonQuery(connection, null, @"
                CREATE TABLE IF NOT EXISTS world_photo_roots (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    canonical_path TEXT NOT NULL,
                    path_key TEXT NOT NULL UNIQUE,
                    is_current INTEGER NOT NULL DEFAULT 0,
                    created_at_utc INTEGER NOT NULL,
                    last_seen_at_utc INTEGER NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ux_world_photo_current_root
                    ON world_photo_roots(is_current) WHERE is_current = 1;
                CREATE TABLE IF NOT EXISTS world_photos (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    public_token TEXT NOT NULL UNIQUE,
                    root_id INTEGER NOT NULL,
                    file_path TEXT NOT NULL,
                    path_key TEXT NOT NULL,
                    world_id TEXT NULL,
                    captured_at_utc INTEGER NOT NULL,
                    last_write_utc INTEGER NOT NULL,
                    file_size INTEGER NOT NULL,
                    parse_state INTEGER NOT NULL,
                    last_error_code TEXT NULL,
                    seen_generation INTEGER NOT NULL DEFAULT 0,
                    retry_after_utc INTEGER NULL,
                    thumbnail_key TEXT NULL,
                    thumbnail_path TEXT NULL,
                    thumbnail_state INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY(root_id) REFERENCES world_photo_roots(id) ON DELETE CASCADE,
                    UNIQUE(root_id, path_key)
                );
                CREATE INDEX IF NOT EXISTS ix_world_photos_world_page
                    ON world_photos(root_id, world_id, captured_at_utc DESC, id DESC)
                    WHERE parse_state = 1;
                CREATE INDEX IF NOT EXISTS ix_world_photos_seen_generation
                    ON world_photos(root_id, seen_generation);
                CREATE TABLE IF NOT EXISTS world_photo_index_state (
                    singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
                    schema_version INTEGER NOT NULL,
                    current_root_id INTEGER NULL,
                    generation_sequence INTEGER NOT NULL DEFAULT 0,
                    lifecycle_state TEXT NOT NULL DEFAULT 'idle',
                    last_successful_reconcile_utc INTEGER NULL,
                    FOREIGN KEY(current_root_id) REFERENCES world_photo_roots(id)
                );
                INSERT INTO world_photo_index_state (singleton, schema_version, generation_sequence, lifecycle_state)
                    VALUES (1, 1, 0, 'idle') ON CONFLICT(singleton) DO NOTHING;");

            using var versionCommand = new SQLiteCommand("SELECT schema_version FROM world_photo_index_state WHERE singleton = 1;", connection);
            var storedVersion = Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (storedVersion > SchemaVersion)
                throw new InvalidOperationException($"World photo index schema {storedVersion} is newer than supported schema {SchemaVersion}.");
        }

        private SQLiteConnection OpenConnection()
        {
            var connection = new SQLiteConnection($"Data Source=\"{_databasePath}\";Version=3;Default Timeout=5;Pooling=True;", true);
            try
            {
                connection.Open();
                ExecuteNonQuery(connection, null, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;");
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        private static void ExecuteNonQuery(SQLiteConnection connection, SQLiteTransaction? transaction, string sql)
        {
            using var command = new SQLiteCommand(sql, connection, transaction);
            command.ExecuteNonQuery();
        }

        private static void EnsureCurrentRoot(SQLiteConnection connection, SQLiteTransaction transaction, long rootId, string? candidatePath)
        {
            using var command = new SQLiteCommand("SELECT canonical_path FROM world_photo_roots WHERE id = @rootId AND is_current = 1;", connection, transaction);
            command.Parameters.AddWithValue("@rootId", rootId);
            var currentPath = command.ExecuteScalar() as string;
            if (currentPath == null)
                throw new InvalidOperationException("The requested photo root is not current.");
            if (candidatePath != null && !IsContainedBy(currentPath, candidatePath))
                throw new InvalidOperationException("The photo path is outside the current root.");
        }

        public static bool IsContainedBy(string rootPath, string candidatePath)
        {
            var root = CanonicalizeDirectory(rootPath);
            var candidate = Path.GetFullPath(candidatePath);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(root, candidate, comparison))
                return true;
            var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            return candidate.StartsWith(prefix, comparison);
        }

        private static WorldPhotoIndexRecord? FindByPath(SQLiteConnection connection, SQLiteTransaction transaction, long rootId, string pathKey)
        {
            using var command = new SQLiteCommand(@"
                SELECT id, public_token, root_id, file_path, path_key, world_id, captured_at_utc,
                       last_write_utc, file_size, parse_state, last_error_code, seen_generation,
                       retry_after_utc, thumbnail_path, thumbnail_state
                FROM world_photos WHERE root_id = @rootId AND path_key = @pathKey LIMIT 1;", connection, transaction);
            command.Parameters.AddWithValue("@rootId", rootId);
            command.Parameters.AddWithValue("@pathKey", pathKey);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadRecord(reader) : null;
        }

        private static WorldPhotoIndexRecord ReadRecord(SQLiteDataReader reader)
        {
            return new WorldPhotoIndexRecord
            {
                Id = reader.GetInt64(0),
                PublicToken = reader.GetString(1),
                RootId = reader.GetInt64(2),
                FilePath = reader.GetString(3),
                PathKey = reader.GetString(4),
                WorldId = reader.IsDBNull(5) ? null : reader.GetString(5),
                CapturedAt = new DateTimeOffset(reader.GetInt64(6), TimeSpan.Zero),
                Fingerprint = new WorldPhotoFingerprint(reader.GetInt64(7), reader.GetInt64(8)),
                ParseState = (WorldPhotoParseState)reader.GetInt32(9),
                LastErrorCode = reader.IsDBNull(10) ? null : reader.GetString(10),
                SeenGeneration = reader.GetInt64(11),
                RetryAfter = reader.IsDBNull(12) ? null : new DateTimeOffset(reader.GetInt64(12), TimeSpan.Zero),
                ThumbnailPath = reader.IsDBNull(13) ? null : reader.GetString(13),
                ThumbnailState = (WorldPhotoThumbnailState)reader.GetInt32(14)
            };
        }

        private static string CreatePublicToken()
        {
            return Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        }

        private static string EncodeCursor(long capturedAtTicks, long id)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{capturedAtTicks.ToString(CultureInfo.InvariantCulture)}:{id.ToString(CultureInfo.InvariantCulture)}"));
        }

        private static (long CapturedAtTicks, long Id)? DecodeCursor(string? cursor)
        {
            if (string.IsNullOrEmpty(cursor))
                return null;
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
                var separator = decoded.IndexOf(':');
                if (separator <= 0 || separator == decoded.Length - 1)
                    throw new FormatException();
                return (
                    long.Parse(decoded.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture),
                    long.Parse(decoded.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture));
            }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            {
                throw new ArgumentException("The world photo cursor is invalid.", nameof(cursor), exception);
            }
        }

        private void QuarantineDatabase()
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var source = _databasePath + suffix;
                if (!File.Exists(source))
                    continue;
                var destination = $"{source}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
                File.Move(source, destination);
            }
        }
    }
}
