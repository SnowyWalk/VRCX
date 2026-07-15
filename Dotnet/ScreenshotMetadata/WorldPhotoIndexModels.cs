#nullable enable
using System;
using System.Collections.Generic;

namespace VRCX
{
    public enum WorldPhotoParseState
    {
        Pending = 0,
        Valid = 1,
        NoMetadata = 2,
        Retryable = 3,
        Error = 4
    }

    public enum WorldPhotoThumbnailState
    {
        Missing = 0,
        Queued = 1,
        Ready = 2,
        Error = 3
    }

    public sealed class WorldPhotoRoot
    {
        public long Id { get; init; }
        public string CanonicalPath { get; init; } = string.Empty;
        public string PathKey { get; init; } = string.Empty;
        public bool IsCurrent { get; init; }
    }

    public readonly record struct WorldPhotoFingerprint(long LastWriteUtcTicks, long FileSize);

    public sealed class WorldPhotoIndexWrite
    {
        public required string FilePath { get; init; }
        public required WorldPhotoFingerprint Fingerprint { get; init; }
        public required WorldPhotoParseState ParseState { get; init; }
        public string? WorldId { get; init; }
        public DateTimeOffset CapturedAt { get; init; }
        public string? ErrorCode { get; init; }
        public long SeenGeneration { get; init; }
        public DateTimeOffset? RetryAfter { get; init; }
    }

    public sealed class WorldPhotoIndexRecord
    {
        public long Id { get; init; }
        public string PublicToken { get; init; } = string.Empty;
        public long RootId { get; init; }
        public string FilePath { get; init; } = string.Empty;
        public string PathKey { get; init; } = string.Empty;
        public string? WorldId { get; init; }
        public DateTimeOffset CapturedAt { get; init; }
        public WorldPhotoFingerprint Fingerprint { get; init; }
        public WorldPhotoParseState ParseState { get; init; }
        public string? LastErrorCode { get; init; }
        public long SeenGeneration { get; init; }
        public DateTimeOffset? RetryAfter { get; init; }
        public string? ThumbnailPath { get; init; }
        public WorldPhotoThumbnailState ThumbnailState { get; init; }
    }

    public sealed class WorldPhotoPageItem
    {
        public string PublicToken { get; init; } = string.Empty;
        public DateTimeOffset CapturedAt { get; init; }
        public string? ThumbnailPath { get; init; }
        public WorldPhotoThumbnailState ThumbnailState { get; init; }
    }

    public sealed class WorldPhotoPage
    {
        public IReadOnlyList<WorldPhotoPageItem> Items { get; init; } = Array.Empty<WorldPhotoPageItem>();
        public string? NextCursor { get; init; }
    }

    public sealed class WorldPhotoIndexStatistics
    {
        public long Total { get; init; }
        public long Valid { get; init; }
        public long Invalid { get; init; }
        public long Retryable { get; init; }
        public long Error { get; init; }
    }
}
