#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VRCX
{
    public sealed class WorldPhotoMetadataReadResult
    {
        public WorldPhotoParseState State { get; init; }
        public string? WorldId { get; init; }
        public DateTimeOffset? CapturedAt { get; init; }
        public string? ErrorCode { get; init; }
    }

    public interface IWorldPhotoMetadataReader
    {
        WorldPhotoMetadataReadResult Read(string filePath);
    }

    public interface IWorldPhotoThumbnailStore
    {
        Task<string> CreateAsync(WorldPhotoIndexRecord record, CancellationToken cancellationToken);
        bool IsUsable(string path);
        void Clear();
    }

    public interface IWorldPhotoLauncher
    {
        void Open(string canonicalFilePath);
    }

    public sealed class WorldPhotoThumbnailRequestResult
    {
        public string PublicToken { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string? ThumbnailPath { get; init; }
        public string? Error { get; init; }
    }

    public sealed class WorldPhotoIndexStatus
    {
        public string State { get; init; } = "stopped";
        public string? CurrentRootIdentity { get; init; }
        public long Discovered { get; init; }
        public long Processed { get; init; }
        public long QueueDepth { get; init; }
        public long ThumbnailQueueDepth { get; init; }
        public DateTimeOffset? LastSuccessfulReconciliation { get; init; }
        public string? LastError { get; init; }
        public bool RebuildInProgress { get; init; }
        public WorldPhotoIndexStatistics Counts { get; init; } = new();
    }

    public sealed class WorldPhotoIndexServiceOptions
    {
        public int WorkQueueCapacity { get; init; } = 1024;
        public int WriterBatchSize { get; init; } = 64;
        public int ThumbnailQueueCapacity { get; init; } = 256;
        public int RetryBatchSize { get; init; } = 100;
        public bool EnableFileSystemWatcher { get; init; } = true;
        public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMinutes(5);
        public TimeSpan RetryPollInterval { get; init; } = TimeSpan.FromSeconds(30);
        public TimeSpan PeriodicReconciliationInterval { get; init; } = TimeSpan.FromHours(1);
        public TimeSpan StableFileDelay { get; init; } = TimeSpan.FromMilliseconds(150);
        public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(3);
    }
}
