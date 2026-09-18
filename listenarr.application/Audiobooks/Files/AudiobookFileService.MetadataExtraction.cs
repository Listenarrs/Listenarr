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

    /// <summary>
    /// Resolve the length of the audio file a registration is about to record.
    /// </summary>
    /// <remarks>
    /// A lease's MetadataPath is a descriptor path on Linux and macOS
    /// (/proc/&lt;pid&gt;/fd/&lt;n&gt; or /dev/fd/&lt;n&gt;), so stat'ing it reports the size
    /// of the descriptor link rather than the length of the file the descriptor
    /// pins. The lease's own read stream is served from that descriptor and does
    /// report the pinned file's length, which is also how
    /// FileRegistrationRecoveryService compares a published file against its
    /// journalled length. Leases that expose no generation-bound read fall back
    /// to the published path.
    /// </remarks>
    private long? ResolveRegisteredFileLength(
        IAudiobookFileRegistrationLease? registrationLease,
        string filePath)
    {
        if (registrationLease != null)
        {
            try
            {
                using var metadataStream = registrationLease.OpenMetadataReadStream();
                if (metadataStream.CanSeek)
                {
                    return metadataStream.Length;
                }
            }
            catch (Exception exception) when (exception is
                NotSupportedException or IOException or UnauthorizedAccessException
                    or ObjectDisposedException)
            {
                logger.LogDebug(
                    exception,
                    "The registration lease exposed no generation-bound length; falling back to the published path for {Path}",
                    LogRedaction.SanitizeFilePath(filePath));
            }
        }

        try
        {
            return fileSystem.FileExists(filePath)
                ? fileSystem.GetFileLength(filePath)
                : null;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(
                exception,
                "Could not read the length of the registered audiobook file {Path}",
                LogRedaction.SanitizeFilePath(filePath));
            return null;
        }
    }
}
