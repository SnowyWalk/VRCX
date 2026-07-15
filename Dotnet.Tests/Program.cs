using System.Data.SQLite;
using System.Diagnostics;
using VRCX;

var tests = new (string Name, Action Body)[]
{
    ("schema and legacy isolation", SchemaAndLegacyIsolation),
    ("corruption recovery", CorruptionRecovery),
    ("exact world paging", ExactWorldPaging),
    ("token rotation and root filtering", TokenRotationAndRootFiltering),
    ("generation cleanup authority", GenerationCleanupAuthority),
    ("batch writer atomicity", BatchWriterAtomicity),
    ("concurrent read write availability", () => ConcurrentReadWriteAvailability().GetAwaiter().GetResult()),
    ("production shell launcher adapter", ProductionShellLauncherAdapter),
    ("service reconciliation and secure capabilities", () => ServiceReconciliationAndCapabilities().GetAwaiter().GetResult()),
    ("failed reconciliation preserves stale rows", () => FailedReconciliationPreservesStaleRows().GetAwaiter().GetResult()),
    ("retry and startup recovery", () => RetryAndStartupRecovery().GetAwaiter().GetResult()),
    ("stable write retry", () => StableWriteRetry().GetAwaiter().GetResult()),
    ("thumbnail queue and restart recovery", () => ThumbnailQueueAndRestartRecovery().GetAwaiter().GetResult()),
    ("rebuild failure recovery", () => RebuildFailureRecovery().GetAwaiter().GetResult()),
    ("root switch serializes opener", () => RootSwitchSerializesOpener().GetAwaiter().GetResult()),
    ("hostile path policy", HostilePathPolicy),
    ("100k indexed query plan", HundredThousandRowQueryPlan)
};

foreach (var test in tests)
{
    test.Body();
    Console.WriteLine($"PASS {test.Name}");
}

return;

static void SchemaAndLegacyIsolation()
{
    using var fixture = new Fixture();
    var legacyPath = Path.Combine(fixture.Directory, "metadataCache.db");
    using (var legacy = new SQLiteConnection($"Data Source={legacyPath};Version=3;"))
    {
        legacy.Open();
        using var command = new SQLiteCommand("CREATE TABLE cache(id INTEGER PRIMARY KEY, file_path TEXT NOT NULL, metadata TEXT); INSERT INTO cache VALUES(1, 'photo.png', 'sentinel');", legacy);
        command.ExecuteNonQuery();
    }

    var before = ReadLegacy(legacyPath);
    _ = fixture.Database;
    var after = ReadLegacy(legacyPath);
    Assert(before == after, "Creating the dedicated index changed the legacy cache.");
    Assert(File.Exists(Path.Combine(fixture.Directory, "worldPhotoIndex.db")), "Dedicated database was not created.");

    using (var index = new SQLiteConnection($"Data Source={fixture.Database.DatabasePath};Version=3;"))
    {
        index.Open();
        using var version = new SQLiteCommand("SELECT schema_version FROM world_photo_index_state WHERE singleton = 1; PRAGMA user_version;", index);
        using var reader = version.ExecuteReader();
        Assert(reader.Read() && reader.GetInt32(0) == 1, "Dedicated schema version was not recorded.");
        Assert(reader.NextResult() && reader.Read() && reader.GetInt32(0) == 0, "Schema version has two competing authorities.");
    }

    if (OperatingSystem.IsWindows())
    {
        var sameRoot = fixture.Database.SetCurrentRoot(fixture.PhotoRoot.ToUpperInvariant());
        var originalRoot = fixture.Database.SetCurrentRoot(fixture.PhotoRoot);
        Assert(sameRoot.Id == originalRoot.Id, "Windows root identity is not case-insensitive.");
    }
}

static void CorruptionRecovery()
{
    var directory = Path.Combine(Path.GetTempPath(), "vrcx-world-photo-index-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var databasePath = Path.Combine(directory, "worldPhotoIndex.db");
    File.WriteAllBytes(databasePath, "not a sqlite database"u8.ToArray());
    try
    {
        var database = new WorldPhotoIndexDatabase(databasePath);
        var rootPath = Path.Combine(directory, "photos");
        Directory.CreateDirectory(rootPath);
        Assert(database.SetCurrentRoot(rootPath).IsCurrent, "Recovered database is not usable.");
        Assert(Directory.EnumerateFiles(directory, "worldPhotoIndex.db.corrupt-*").Any(), "Corrupt database was not quarantined.");
    }
    finally
    {
        SQLiteConnection.ClearAllPools();
        Directory.Delete(directory, true);
    }
}

static void ExactWorldPaging()
{
    using var fixture = new Fixture();
    var root = fixture.Database.SetCurrentRoot(fixture.PhotoRoot);
    var generation = fixture.Database.BeginReconciliation(root.Id);
    fixture.Upsert(root.Id, "one.png", "wrld_exact", 30, generation);
    fixture.Upsert(root.Id, "two.png", "wrld_exact_more", 20, generation);
    fixture.Upsert(root.Id, "three.png", "wrld_exact", 10, generation);
    fixture.Upsert(root.Id, "missing.png", null, 40, generation, WorldPhotoParseState.NoMetadata);

    var first = fixture.Database.GetWorldPhotos("wrld_exact", null, 1);
    Assert(first.Items.Count == 1 && first.Items[0].CapturedAt.UtcTicks == 30, "First exact page is wrong.");
    Assert(first.NextCursor != null, "Expected a next cursor.");
    var second = fixture.Database.GetWorldPhotos("wrld_exact", first.NextCursor, 1);
    Assert(second.Items.Count == 1 && second.Items[0].CapturedAt.UtcTicks == 10, "Second exact page is wrong.");
    Assert(second.NextCursor == null, "Unexpected trailing cursor.");
    Assert(fixture.Database.GetWorldPhotos("wrld_exact_more", null, 10).Items.Count == 1, "Similar world ID was not independently indexed.");
    Expect<ArgumentException>(() => fixture.Database.GetWorldPhotos("wrld_exact", "not-a-cursor", 10));
}

static void TokenRotationAndRootFiltering()
{
    using var fixture = new Fixture();
    var rootA = fixture.Database.SetCurrentRoot(fixture.PhotoRoot);
    var first = fixture.Upsert(rootA.Id, "replace.png", "wrld_a", 10, 1);
    Assert(first.PublicToken.Length == 32, "Public token is not 128-bit hex.");
    var unchanged = fixture.Upsert(rootA.Id, "replace.png", "wrld_a", 10, 2);
    Assert(first.PublicToken == unchanged.PublicToken, "Unchanged content rotated its token.");
    var changed = fixture.Upsert(rootA.Id, "replace.png", "wrld_a", 11, 3);
    Assert(first.PublicToken != changed.PublicToken, "Changed content did not rotate its token.");
    Assert(fixture.Database.FindByPublicToken(first.PublicToken) == null, "Stale token remained usable.");

    var otherRoot = Path.Combine(fixture.Directory, "other");
    Directory.CreateDirectory(otherRoot);
    fixture.Database.SetCurrentRoot(otherRoot);
    Assert(fixture.Database.GetWorldPhotos("wrld_a", null, 10).Items.Count == 0, "Prior-root rows remained visible.");
    Assert(fixture.Database.FindByPublicToken(changed.PublicToken) == null, "Prior-root token remained usable.");
}

static void GenerationCleanupAuthority()
{
    using var fixture = new Fixture();
    var root = fixture.Database.SetCurrentRoot(fixture.PhotoRoot);
    var firstGeneration = fixture.Database.BeginReconciliation(root.Id);
    fixture.Upsert(root.Id, "old.png", "wrld_a", 10, firstGeneration);
    var secondGeneration = fixture.Database.BeginReconciliation(root.Id);
    fixture.Upsert(root.Id, "kept.png", "wrld_a", 20, secondGeneration);
    var hintUpdate = fixture.Upsert(root.Id, "kept.png", "wrld_a", 20, 0);
    var hintInsert = fixture.Upsert(root.Id, "hint.png", "wrld_a", 30, 0);
    Assert(hintUpdate.SeenGeneration == secondGeneration, "A hint reset a reconciliation generation.");
    Assert(hintInsert.SeenGeneration == secondGeneration, "A hint inserted during reconciliation was not protected by the active generation.");
    var removed = fixture.Database.CompleteReconciliation(root.Id, secondGeneration);
    Assert(removed == 1, "Generation cleanup did not remove exactly the stale row.");
    Assert(fixture.Database.GetWorldPhotos("wrld_a", null, 10).Items.Count == 2, "Generation cleanup removed a current hint row.");

    var otherRoot = Path.Combine(fixture.Directory, "new-root");
    Directory.CreateDirectory(otherRoot);
    fixture.Database.SetCurrentRoot(otherRoot);
    Expect<InvalidOperationException>(() => fixture.Database.CompleteReconciliation(root.Id, secondGeneration));
}

static void ProductionShellLauncherAdapter()
{
    var canonicalPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "adapter.png"));
    var startInfo = ShellWorldPhotoLauncher.CreateStartInfo(canonicalPath);
    Assert(startInfo.FileName == canonicalPath, "Production launcher changed the canonical path.");
    Assert(startInfo.UseShellExecute, "Production launcher does not use the OS default application.");
    Assert(string.IsNullOrEmpty(startInfo.Arguments), "Production launcher added shell arguments.");
}

static void BatchWriterAtomicity()
{
    using var fixture = new Fixture();
    var root = fixture.Database.SetCurrentRoot(fixture.PhotoRoot);
    var generation = fixture.Database.BeginReconciliation(root.Id);
    var first = new WorldPhotoIndexWrite
    {
        FilePath = Path.Combine(fixture.PhotoRoot, "first.png"),
        Fingerprint = new WorldPhotoFingerprint(1, 1),
        ParseState = WorldPhotoParseState.Valid,
        WorldId = "wrld_batch",
        CapturedAt = DateTimeOffset.UtcNow,
        SeenGeneration = generation
    };
    var invalid = new WorldPhotoIndexWrite
    {
        FilePath = Path.Combine(fixture.PhotoRoot, "invalid.png"),
        Fingerprint = new WorldPhotoFingerprint(2, 2),
        ParseState = WorldPhotoParseState.Valid,
        CapturedAt = DateTimeOffset.UtcNow,
        SeenGeneration = generation
    };

    Expect<ArgumentException>(() => fixture.Database.UpsertBatch(root.Id, [first, invalid]));
    Assert(fixture.Database.FindByPath(root.Id, first.FilePath) == null, "A failed batch left a partial commit.");

    var writes = Enumerable.Range(0, 128).Select(index => new WorldPhotoIndexWrite
    {
        FilePath = Path.Combine(fixture.PhotoRoot, $"batch-{index}.png"),
        Fingerprint = new WorldPhotoFingerprint(index + 10, index + 10),
        ParseState = WorldPhotoParseState.Valid,
        WorldId = "wrld_batch",
        CapturedAt = DateTimeOffset.UtcNow.AddSeconds(-index),
        SeenGeneration = generation
    }).ToArray();
    Assert(fixture.Database.UpsertBatch(root.Id, writes).Count == writes.Length, "The serialized batch writer lost rows.");
}

static async Task ConcurrentReadWriteAvailability()
{
    using var fixture = new Fixture();
    var root = fixture.Database.SetCurrentRoot(fixture.PhotoRoot);
    var generation = fixture.Database.BeginReconciliation(root.Id);
    var failures = new List<Exception>();
    var failureLock = new object();

    var writer = Task.Run(() =>
    {
        try
        {
            for (var batch = 0; batch < 200; batch++)
            {
                var writes = Enumerable.Range(0, 8).Select(offset =>
                {
                    var index = batch * 8 + offset;
                    return new WorldPhotoIndexWrite
                    {
                        FilePath = Path.Combine(fixture.PhotoRoot, $"concurrent-{index}.png"),
                        Fingerprint = new WorldPhotoFingerprint(index + 1, index + 1),
                        ParseState = WorldPhotoParseState.Valid,
                        WorldId = "wrld_concurrent",
                        CapturedAt = DateTimeOffset.UtcNow.AddTicks(-index),
                        SeenGeneration = generation
                    };
                }).ToArray();
                fixture.Database.UpsertBatch(root.Id, writes);
            }
        }
        catch (Exception exception)
        {
            lock (failureLock)
                failures.Add(exception);
        }
    });

    var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
    {
        try
        {
            for (var iteration = 0; iteration < 500; iteration++)
            {
                fixture.Database.GetWorldPhotos("wrld_concurrent", null, 50);
                fixture.Database.GetStatistics();
                fixture.Database.GetCurrentRoot();
            }
        }
        catch (Exception exception)
        {
            lock (failureLock)
                failures.Add(exception);
        }
    })).ToArray();

    await Task.WhenAll(readers.Append(writer));
    Assert(failures.Count == 0, $"Concurrent DB access surfaced {failures.FirstOrDefault()?.GetType().Name}: {failures.FirstOrDefault()?.Message}");
    Assert(fixture.Database.GetWorldPhotos("wrld_concurrent", null, 100).Items.Count == 100, "Concurrent writer did not commit the expected rows.");
}

static void HundredThousandRowQueryPlan()
{
    using var fixture = new Fixture();
    var root = fixture.Database.SetCurrentRoot(fixture.PhotoRoot);
    using (var connection = new SQLiteConnection($"Data Source={fixture.Database.DatabasePath};Version=3;"))
    {
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = new SQLiteCommand(@"
            INSERT INTO world_photos(public_token, root_id, file_path, path_key, world_id, captured_at_utc,
                last_write_utc, file_size, parse_state, seen_generation, thumbnail_state)
            VALUES(@token, @rootId, @path, @pathKey, @worldId, @captured, @captured, 1, 1, 1, 0);", connection, transaction);
        var token = command.Parameters.Add("@token", System.Data.DbType.String);
        command.Parameters.AddWithValue("@rootId", root.Id);
        var path = command.Parameters.Add("@path", System.Data.DbType.String);
        var pathKey = command.Parameters.Add("@pathKey", System.Data.DbType.String);
        var worldId = command.Parameters.Add("@worldId", System.Data.DbType.String);
        var captured = command.Parameters.Add("@captured", System.Data.DbType.Int64);
        for (var i = 0; i < 100_000; i++)
        {
            token.Value = Guid.NewGuid().ToString("N");
            path.Value = Path.Combine(fixture.PhotoRoot, $"seed-{i}.png");
            pathKey.Value = WorldPhotoIndexDatabase.CreatePathKey((string)path.Value);
            worldId.Value = i % 100 == 0 ? "wrld_dense" : $"wrld_{i % 1000}";
            captured.Value = i;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        using var analyze = new SQLiteCommand("ANALYZE;", connection);
        analyze.ExecuteNonQuery();
    }

    var plan = string.Join(" | ", fixture.Database.GetWorldQueryPlan("wrld_dense"));
    Assert(plan.Contains("ix_world_photos_world_page", StringComparison.Ordinal), $"Expected page index, got: {plan}");
    for (var i = 0; i < 5; i++)
        fixture.Database.GetWorldPhotos("wrld_dense", null, 50);
    var samples = new List<double>();
    for (var i = 0; i < 25; i++)
    {
        var stopwatch = Stopwatch.StartNew();
        var page = fixture.Database.GetWorldPhotos("wrld_dense", null, 50);
        stopwatch.Stop();
        Assert(page.Items.Count == 50, "Dense benchmark page count is wrong.");
        samples.Add(stopwatch.Elapsed.TotalMilliseconds);
    }
    samples.Sort();
    var p95 = samples[(int)Math.Ceiling(samples.Count * 0.95) - 1];
    Console.WriteLine($"INFO 100k query p95={p95:F2}ms");
    Assert(p95 < 500, $"100k query p95 exceeded 500ms: {p95:F2}ms");
}

static async Task ServiceReconciliationAndCapabilities()
{
    using var fixture = new Fixture();
    var exactPath = Path.Combine(fixture.PhotoRoot, "exact.png");
    var otherPath = Path.Combine(fixture.PhotoRoot, "other.png");
    var missingPath = Path.Combine(fixture.PhotoRoot, "missing.png");
    File.WriteAllBytes(exactPath, [1, 2, 3]);
    File.WriteAllBytes(otherPath, [4, 5, 6]);
    File.WriteAllBytes(missingPath, [7, 8, 9]);
    var reader = new FakeMetadataReader(new Dictionary<string, WorldPhotoMetadataReadResult>(StringComparer.OrdinalIgnoreCase)
    {
        [exactPath] = new() { State = WorldPhotoParseState.Valid, WorldId = "wrld_exact", CapturedAt = DateTimeOffset.UtcNow },
        [otherPath] = new() { State = WorldPhotoParseState.Valid, WorldId = "wrld_other", CapturedAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
        [missingPath] = new() { State = WorldPhotoParseState.NoMetadata, ErrorCode = "missing_metadata" }
    });
    var launcher = new RecordingLauncher();
    var thumbnails = new FakeThumbnailStore(Path.Combine(fixture.Directory, "thumbs"));
    using var service = new WorldPhotoIndexService(
        fixture.Database,
        reader,
        thumbnails,
        launcher,
        new WorldPhotoIndexServiceOptions
        {
            EnableFileSystemWatcher = false,
            PeriodicReconciliationInterval = TimeSpan.FromHours(1),
            StableFileDelay = TimeSpan.FromMilliseconds(10),
            StopTimeout = TimeSpan.FromSeconds(1)
        },
        (message, exception) => Console.WriteLine($"SERVICE {message} {exception}"));

    service.Start(fixture.PhotoRoot);
    service.Start(fixture.PhotoRoot);
    await service.ReconcileNowAsync();
    var exact = service.GetWorldPhotos("wrld_exact", null, 50);
    Assert(exact.Items.Count == 1, "Exact metadata photo was not indexed.");
    Assert(service.GetWorldPhotos("wrld_exact_more", null, 50).Items.Count == 0, "World matching was not exact.");
    Assert(service.GetStatus().Counts.Invalid == 1, "Missing metadata was not cached as excluded.");

    var directPath = Path.Combine(fixture.PhotoRoot, "direct.png");
    reader.Results[directPath] = new WorldPhotoMetadataReadResult { State = WorldPhotoParseState.Valid, WorldId = "wrld_direct" };
    File.WriteAllBytes(directPath, [10, 11, 12]);
    service.EnqueuePath(directPath);
    await WaitUntil(() => service.GetWorldPhotos("wrld_direct", null, 50).Items.Count == 1, TimeSpan.FromSeconds(3));

    var token = exact.Items[0].PublicToken;
    Assert(service.OpenIndexedPhoto(token), "Valid token did not open.");
    Assert(launcher.Paths.Count == 1 && launcher.Paths[0] == Path.GetFullPath(exactPath), "Launcher did not receive the canonical path exactly once.");
    Assert(!service.OpenIndexedPhoto("not-a-token") && launcher.Paths.Count == 1, "Unknown token reached the launcher.");
    File.AppendAllText(exactPath, "changed");
    Assert(!service.OpenIndexedPhoto(token) && launcher.Paths.Count == 1, "Changed fingerprint reached the launcher.");

    var currentToken = service.GetWorldPhotos("wrld_direct", null, 50).Items[0].PublicToken;
    var requested = service.RequestThumbnails([currentToken]);
    Assert(requested.Count == 1 && requested[0].Status == "accepted", "Thumbnail request was not accepted.");
    await WaitUntil(() => fixture.Database.FindByPublicToken(currentToken)?.ThumbnailState == WorldPhotoThumbnailState.Ready, TimeSpan.FromSeconds(10));
    var ready = service.RequestThumbnails([currentToken]);
    Assert(ready[0].Status == "ready" && File.Exists(ready[0].ThumbnailPath), "Ready thumbnail was not returned.");
    Assert(service.RequestThumbnails(["invalid"])[0].Status == "error", "Invalid thumbnail token was accepted.");

    service.Rebuild();
    Assert(service.GetStatus().RebuildInProgress, "Rebuild status was not exposed while reconciliation was pending.");
    await WaitUntil(() => !service.GetStatus().RebuildInProgress, TimeSpan.FromSeconds(3));
    Assert(!service.OpenIndexedPhoto(currentToken), "Rebuild did not rotate the public token.");
    Assert(service.GetWorldPhotos("wrld_exact", null, 50).Items.Count == 1, "Rebuild did not restore indexed results.");

    File.Delete(directPath);
    service.EnqueuePath(directPath);
    await WaitUntil(() => service.GetWorldPhotos("wrld_direct", null, 50).Items.Count == 0, TimeSpan.FromSeconds(3));

    var rootB = Path.Combine(fixture.Directory, "root-b");
    Directory.CreateDirectory(rootB);
    var rootBPhoto = Path.Combine(rootB, "b.png");
    File.WriteAllBytes(rootBPhoto, [1]);
    reader.Results[rootBPhoto] = new WorldPhotoMetadataReadResult { State = WorldPhotoParseState.Valid, WorldId = "wrld_b" };
    service.EnsureRoot(rootB);
    await service.ReconcileNowAsync();
    Assert(service.GetWorldPhotos("wrld_exact", null, 50).Items.Count == 0, "Old root remained visible after switch.");
    Assert(!service.OpenIndexedPhoto(currentToken), "Old-root token remained usable after switch.");
    Assert(service.GetWorldPhotos("wrld_b", null, 50).Items.Count == 1, "New root did not reconcile.");

    service.EnsureRoot(fixture.PhotoRoot);
    await service.ReconcileNowAsync();
    Assert(!service.OpenIndexedPhoto(token) && !service.OpenIndexedPhoto(currentToken), "Returning to a root revived an old public token.");

    service.Stop();
    service.Stop();
    Assert(service.GetStatus().State == "stopped", "Idempotent stop failed.");
    Assert(service.GetStatus().CurrentRootIdentity is { Length: 64 }, "Status exposed no aggregate-safe root identity.");
}

static async Task FailedReconciliationPreservesStaleRows()
{
    using var fixture = new Fixture();
    var doomedPath = Path.Combine(fixture.PhotoRoot, "doomed.png");
    File.WriteAllBytes(doomedPath, [1]);
    var reader = new FakeMetadataReader(new Dictionary<string, WorldPhotoMetadataReadResult>(StringComparer.OrdinalIgnoreCase)
    {
        [doomedPath] = new() { State = WorldPhotoParseState.Valid, WorldId = "wrld_doomed" }
    });
    using var service = CreateService(fixture, reader, new FakeThumbnailStore(Path.Combine(fixture.Directory, "thumbs")), new RecordingLauncher());
    service.Start(fixture.PhotoRoot);
    await service.ReconcileNowAsync();
    Assert(service.GetWorldPhotos("wrld_doomed", null, 50).Items.Count == 1, "Failed-generation fixture was not indexed.");

    var blockerPath = Path.Combine(fixture.PhotoRoot, "blocker.png");
    File.WriteAllBytes(blockerPath, [2]);
    reader.ThrowingPaths.Add(blockerPath);
    File.Delete(doomedPath);
    service.EnqueuePath(doomedPath);
    await WaitUntil(() => service.GetStatus().State == "error", TimeSpan.FromSeconds(3));
    Assert(service.GetWorldPhotos("wrld_doomed", null, 50).Items.Count == 1, "A failed generation purged a stale row.");

    reader.ThrowingPaths.Remove(blockerPath);
    File.Delete(blockerPath);
    await service.ReconcileNowAsync();
    Assert(service.GetWorldPhotos("wrld_doomed", null, 50).Items.Count == 0, "A successful generation did not purge the stale row.");
}

static async Task RetryAndStartupRecovery()
{
    using var fixture = new Fixture();
    var path = Path.Combine(fixture.PhotoRoot, "retry.png");
    File.WriteAllBytes(path, [1, 2, 3]);
    var reader = new SequencedMetadataReader(path,
        new WorldPhotoMetadataReadResult { State = WorldPhotoParseState.Retryable, ErrorCode = "sharing_violation" },
        new WorldPhotoMetadataReadResult { State = WorldPhotoParseState.Valid, WorldId = "wrld_retry" });
    using var service = new WorldPhotoIndexService(
        fixture.Database,
        reader,
        new FakeThumbnailStore(Path.Combine(fixture.Directory, "thumbs")),
        new RecordingLauncher(),
        new WorldPhotoIndexServiceOptions
        {
            RetryDelay = TimeSpan.FromMilliseconds(150),
            RetryPollInterval = TimeSpan.FromMilliseconds(20),
            PeriodicReconciliationInterval = TimeSpan.FromHours(1),
            StableFileDelay = TimeSpan.FromMilliseconds(1),
            StopTimeout = TimeSpan.FromSeconds(1)
        });

    Expect<ArgumentException>(() => service.Start(""));
    Assert(!service.IsRunning, "A failed start left the service half-running.");
    service.Start(fixture.PhotoRoot);
    var root = fixture.Database.GetCurrentRoot()!;
    await WaitUntil(
        () => fixture.Database.FindByPath(root.Id, path)?.ParseState == WorldPhotoParseState.Retryable,
        TimeSpan.FromSeconds(3));
    await WaitUntil(() => service.GetWorldPhotos("wrld_retry", null, 50).Items.Count == 1, TimeSpan.FromSeconds(3));
    Assert(reader.ReadCount >= 2, "A due retryable fingerprint was not parsed again.");
}

static async Task StableWriteRetry()
{
    using var fixture = new Fixture();
    var path = Path.Combine(fixture.PhotoRoot, "growing.png");
    File.WriteAllBytes(path, [1]);
    var reader = new FakeMetadataReader(new Dictionary<string, WorldPhotoMetadataReadResult>(StringComparer.OrdinalIgnoreCase)
    {
        [path] = new() { State = WorldPhotoParseState.Valid, WorldId = "wrld_stable" }
    });
    using var service = new WorldPhotoIndexService(
        fixture.Database,
        reader,
        new FakeThumbnailStore(Path.Combine(fixture.Directory, "thumbs")),
        new RecordingLauncher(),
        new WorldPhotoIndexServiceOptions
        {
            EnableFileSystemWatcher = false,
            RetryDelay = TimeSpan.FromMilliseconds(500),
            RetryPollInterval = TimeSpan.FromMilliseconds(20),
            PeriodicReconciliationInterval = TimeSpan.FromHours(1),
            StableFileDelay = TimeSpan.FromMilliseconds(150),
            StopTimeout = TimeSpan.FromSeconds(1)
        });

    service.Start(fixture.PhotoRoot);
    await service.ReconcileNowAsync();
    var baselineReads = reader.ReadCount;
    service.EnqueuePath(path);
    await Task.Delay(40);
    File.AppendAllText(path, "still-writing");

    var root = fixture.Database.GetCurrentRoot()!;
    await WaitUntil(
        () => fixture.Database.FindByPath(root.Id, path) is { ParseState: WorldPhotoParseState.Retryable, LastErrorCode: "file_not_stable" },
        TimeSpan.FromSeconds(3));
    Assert(reader.ReadCount == baselineReads, "A file that changed during the stability window was parsed.");
    await WaitUntil(() => service.GetWorldPhotos("wrld_stable", null, 50).Items.Count == 1, TimeSpan.FromSeconds(3));
    Assert(reader.ReadCount == baselineReads + 1, "A stable due retry was not parsed exactly once.");
}

static async Task ThumbnailQueueAndRestartRecovery()
{
    using var fixture = new Fixture();
    var results = new Dictionary<string, WorldPhotoMetadataReadResult>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < 3; index++)
    {
        var path = Path.Combine(fixture.PhotoRoot, $"thumb-{index}.png");
        File.WriteAllBytes(path, [1, (byte)index]);
        results[path] = new WorldPhotoMetadataReadResult { State = WorldPhotoParseState.Valid, WorldId = "wrld_thumbs" };
    }
    var blocker = new BlockingThumbnailStore(Path.Combine(fixture.Directory, "blocked-thumbs"));
    using (var service = new WorldPhotoIndexService(
        fixture.Database,
        new FakeMetadataReader(results),
        blocker,
        new RecordingLauncher(),
        new WorldPhotoIndexServiceOptions
        {
            ThumbnailQueueCapacity = 1,
            PeriodicReconciliationInterval = TimeSpan.FromHours(1),
            RetryPollInterval = TimeSpan.FromHours(1),
            StableFileDelay = TimeSpan.FromMilliseconds(1),
            StopTimeout = TimeSpan.FromSeconds(1)
        }))
    {
        service.Start(fixture.PhotoRoot);
        await service.ReconcileNowAsync();
        var tokens = service.GetWorldPhotos("wrld_thumbs", null, 50).Items.Select(item => item.PublicToken).ToArray();
        Assert(tokens.Length == 3, "Thumbnail saturation fixture was not indexed.");
        Assert(service.RequestThumbnails([tokens[0]])[0].Status == "accepted", "First thumbnail was rejected.");
        Assert(blocker.Started.Wait(TimeSpan.FromSeconds(2)), "Thumbnail worker did not start.");
        var saturated = service.RequestThumbnails([tokens[1], tokens[2]]);
        Assert(saturated.Count(result => result.Status == "error") == 1, "A full thumbnail queue did not reject exactly one request.");
        Assert(service.GetStatus().ThumbnailQueueDepth <= 2, "Thumbnail queue depth exceeded worker plus bounded queue capacity.");
        blocker.Release.Set();
        await WaitUntil(() => service.RequestThumbnails([tokens[0]])[0].Status == "ready", TimeSpan.FromSeconds(3));

        var queuedToken = saturated.Single(result => result.Status == "error").PublicToken;
        fixture.Database.UpdateThumbnail(queuedToken, WorldPhotoThumbnailState.Queued, null, null);
        service.Stop();

        using var restarted = CreateService(
            fixture,
            new FakeMetadataReader(results),
            new FakeThumbnailStore(Path.Combine(fixture.Directory, "restart-thumbs")),
            new RecordingLauncher());
        restarted.Start(fixture.PhotoRoot);
        Assert(restarted.RequestThumbnails([queuedToken])[0].Status == "accepted", "A persisted queued thumbnail was not re-enqueued after restart.");
        await WaitUntil(() => restarted.RequestThumbnails([queuedToken])[0].Status == "ready", TimeSpan.FromSeconds(3));

        var missingCacheToken = tokens[0];
        fixture.Database.UpdateThumbnail(missingCacheToken, WorldPhotoThumbnailState.Ready, "missing", Path.Combine(fixture.Directory, "missing.jpg"));
        Assert(restarted.RequestThumbnails([missingCacheToken])[0].Status == "accepted", "A missing cached thumbnail path was not regenerated.");
        await WaitUntil(() => restarted.RequestThumbnails([missingCacheToken])[0].Status == "ready", TimeSpan.FromSeconds(3));

        var corruptPath = restarted.RequestThumbnails([missingCacheToken])[0].ThumbnailPath!;
        File.WriteAllBytes(corruptPath, []);
        Assert(restarted.RequestThumbnails([missingCacheToken])[0].Status == "accepted", "A corrupt cached thumbnail was returned as ready.");
        await WaitUntil(() => restarted.RequestThumbnails([missingCacheToken])[0].Status == "ready", TimeSpan.FromSeconds(3));
        Assert(new FileInfo(corruptPath).Length > 0, "A corrupt cached thumbnail was not regenerated.");
    }
}

static async Task RebuildFailureRecovery()
{
    using var fixture = new Fixture();
    var path = Path.Combine(fixture.PhotoRoot, "rebuild.png");
    File.WriteAllBytes(path, [1]);
    var reader = new FakeMetadataReader(new Dictionary<string, WorldPhotoMetadataReadResult>(StringComparer.OrdinalIgnoreCase)
    {
        [path] = new() { State = WorldPhotoParseState.Valid, WorldId = "wrld_rebuild" }
    });
    var thumbnails = new FailOnceClearThumbnailStore(Path.Combine(fixture.Directory, "thumbs"));
    using var service = CreateService(fixture, reader, thumbnails, new RecordingLauncher());
    service.Start(fixture.PhotoRoot);
    await service.ReconcileNowAsync();
    Assert(service.GetWorldPhotos("wrld_rebuild", null, 50).Items.Count == 1, "Rebuild fixture was not indexed.");

    service.Rebuild();
    await WaitUntil(() => !service.GetStatus().RebuildInProgress, TimeSpan.FromSeconds(3));
    Assert(service.IsRunning && service.GetStatus().State == "error", "A failed rebuild did not remain a nonfatal service error.");
    Assert(fixture.Database.GetCurrentRoot()?.CanonicalPath == Path.GetFullPath(fixture.PhotoRoot), "A failed rebuild did not restore current-root state.");

    service.Rebuild();
    await WaitUntil(() => !service.GetStatus().RebuildInProgress, TimeSpan.FromSeconds(3));
    Assert(service.GetWorldPhotos("wrld_rebuild", null, 50).Items.Count == 1, "The reconciliation consumer did not process a rebuild after failure.");
}

static async Task RootSwitchSerializesOpener()
{
    using var fixture = new Fixture();
    var path = Path.Combine(fixture.PhotoRoot, "open.png");
    File.WriteAllBytes(path, [1]);
    var reader = new FakeMetadataReader(new Dictionary<string, WorldPhotoMetadataReadResult>(StringComparer.OrdinalIgnoreCase)
    {
        [path] = new() { State = WorldPhotoParseState.Valid, WorldId = "wrld_open" }
    });
    var launcher = new BlockingLauncher();
    using var service = CreateService(fixture, reader, new FakeThumbnailStore(Path.Combine(fixture.Directory, "thumbs")), launcher);
    service.Start(fixture.PhotoRoot);
    await service.ReconcileNowAsync();
    var token = service.GetWorldPhotos("wrld_open", null, 50).Items.Single().PublicToken;

    var openTask = Task.Run(() => service.OpenIndexedPhoto(token));
    Assert(launcher.Entered.Wait(TimeSpan.FromSeconds(2)), "The opener did not reach the serialized launch boundary.");
    var rootB = Path.Combine(fixture.Directory, "root-b");
    Directory.CreateDirectory(rootB);
    var switchTask = Task.Run(() => service.EnsureRoot(rootB));
    await Task.Delay(100);
    Assert(!switchTask.IsCompleted, "Root switching crossed an in-flight validated launch.");
    launcher.Release.Set();
    Assert(await openTask, "The validated pre-switch open failed unexpectedly.");
    await switchTask;
    Assert(!service.OpenIndexedPhoto(token), "A prior-root token opened after root switching committed.");
    Assert(launcher.Paths.Count == 1, "The serialized opener launched more than once.");
}

static void HostilePathPolicy()
{
    using var fixture = new Fixture();
    var outside = Path.Combine(fixture.Directory, "outside.png");
    var inside = Path.Combine(fixture.PhotoRoot, "inside.png");
    File.WriteAllBytes(outside, [1]);
    File.WriteAllBytes(inside, [1]);
    Assert(WorldPhotoPathPolicy.IsSafeCurrentPng(fixture.PhotoRoot, inside), "A safe contained PNG was rejected.");
    Assert(!WorldPhotoPathPolicy.IsSafeCurrentPng(fixture.PhotoRoot, outside), "An outside-root PNG passed containment.");
    Assert(!WorldPhotoPathPolicy.IsSafeCurrentPng(fixture.PhotoRoot, Path.Combine(fixture.PhotoRoot, "missing.png")), "A missing PNG passed opener policy.");

    var link = Path.Combine(fixture.PhotoRoot, "escape.png");
    try
    {
        File.CreateSymbolicLink(link, outside);
        Assert(!WorldPhotoPathPolicy.IsSafeCurrentPng(fixture.PhotoRoot, link), "A reparse/symlink escape passed opener policy.");
    }
    catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
    {
        Console.WriteLine($"INFO symlink fixture unavailable: {exception.GetType().Name}");
    }
}

static WorldPhotoIndexService CreateService(
    Fixture fixture,
    IWorldPhotoMetadataReader reader,
    IWorldPhotoThumbnailStore thumbnails,
    IWorldPhotoLauncher launcher)
{
    return new WorldPhotoIndexService(
        fixture.Database,
        reader,
        thumbnails,
        launcher,
        new WorldPhotoIndexServiceOptions
        {
            EnableFileSystemWatcher = false,
            PeriodicReconciliationInterval = TimeSpan.FromHours(1),
            RetryPollInterval = TimeSpan.FromHours(1),
            StableFileDelay = TimeSpan.FromMilliseconds(1),
            StopTimeout = TimeSpan.FromSeconds(1)
        },
        (message, exception) => Console.WriteLine($"SERVICE {message} {exception}"));
}

static async Task WaitUntil(Func<bool> predicate, TimeSpan timeout)
{
    var started = Stopwatch.StartNew();
    while (!predicate())
    {
        if (started.Elapsed > timeout)
            throw new TimeoutException("Condition was not reached in time.");
        await Task.Delay(20);
    }
}

static string ReadLegacy(string path)
{
    using var connection = new SQLiteConnection($"Data Source={path};Version=3;");
    connection.Open();
    using var command = new SQLiteCommand("SELECT sql FROM sqlite_master WHERE type='table' ORDER BY name; SELECT id || '|' || file_path || '|' || metadata FROM cache ORDER BY id;", connection);
    using var reader = command.ExecuteReader();
    var values = new List<string>();
    do
    {
        while (reader.Read())
            values.Add(reader.GetValue(0)?.ToString() ?? "NULL");
    } while (reader.NextResult());
    return string.Join("\n", values);
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void Expect<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

sealed class Fixture : IDisposable
{
    public Fixture()
    {
        Directory = Path.Combine(Path.GetTempPath(), "vrcx-world-photo-index-tests", Guid.NewGuid().ToString("N"));
        PhotoRoot = Path.Combine(Directory, "photos");
        System.IO.Directory.CreateDirectory(PhotoRoot);
        Database = new WorldPhotoIndexDatabase(Path.Combine(Directory, "worldPhotoIndex.db"));
    }

    public string Directory { get; }
    public string PhotoRoot { get; }
    public WorldPhotoIndexDatabase Database { get; }

    public WorldPhotoIndexRecord Upsert(long rootId, string name, string? worldId, long ticks, long generation, WorldPhotoParseState state = WorldPhotoParseState.Valid)
    {
        return Database.Upsert(rootId, new WorldPhotoIndexWrite
        {
            FilePath = Path.Combine(PhotoRoot, name),
            Fingerprint = new WorldPhotoFingerprint(ticks, ticks),
            ParseState = state,
            WorldId = worldId,
            CapturedAt = new DateTimeOffset(ticks, TimeSpan.Zero),
            SeenGeneration = generation
        });
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, true);
        }
        catch
        {
        }
    }
}

sealed class FakeMetadataReader : IWorldPhotoMetadataReader
{
    public FakeMetadataReader(Dictionary<string, WorldPhotoMetadataReadResult> results)
    {
        Results = results;
    }

    public Dictionary<string, WorldPhotoMetadataReadResult> Results { get; }
    public HashSet<string> ThrowingPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int ReadCount { get; private set; }

    public WorldPhotoMetadataReadResult Read(string filePath)
    {
        ReadCount++;
        if (ThrowingPaths.Contains(filePath))
            throw new InvalidDataException("Injected metadata failure.");
        return Results.TryGetValue(filePath, out var result)
            ? result
            : new WorldPhotoMetadataReadResult { State = WorldPhotoParseState.NoMetadata, ErrorCode = "missing_metadata" };
    }
}

sealed class SequencedMetadataReader : IWorldPhotoMetadataReader
{
    private readonly string _path;
    private readonly Queue<WorldPhotoMetadataReadResult> _results;
    private readonly object _lock = new();

    public SequencedMetadataReader(string path, params WorldPhotoMetadataReadResult[] results)
    {
        _path = path;
        _results = new Queue<WorldPhotoMetadataReadResult>(results);
    }

    public int ReadCount { get; private set; }

    public WorldPhotoMetadataReadResult Read(string filePath)
    {
        lock (_lock)
        {
            ReadCount++;
            if (!string.Equals(filePath, _path, StringComparison.OrdinalIgnoreCase) || _results.Count == 0)
                return new WorldPhotoMetadataReadResult { State = WorldPhotoParseState.NoMetadata, ErrorCode = "missing_metadata" };
            return _results.Count == 1 ? _results.Peek() : _results.Dequeue();
        }
    }
}

sealed class RecordingLauncher : IWorldPhotoLauncher
{
    public List<string> Paths { get; } = [];

    public void Open(string canonicalFilePath)
    {
        Paths.Add(canonicalFilePath);
    }
}

sealed class BlockingLauncher : IWorldPhotoLauncher
{
    private readonly object _lock = new();
    public ManualResetEventSlim Entered { get; } = new(false);
    public ManualResetEventSlim Release { get; } = new(false);
    public List<string> Paths { get; } = [];

    public void Open(string canonicalFilePath)
    {
        Entered.Set();
        Release.Wait(TimeSpan.FromSeconds(3));
        lock (_lock)
            Paths.Add(canonicalFilePath);
    }
}

sealed class FakeThumbnailStore : IWorldPhotoThumbnailStore
{
    private readonly string _directory;

    public FakeThumbnailStore(string directory)
    {
        _directory = directory;
    }

    public Task<string> CreateAsync(WorldPhotoIndexRecord record, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, record.PublicToken + ".jpg");
        File.WriteAllBytes(path, [1]);
        return Task.FromResult(path);
    }

    public bool IsUsable(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    public void Clear()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }
}

sealed class BlockingThumbnailStore : IWorldPhotoThumbnailStore
{
    private readonly string _directory;
    public ManualResetEventSlim Started { get; } = new(false);
    public ManualResetEventSlim Release { get; } = new(false);

    public BlockingThumbnailStore(string directory)
    {
        _directory = directory;
    }

    public Task<string> CreateAsync(WorldPhotoIndexRecord record, CancellationToken cancellationToken)
    {
        Started.Set();
        Release.Wait(cancellationToken);
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, record.PublicToken + ".jpg");
        File.WriteAllBytes(path, [1]);
        return Task.FromResult(path);
    }

    public bool IsUsable(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    public void Clear()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }
}

sealed class FailOnceClearThumbnailStore : IWorldPhotoThumbnailStore
{
    private readonly FakeThumbnailStore _inner;
    private int _failuresRemaining = 1;

    public FailOnceClearThumbnailStore(string directory)
    {
        _inner = new FakeThumbnailStore(directory);
    }

    public Task<string> CreateAsync(WorldPhotoIndexRecord record, CancellationToken cancellationToken) =>
        _inner.CreateAsync(record, cancellationToken);

    public bool IsUsable(string path) => _inner.IsUsable(path);

    public void Clear()
    {
        if (Interlocked.Exchange(ref _failuresRemaining, 0) == 1)
            throw new IOException("Injected thumbnail clear failure.");
        _inner.Clear();
    }
}
