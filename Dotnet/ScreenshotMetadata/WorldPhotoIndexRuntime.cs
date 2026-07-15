#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace VRCX
{
    public sealed class ScreenshotWorldPhotoMetadataReader : IWorldPhotoMetadataReader
    {
        public WorldPhotoMetadataReadResult Read(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)
                    || !string.Equals(Path.GetExtension(filePath), ".png", StringComparison.OrdinalIgnoreCase)
                    || !ScreenshotHelper.IsPNGFile(filePath))
                {
                    return new WorldPhotoMetadataReadResult { State = WorldPhotoParseState.Error, ErrorCode = "not_png" };
                }

                var metadata = ScreenshotHelper.GetScreenshotMetadata(filePath);
                if (metadata == null || metadata.Error != null || string.IsNullOrWhiteSpace(metadata.World?.Id))
                {
                    return new WorldPhotoMetadataReadResult
                    {
                        State = WorldPhotoParseState.NoMetadata,
                        ErrorCode = metadata?.Error == null ? "missing_metadata" : "invalid_metadata"
                    };
                }

                DateTimeOffset? capturedAt = null;
                if (metadata.Timestamp.HasValue)
                {
                    var timestamp = metadata.Timestamp.Value;
                    capturedAt = timestamp.Kind == DateTimeKind.Unspecified
                        ? new DateTimeOffset(timestamp, TimeZoneInfo.Local.GetUtcOffset(timestamp)).ToUniversalTime()
                        : new DateTimeOffset(timestamp).ToUniversalTime();
                }
                return new WorldPhotoMetadataReadResult
                {
                    State = WorldPhotoParseState.Valid,
                    WorldId = metadata.World.Id,
                    CapturedAt = capturedAt
                };
            }
            catch (Exception exception)
            {
                return new WorldPhotoMetadataReadResult
                {
                    State = exception is IOException or UnauthorizedAccessException
                        ? WorldPhotoParseState.Retryable
                        : WorldPhotoParseState.Error,
                    ErrorCode = exception.GetType().Name
                };
            }
        }
    }

    internal sealed class ImageSharpWorldPhotoThumbnailStore : IWorldPhotoThumbnailStore
    {
        private const long MaximumCacheBytes = 1024L * 1024 * 1024;
        private const long CleanupTargetBytes = MaximumCacheBytes * 8 / 10;
        private const long MaximumPixels = 100_000_000;
        private static readonly TimeSpan MaximumAge = TimeSpan.FromDays(90);
        private readonly string _directory;

        public ImageSharpWorldPhotoThumbnailStore(string directory)
        {
            _directory = directory;
            Directory.CreateDirectory(_directory);
        }

        public async Task<string> CreateAsync(WorldPhotoIndexRecord record, CancellationToken cancellationToken)
        {
            var key = $"{record.PublicToken}-{record.Fingerprint.LastWriteUtcTicks}-{record.Fingerprint.FileSize}";
            var target = Path.Join(_directory, key + ".jpg");
            if (IsUsable(target))
            {
                File.SetLastAccessTimeUtc(target, DateTime.UtcNow);
                return target;
            }
            if (File.Exists(target))
                File.Delete(target);

            var info = await Image.IdentifyAsync(record.FilePath, cancellationToken).ConfigureAwait(false);
            if (info == null || (long)info.Width * info.Height > MaximumPixels)
                throw new InvalidDataException("Photo dimensions exceed the thumbnail safety limit.");

            using var image = await Image.LoadAsync(record.FilePath, cancellationToken).ConfigureAwait(false);
            image.Mutate(context => context.AutoOrient().Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new Size(512, 512),
                Sampler = KnownResamplers.Lanczos3
            }));
            var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await image.SaveAsync(temporary, new JpegEncoder { Quality = 82 }, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, target, true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            Cleanup();
            return target;
        }

        public bool IsUsable(string path)
        {
            try
            {
                return File.Exists(path) && Image.Identify(path) != null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidImageContentException or UnknownImageFormatException)
            {
                return false;
            }
        }

        public void Clear()
        {
            if (!Directory.Exists(_directory))
                return;
            foreach (var file in Directory.EnumerateFiles(_directory))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        private void Cleanup()
        {
            var files = new DirectoryInfo(_directory).EnumerateFiles("*.jpg")
                .OrderBy(file => file.LastAccessTimeUtc)
                .ToList();
            var now = DateTime.UtcNow;
            foreach (var expired in files.Where(file => now - file.LastAccessTimeUtc > MaximumAge).ToList())
            {
                TryDelete(expired);
                files.Remove(expired);
            }
            var total = files.Sum(file => file.Exists ? file.Length : 0);
            if (total <= MaximumCacheBytes)
                return;
            foreach (var file in files)
            {
                var length = file.Length;
                if (TryDelete(file))
                    total -= length;
                if (total <= CleanupTargetBytes)
                    break;
            }
        }

        private static bool TryDelete(FileInfo file)
        {
            try
            {
                file.Delete();
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    internal static class WorldPhotoIndexRuntime
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static readonly object Sync = new();
        private static WorldPhotoIndexService? _service;

        public static void Start(AppApi appApi)
        {
            lock (Sync)
            {
                try
                {
                    if (_service == null)
                    {
                        var database = new WorldPhotoIndexDatabase(Path.Join(Program.AppDataDirectory, "worldPhotoIndex.db"));
                        _service = new WorldPhotoIndexService(
                            database,
                            new ScreenshotWorldPhotoMetadataReader(),
                            new ImageSharpWorldPhotoThumbnailStore(Path.Join(Program.AppDataDirectory, "worldPhotoThumbnails")),
                            new ShellWorldPhotoLauncher(),
                            log: (message, exception) => Logger.Error(exception, message));
                    }
                    _service.Start(appApi.GetVRChatPhotosLocation());
                }
                catch (Exception exception)
                {
                    Logger.Error(exception, "World photo index failed to start; VRCX will continue without it.");
                }
            }
        }

        public static WorldPhotoIndexService? Get(AppApi appApi)
        {
            Start(appApi);
            try
            {
                _service?.EnsureRoot(appApi.GetVRChatPhotosLocation());
            }
            catch (Exception exception)
            {
                Logger.Error(exception, "World photo index root refresh failed.");
            }
            return _service;
        }

        public static void Enqueue(string path)
        {
            try
            {
                _service?.EnqueuePath(path);
            }
            catch (Exception exception)
            {
                Logger.Error(exception, "World photo capture registration failed.");
            }
        }

        public static void Stop()
        {
            lock (Sync)
            {
                try
                {
                    _service?.Stop();
                }
                catch (Exception exception)
                {
                    Logger.Error(exception, "World photo index failed to stop cleanly.");
                }
            }
        }
    }
}
