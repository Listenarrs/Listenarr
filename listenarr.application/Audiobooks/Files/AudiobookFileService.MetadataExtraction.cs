using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Files;

public partial class AudiobookFileService
{
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
