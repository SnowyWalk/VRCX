#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace VRCX
{
    public sealed class WorldPhotoIndexService : IDisposable
    {
        private sealed record WorkItem(string Path, long RootId, long Generation, bool RequireStable, TaskCompletionSource<bool>? Completion, string? HintKey);
        private sealed record WorkBatch(IReadOnlyList<WorkItem> Items);
        private sealed record PreparedWork(WorkItem Item, bool Success, WorldPhotoIndexWrite? Write);

        private readonly WorldPhotoIndexDatabase _database;
        private readonly IWorldPhotoMetadataReader _metadataReader;
        private readonly IWorldPhotoThumbnailStore _thumbnailStore;
        private readonly IWorldPhotoLauncher _launcher;
        private readonly WorldPhotoIndexServiceOptions _options;
        private readonly Action<string, Exception?> _log;
        private readonly object _lifecycleLock = new();
        private readonly SemaphoreSlim _reconcileLock = new(1, 1);
        private Channel<WorkBatch>? _workChannel;
        private Channel<string>? _thumbnailChannel;
        private Channel<bool>? _reconciliationChannel;
        private CancellationTokenSource? _cancellation;
        private FileSystemWatcher? _watcher;
        private Task? _workerTask;
        private Task? _thumbnailTask;
        private Task? _periodicTask;
        private Task? _reconciliationTask;
        private WorldPhotoRoot? _root;
        private readonly ConcurrentDictionary<string, byte> _pendingHintPaths = new(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _pendingThumbnailTokens = new(StringComparer.Ordinal);
        private long _discovered;
        private long _processed;
        private long _queueDepth;
        private long _thumbnailQueueDepth;
        private DateTimeOffset? _lastSuccessfulReconciliation;
        private string? _lastError;
        private volatile string _state = "stopped";
        private volatile bool _rebuildInProgress;
        private int _rebuildRequested;

        public WorldPhotoIndexService(
            WorldPhotoIndexDatabase database,
            IWorldPhotoMetadataReader metadataReader,
            IWorldPhotoThumbnailStore thumbnailStore,
            IWorldPhotoLauncher launcher,
            WorldPhotoIndexServiceOptions? options = null,
            Action<string, Exception?>? log = null)
        {
            _database = database;
            _metadataReader = metadataReader;
            _thumbnailStore = thumbnailStore;
            _launcher = launcher;
            _options = options ?? new WorldPhotoIndexServiceOptions();
            _log = log ?? ((_, _) => { });
        }

        public bool IsRunning => _cancellation is { IsCancellationRequested: false };

        public void Start(string rootPath)
        {
            lock (_lifecycleLock)
            {
                if (IsRunning)
                {
                    EnsureRoot(rootPath);
                    return;
                }

                CancellationTokenSource? cancellation = null;
                FileSystemWatcher? watcher = null;
                try
                {
                    var root = _database.SetCurrentRoot(rootPath);
                    cancellation = new CancellationTokenSource();
                    var workChannel = Channel.CreateBounded<WorkBatch>(new BoundedChannelOptions(_options.WorkQueueCapacity)
                    {
                        FullMode = BoundedChannelFullMode.Wait,
                        SingleReader = true,
                        SingleWriter = false
                    });
                    var thumbnailChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(_options.ThumbnailQueueCapacity)
                    {
                        FullMode = BoundedChannelFullMode.Wait,
                        SingleReader = true,
                        SingleWriter = false
                    });
                    var reconciliationChannel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
                    {
                        FullMode = BoundedChannelFullMode.DropWrite,
                        SingleReader = true,
                        SingleWriter = false
                    });
                    watcher = CreateWatcher(root.CanonicalPath);

                    _cancellation = cancellation;
                    _workChannel = workChannel;
                    _thumbnailChannel = thumbnailChannel;
                    _reconciliationChannel = reconciliationChannel;
                    _root = root;
                    _watcher = watcher;
                    _state = "starting";
                    _workerTask = Task.Run(() => WorkLoopAsync(cancellation.Token));
                    _thumbnailTask = Task.Run(() => ThumbnailLoopAsync(cancellation.Token));
                    _periodicTask = Task.Run(() => PeriodicLoopAsync(cancellation.Token));
                    _reconciliationTask = Task.Run(() => ReconciliationLoopAsync(cancellation.Token));
                    if (watcher != null)
                        watcher.EnableRaisingEvents = true;
                    ScheduleReconciliation();
                }
                catch
                {
                    watcher?.Dispose();
                    cancellation?.Cancel();
                    cancellation?.Dispose();
                    _cancellation = null;
                    _workChannel = null;
                    _thumbnailChannel = null;
                    _reconciliationChannel = null;
                    _root = null;
                    _watcher = null;
                    _workerTask = null;
                    _thumbnailTask = null;
                    _periodicTask = null;
                    _reconciliationTask = null;
                    _state = "stopped";
                    throw;
                }
            }
        }

        public void EnsureRoot(string rootPath)
        {
            var canonical = WorldPhotoIndexDatabase.CanonicalizeDirectory(rootPath);
            lock (_lifecycleLock)
            {
                if (!IsRunning)
                {
                    Start(canonical);
                    return;
                }
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (_root != null && string.Equals(_root.CanonicalPath, canonical, comparison))
                    return;

                _watcher?.Dispose();
                _watcher = null;
                _root = _database.SetCurrentRoot(canonical);
                _pendingHintPaths.Clear();
                _pendingThumbnailTokens.Clear();
                _watcher = CreateWatcher(canonical);
                if (_watcher != null)
                    _watcher.EnableRaisingEvents = true;
                _state = "scanning";
                ScheduleReconciliation();
            }
        }

        public void EnqueuePath(string filePath)
        {
            var root = _root;
            var channel = _workChannel;
            if (root == null || channel == null || !WorldPhotoIndexDatabase.IsContainedBy(root.CanonicalPath, filePath))
                return;
            var hintKey = WorldPhotoIndexDatabase.CreatePathKey(filePath);
            if (!_pendingHintPaths.TryAdd(hintKey, 0))
                return;
            Interlocked.Increment(ref _queueDepth);
            var item = new WorkItem(filePath, root.Id, 0, true, null, hintKey);
            if (!channel.Writer.TryWrite(new WorkBatch(new[] { item })))
            {
                _pendingHintPaths.TryRemove(hintKey, out _);
                Interlocked.Decrement(ref _queueDepth);
                _lastError = "work_queue_full";
                ScheduleReconciliation();
                return;
            }
        }

        public async Task ReconcileNowAsync(CancellationToken cancellationToken = default)
        {
            if (!IsRunning)
                return;
            await _reconcileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await ReconcileCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _reconcileLock.Release();
            }
        }

        private async Task ReconcileCoreAsync(CancellationToken cancellationToken)
        {
            var reconciliationRootId = 0L;
            try
            {
                var root = _root;
                var channel = _workChannel;
                if (root == null || channel == null || !Directory.Exists(root.CanonicalPath))
                {
                    _state = "idle";
                    return;
                }
                reconciliationRootId = root.Id;

                _state = "scanning";
                _lastError = null;
                var generation = _database.BeginReconciliation(root.Id);
                var enumerationOptions = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    ReturnSpecialDirectories = false
                };
                var writerBatchSize = Math.Clamp(_options.WriterBatchSize, 1, 1024);
                var paths = new List<string>(writerBatchSize);
                foreach (var path in Directory.EnumerateFiles(root.CanonicalPath, "*", enumerationOptions))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
                        continue;
                    Interlocked.Increment(ref _discovered);
                    paths.Add(path);
                    if (paths.Count >= writerBatchSize)
                    {
                        await DispatchReconciliationBatchAsync(channel, root.Id, generation, paths, cancellationToken).ConfigureAwait(false);
                        paths.Clear();
                    }
                }
                if (paths.Count > 0)
                    await DispatchReconciliationBatchAsync(channel, root.Id, generation, paths, cancellationToken).ConfigureAwait(false);

                if (_root?.Id != root.Id)
                    return;
                _database.CompleteReconciliation(root.Id, generation);
                _lastSuccessfulReconciliation = DateTimeOffset.UtcNow;
                _state = "idle";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _state = "stopping";
            }
            catch (Exception) when (_root?.Id != reconciliationRootId)
            {
                // Root switching retires the old generation without cleanup authority.
            }
            catch (Exception exception)
            {
                _lastError = exception.GetType().Name;
                _state = "error";
                _log("World photo reconciliation failed.", exception);
            }
        }

        public IReadOnlyList<WorldPhotoThumbnailRequestResult> RequestThumbnails(IEnumerable<string> publicTokens)
        {
            var results = new List<WorldPhotoThumbnailRequestResult>();
            var channel = _thumbnailChannel;
            var count = 0;
            foreach (var token in publicTokens)
            {
                if (++count > 100)
                    break;
                var record = _database.FindByPublicToken(token);
                if (record == null)
                {
                    results.Add(new WorldPhotoThumbnailRequestResult { PublicToken = token, Status = "error", Error = "invalid_token" });
                    continue;
                }
                if (record.ThumbnailState == WorldPhotoThumbnailState.Ready
                    && record.ThumbnailPath != null
                    && _thumbnailStore.IsUsable(record.ThumbnailPath))
                {
                    results.Add(new WorldPhotoThumbnailRequestResult { PublicToken = token, Status = "ready", ThumbnailPath = record.ThumbnailPath });
                    continue;
                }
                if (!_pendingThumbnailTokens.TryAdd(token, 0))
                {
                    results.Add(new WorldPhotoThumbnailRequestResult { PublicToken = token, Status = "accepted" });
                    continue;
                }
                if (channel == null)
                {
                    _pendingThumbnailTokens.TryRemove(token, out _);
                    results.Add(new WorldPhotoThumbnailRequestResult { PublicToken = token, Status = "error", Error = "queue_full" });
                    continue;
                }
                _database.UpdateThumbnail(token, WorldPhotoThumbnailState.Queued, null, null);
                Interlocked.Increment(ref _thumbnailQueueDepth);
                if (!channel.Writer.TryWrite(token))
                {
                    _pendingThumbnailTokens.TryRemove(token, out _);
                    Interlocked.Decrement(ref _thumbnailQueueDepth);
                    _database.UpdateThumbnail(token, WorldPhotoThumbnailState.Missing, null, null);
                    results.Add(new WorldPhotoThumbnailRequestResult { PublicToken = token, Status = "error", Error = "queue_full" });
                    continue;
                }
                results.Add(new WorldPhotoThumbnailRequestResult { PublicToken = token, Status = "accepted" });
            }
            return results;
        }

        public bool OpenIndexedPhoto(string publicToken)
        {
            lock (_lifecycleLock)
            {
                var record = _database.FindByPublicToken(publicToken);
                var root = _database.GetCurrentRoot();
                if (record == null || root == null || record.RootId != root.Id)
                    return false;
                if (!WorldPhotoPathPolicy.IsSafeCurrentPng(root.CanonicalPath, record.FilePath))
                    return false;
                var file = new FileInfo(record.FilePath);
                var fingerprint = new WorldPhotoFingerprint(file.LastWriteTimeUtc.Ticks, file.Length);
                if (fingerprint != record.Fingerprint)
                    return false;
                _launcher.Open(file.FullName);
                return true;
            }
        }

        public void Rebuild()
        {
            lock (_lifecycleLock)
            {
                if (_root == null || !IsRunning || _rebuildInProgress)
                    return;
                _rebuildInProgress = true;
                Interlocked.Exchange(ref _rebuildRequested, 1);
                ScheduleReconciliation();
            }
        }

        public WorldPhotoPage GetWorldPhotos(string worldId, string? cursor, int limit)
        {
            return _database.GetWorldPhotos(worldId, cursor, limit);
        }

        public WorldPhotoIndexStatus GetStatus()
        {
            return new WorldPhotoIndexStatus
            {
                State = _state,
                CurrentRootIdentity = _root == null
                    ? null
                    : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_root.PathKey))).ToLowerInvariant(),
                Discovered = Interlocked.Read(ref _discovered),
                Processed = Interlocked.Read(ref _processed),
                QueueDepth = Interlocked.Read(ref _queueDepth),
                ThumbnailQueueDepth = Interlocked.Read(ref _thumbnailQueueDepth),
                LastSuccessfulReconciliation = _lastSuccessfulReconciliation,
                LastError = _lastError,
                RebuildInProgress = _rebuildInProgress,
                Counts = _database.GetStatistics()
            };
        }

        public void Stop()
        {
            Task[] tasks;
            lock (_lifecycleLock)
            {
                if (_cancellation == null)
                    return;
                _state = "stopping";
                _watcher?.Dispose();
                _watcher = null;
                _cancellation.Cancel();
                _workChannel?.Writer.TryComplete();
                _thumbnailChannel?.Writer.TryComplete();
                _reconciliationChannel?.Writer.TryComplete();
                tasks = new[] { _workerTask, _thumbnailTask, _periodicTask, _reconciliationTask }
                    .Where(task => task != null)
                    .Cast<Task>()
                    .ToArray();
            }
            try
            {
                Task.WhenAll(tasks).Wait(_options.StopTimeout);
            }
            catch (Exception exception) when (exception is AggregateException or OperationCanceledException)
            {
                _log("World photo index stopped with pending work.", exception);
            }
            lock (_lifecycleLock)
            {
                _cancellation.Dispose();
                _cancellation = null;
                _workChannel = null;
                _thumbnailChannel = null;
                _reconciliationChannel = null;
                _pendingHintPaths.Clear();
                _pendingThumbnailTokens.Clear();
                _workerTask = null;
                _thumbnailTask = null;
                _periodicTask = null;
                _reconciliationTask = null;
                _rebuildInProgress = false;
                Interlocked.Exchange(ref _rebuildRequested, 0);
                _state = "stopped";
            }
        }

        public void Dispose()
        {
            Stop();
            _reconcileLock.Dispose();
        }

        private async Task WorkLoopAsync(CancellationToken cancellationToken)
        {
            var channel = _workChannel!;
            try
            {
                await foreach (var batch in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    var prepared = new List<PreparedWork>(batch.Items.Count);
                    foreach (var item in batch.Items)
                    {
                        try
                        {
                            prepared.Add(await PreparePathAsync(item, cancellationToken).ConfigureAwait(false));
                        }
                        catch (Exception exception)
                        {
                            _lastError = exception.GetType().Name;
                            _log("World photo indexing failed for one file.", exception);
                            prepared.Add(new PreparedWork(item, false, null));
                        }
                    }

                    var writes = prepared.Where(item => item.Success && item.Write != null)
                        .Select(item => item.Write!)
                        .ToArray();
                    var writeSucceeded = true;
                    try
                    {
                        if (writes.Length > 0)
                            _database.UpsertBatch(batch.Items[0].RootId, writes);
                    }
                    catch (Exception exception)
                    {
                        writeSucceeded = false;
                        _lastError = exception.GetType().Name;
                        _log("World photo index batch commit failed.", exception);
                    }

                    foreach (var item in prepared)
                    {
                        Interlocked.Decrement(ref _queueDepth);
                        Interlocked.Increment(ref _processed);
                        if (item.Item.HintKey != null)
                            _pendingHintPaths.TryRemove(item.Item.HintKey, out _);
                        item.Item.Completion?.TrySetResult(item.Success && (item.Write == null || writeSucceeded));
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private async Task<PreparedWork> PreparePathAsync(WorkItem item, CancellationToken cancellationToken)
        {
            var root = _root;
            if (root == null || item.RootId != root.Id || !WorldPhotoIndexDatabase.IsContainedBy(root.CanonicalPath, item.Path))
                return new PreparedWork(item, false, null);
            if (!File.Exists(item.Path))
            {
                if (item.Generation == 0)
                {
                    ScheduleReconciliation();
                    return new PreparedWork(item, true, null);
                }
                return new PreparedWork(item, false, null);
            }
            if (!string.Equals(Path.GetExtension(item.Path), ".png", StringComparison.OrdinalIgnoreCase))
                return new PreparedWork(item, true, null);

            var file = new FileInfo(item.Path);
            var fingerprint = new WorldPhotoFingerprint(file.LastWriteTimeUtc.Ticks, file.Length);
            if (item.RequireStable)
            {
                var initialFingerprint = fingerprint;
                await Task.Delay(_options.StableFileDelay, cancellationToken).ConfigureAwait(false);
                file.Refresh();
                if (!file.Exists)
                {
                    ScheduleReconciliation();
                    return new PreparedWork(item, item.Generation == 0, null);
                }
                fingerprint = new WorldPhotoFingerprint(file.LastWriteTimeUtc.Ticks, file.Length);
                if (fingerprint != initialFingerprint)
                {
                    var unstableCapturedAt = new DateTimeOffset(
                        file.CreationTimeUtc == DateTime.MinValue ? file.LastWriteTimeUtc : file.CreationTimeUtc,
                        TimeSpan.Zero);
                    return new PreparedWork(item, true, new WorldPhotoIndexWrite
                    {
                        FilePath = item.Path,
                        Fingerprint = fingerprint,
                        ParseState = WorldPhotoParseState.Retryable,
                        CapturedAt = unstableCapturedAt,
                        ErrorCode = "file_not_stable",
                        SeenGeneration = item.Generation,
                        RetryAfter = DateTimeOffset.UtcNow.Add(_options.RetryDelay)
                    });
                }
            }

            var existing = _database.FindByPath(item.RootId, item.Path);
            if (existing != null && existing.Fingerprint == fingerprint)
            {
                var retryDue = existing.ParseState == WorldPhotoParseState.Retryable
                    && (!existing.RetryAfter.HasValue || existing.RetryAfter <= DateTimeOffset.UtcNow);
                if (!retryDue)
                {
                    if (item.Generation == 0)
                        return new PreparedWork(item, true, null);
                    return new PreparedWork(item, true, new WorldPhotoIndexWrite
                    {
                        FilePath = existing.FilePath,
                        Fingerprint = existing.Fingerprint,
                        ParseState = existing.ParseState,
                        WorldId = existing.WorldId,
                        CapturedAt = existing.CapturedAt,
                        ErrorCode = existing.LastErrorCode,
                        SeenGeneration = item.Generation,
                        RetryAfter = existing.RetryAfter
                    });
                }
            }

            WorldPhotoMetadataReadResult parsed;
            try
            {
                parsed = _metadataReader.Read(item.Path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                parsed = new WorldPhotoMetadataReadResult { State = WorldPhotoParseState.Retryable, ErrorCode = exception.GetType().Name };
            }
            var capturedAt = parsed.CapturedAt ?? new DateTimeOffset(file.CreationTimeUtc == DateTime.MinValue ? file.LastWriteTimeUtc : file.CreationTimeUtc, TimeSpan.Zero);
            return new PreparedWork(item, true, new WorldPhotoIndexWrite
            {
                FilePath = item.Path,
                Fingerprint = fingerprint,
                ParseState = parsed.State,
                WorldId = parsed.WorldId,
                CapturedAt = capturedAt,
                ErrorCode = parsed.ErrorCode,
                SeenGeneration = item.Generation,
                RetryAfter = parsed.State == WorldPhotoParseState.Retryable ? DateTimeOffset.UtcNow.Add(_options.RetryDelay) : null
            });
        }

        private async Task ThumbnailLoopAsync(CancellationToken cancellationToken)
        {
            var channel = _thumbnailChannel!;
            try
            {
                await foreach (var token in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        var record = _database.FindByPublicToken(token);
                        if (record == null)
                            continue;
                        var path = await _thumbnailStore.CreateAsync(record, cancellationToken).ConfigureAwait(false);
                        var key = $"{record.PublicToken}-{record.Fingerprint.LastWriteUtcTicks}-{record.Fingerprint.FileSize}";
                        _database.UpdateThumbnail(token, WorldPhotoThumbnailState.Ready, key, path);
                    }
                    catch (Exception exception)
                    {
                        _database.UpdateThumbnail(token, WorldPhotoThumbnailState.Error, null, null);
                        _log("World photo thumbnail generation failed.", exception);
                    }
                    finally
                    {
                        _pendingThumbnailTokens.TryRemove(token, out _);
                        Interlocked.Decrement(ref _thumbnailQueueDepth);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private async Task PeriodicLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                var retryPollInterval = _options.RetryPollInterval <= TimeSpan.Zero
                    ? TimeSpan.FromSeconds(1)
                    : _options.RetryPollInterval;
                var nextReconciliation = DateTimeOffset.UtcNow.Add(_options.PeriodicReconciliationInterval);
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(retryPollInterval, cancellationToken).ConfigureAwait(false);
                    var root = _root;
                    if (root != null)
                    {
                        foreach (var path in _database.GetDueRetryPaths(root.Id, DateTimeOffset.UtcNow, _options.RetryBatchSize))
                            EnqueuePath(path);
                    }
                    if (DateTimeOffset.UtcNow >= nextReconciliation)
                    {
                        ScheduleReconciliation();
                        nextReconciliation = DateTimeOffset.UtcNow.Add(_options.PeriodicReconciliationInterval);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private async Task ReconciliationLoopAsync(CancellationToken cancellationToken)
        {
            var channel = _reconciliationChannel!;
            try
            {
                await foreach (var _ in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    var rebuild = Interlocked.Exchange(ref _rebuildRequested, 0) == 1;
                    try
                    {
                        if (rebuild)
                            await RebuildAndReconcileAsync(cancellationToken).ConfigureAwait(false);
                        else
                            await ReconcileNowAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        _lastError = exception.GetType().Name;
                        _state = "error";
                        _log("World photo reconciliation request failed.", exception);
                    }
                    finally
                    {
                        if (rebuild)
                            _rebuildInProgress = false;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private async Task RebuildAndReconcileAsync(CancellationToken cancellationToken)
        {
            await _reconcileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            string? rootPath = null;
            try
            {
                lock (_lifecycleLock)
                {
                    rootPath = _root?.CanonicalPath;
                    if (rootPath == null)
                        return;
                    _database.Reset();
                    _thumbnailStore.Clear();
                    _pendingHintPaths.Clear();
                    _pendingThumbnailTokens.Clear();
                    _root = _database.SetCurrentRoot(rootPath);
                }
                await ReconcileCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _lastError = exception.GetType().Name;
                _state = "error";
                _log("World photo index rebuild failed.", exception);
                if (rootPath != null)
                {
                    try
                    {
                        lock (_lifecycleLock)
                            _root = _database.SetCurrentRoot(rootPath);
                    }
                    catch (Exception recoveryException)
                    {
                        _log("World photo index rebuild recovery failed.", recoveryException);
                    }
                }
            }
            finally
            {
                _reconcileLock.Release();
            }
        }

        private void ScheduleReconciliation()
        {
            _reconciliationChannel?.Writer.TryWrite(true);
        }

        private async Task DispatchReconciliationBatchAsync(
            Channel<WorkBatch> channel,
            long rootId,
            long generation,
            IReadOnlyList<string> paths,
            CancellationToken cancellationToken)
        {
            var items = new List<WorkItem>(paths.Count);
            var completions = new List<Task<bool>>(paths.Count);
            foreach (var path in paths)
            {
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                items.Add(new WorkItem(path, rootId, generation, false, completion, null));
                completions.Add(completion.Task);
            }
            Interlocked.Add(ref _queueDepth, items.Count);
            try
            {
                await channel.Writer.WriteAsync(new WorkBatch(items), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Interlocked.Add(ref _queueDepth, -items.Count);
                throw;
            }
            var results = await Task.WhenAll(completions).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (results.Any(success => !success))
                throw new IOException("A reconciliation batch could not be indexed safely.");
        }

        private FileSystemWatcher? CreateWatcher(string rootPath)
        {
            if (!_options.EnableFileSystemWatcher || !Directory.Exists(rootPath))
                return null;
            var watcher = new FileSystemWatcher(rootPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                Filter = "*.*"
            };
            watcher.Created += (_, args) => EnqueuePath(args.FullPath);
            watcher.Changed += (_, args) => EnqueuePath(args.FullPath);
            watcher.Renamed += (_, args) =>
            {
                EnqueuePath(args.OldFullPath);
                EnqueuePath(args.FullPath);
            };
            watcher.Deleted += (_, args) => EnqueuePath(args.FullPath);
            watcher.Error += (_, args) =>
            {
                _lastError = "watcher_error";
                _log("World photo watcher overflowed or failed; reconciliation scheduled.", args.GetException());
                ScheduleReconciliation();
            };
            return watcher;
        }
    }
}
