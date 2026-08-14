using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Files;

public partial class AudiobookFileService
{
    /// <summary>
    /// The byte length of the file a registration lease holds open.
    /// </summary>
    /// <remarks>
    /// A lease's metadata path is not always a path to the file. On Linux it is a
    /// <c>/proc/{pid}/fd/{fd}</c> descriptor link, and stat'ing the link reports the length of
    /// the link itself rather than of its target, which is a constant 64 bytes. Reading the
    /// length from the pinned handle keeps the generation guarantee the lease exists to
    /// provide, since it never consults the visible path, and reports the bytes the file
    /// actually has.
    ///
    /// Leases that do not expose generation-bound reads fall back to the metadata path, which
    /// is the public path for those callers.
    /// </remarks>
    private static long? ResolveRegisteredLength(
        IAudiobookFileRegistrationLease? registrationLease,
        string metadataPath)
    {
        if (registrationLease != null)
        {
            try
            {
                using var stream = registrationLease.OpenMetadataReadStream();
                if (stream.CanSeek)
                {
                    return stream.Length;
                }
            }
            catch (NotSupportedException)
            {
                // The lease does not expose generation-bound reads; fall through to the path.
            }
        }

        var fileInfo = new FileInfo(metadataPath);
        return fileInfo.Exists ? fileInfo.Length : null;
    }

    private async Task<AudioMetadata?> ExtractMetadataAsync(
        string metadataPath,
        string cacheIdentity,
        string publicPath)
    {
        AudioMetadata? metadata = null;
        try
        {
            var fileInfo = new FileInfo(metadataPath);
            var ticks = fileInfo.Exists ? fileInfo.LastWriteTimeUtc.Ticks : 0L;
            var cacheKey = $"meta::{cacheIdentity}::{ticks}";
            if (!memoryCache.TryGetValue(cacheKey, out var cachedObject)
                || cachedObject is not AudioMetadata cachedMetadata)
            {
                using var _ = await limiter.Sem.LockAsync();
                metadata = await metadataService.ExtractFileMetadataAsync(
                    new MetadataFileSource(metadataPath, publicPath));
                memoryCache.Set(cacheKey, metadata, TimeSpan.FromMinutes(5));
            }
            else
            {
                metadata = cachedMetadata;
            }
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            logger.LogInformation(
                exception,
                "Metadata extraction failed for {Path}",
                LogRedaction.SanitizeFilePath(publicPath));
        }

        try
        {
            var needsRetry = metadata == null
                || (metadata.Duration == TimeSpan.Zero
                    && string.IsNullOrEmpty(metadata.Format));
            if (!needsRetry)
            {
                return metadata;
            }

            var installTask = ffmpegService.EnsureFfprobeInstalledAsync();
            var completed = await Task.WhenAny(
                installTask,
                Task.Delay(TimeSpan.FromSeconds(10)));
            if (completed != installTask)
            {
                return metadata;
            }

            try
            {
                var ffprobePath = await installTask;
                if (string.IsNullOrEmpty(ffprobePath))
                {
                    return metadata;
                }

                using var _ = await limiter.Sem.LockAsync();
                metadata = await metadataService.ExtractFileMetadataAsync(
                    new MetadataFileSource(metadataPath, publicPath));
                var fileInfo = new FileInfo(metadataPath);
                var ticks = fileInfo.Exists ? fileInfo.LastWriteTimeUtc.Ticks : 0L;
                var cacheKey = $"meta::{cacheIdentity}::{ticks}";
                memoryCache.Set(cacheKey, metadata, TimeSpan.FromMinutes(5));
            }
            catch (Exception exception) when (exception is not (
                OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                logger.LogInformation(
                    exception,
                    "Retry metadata extraction failed for {Path}",
                    LogRedaction.SanitizeFilePath(publicPath));
            }
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            logger.LogDebug(
                exception,
                "Non-fatal error while attempting ffprobe install/retry for {Path}",
                LogRedaction.SanitizeFilePath(publicPath));
        }

        return metadata;
    }
}
