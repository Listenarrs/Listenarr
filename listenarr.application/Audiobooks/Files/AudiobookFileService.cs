/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using Listenarr.Application.Common;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Files
{
    public class AudiobookFileService(
        IMemoryCache memoryCache,
        MetadataExtractionLimiter limiter,
        IAudiobookFileRepository audiobookFileRepository,
        IHistoryRepository historyRepository,
        IMetadataService metadataService,
        IToastService toastService,
        IFfmpegService ffmpegService,
        IFileSystem fileSystem,
        ILogger<AudiobookFileService> logger) : IAudiobookFileService
    {
        public async Task<bool> EnsureAudiobookFileAsync(Audiobook audiobook, string filePath, string? source = "scan")
        {
            if (!fileSystem.FileExists(filePath))
            {
                return false;
            }

            try
            {
                if (!FileUtils.IsAudioFile(filePath))
                {
                    logger.LogInformation("Skipping non-audio audiobook file registration for audiobook {AudiobookId}: {Path}", audiobook.Id, LogRedaction.SanitizeFilePath(filePath));
                    return false;
                }

                // Check for existing
                var exists = await audiobookFileRepository.ExistsAtPathAsync(audiobook.Id, filePath);
                if (exists)
                {
                    logger.LogDebug("AudiobookFile already exists for audiobook {AudiobookId} at path {Path}", audiobook.Id, LogRedaction.SanitizeFilePath(filePath));
                    return false;
                }

                // Skip if already registered to a different audiobook
                var registeredElsewhere = await audiobookFileRepository.IsPathUsedByOtherAsync(audiobook.Id, filePath);
                if (registeredElsewhere)
                {
                    logger.LogInformation("Skipping file {Path} for audiobook {AudiobookId} — already registered to another audiobook", LogRedaction.SanitizeFilePath(filePath), audiobook.Id);
                    return false;
                }

                // Conservative safety: if the audiobook already has a stored FilePath prefer
                // to only associate files in the same containing directory or BasePath.
                try
                {
                    if (!string.IsNullOrWhiteSpace(audiobook.FilePath))
                    {
                        var existingDir = FileUtils.NormalizeStoredPath(Path.GetDirectoryName(audiobook.FilePath));
                        var candidateDir = FileUtils.NormalizeStoredPath(Path.GetDirectoryName(filePath));
                        var candidateFull = FileUtils.NormalizeStoredPath(filePath);
                        var normalizedBasePath = FileUtils.NormalizeStoredPath(audiobook.BasePath);

                        if (!string.IsNullOrEmpty(existingDir)
                            && !string.IsNullOrEmpty(candidateDir)
                            && !string.IsNullOrEmpty(candidateFull))
                        {
                            var isInExistingDir = candidateDir.Equals(existingDir, StringComparison.OrdinalIgnoreCase) ||
                                                  FileUtils.IsPathInsideOf(candidateDir, existingDir);
                            var isInBasePath = !string.IsNullOrWhiteSpace(normalizedBasePath) &&
                                               (candidateDir.Equals(normalizedBasePath, StringComparison.OrdinalIgnoreCase)
                                                || FileUtils.IsPathInsideOf(candidateFull, normalizedBasePath));

                            if (!isInExistingDir && !isInBasePath)
                            {
                                var audiobookTitle = audiobook.Title ?? "Unknown";
                                logger.LogWarning("Refusing to associate file outside audiobook folder. AudiobookId={AudiobookId}, AudiobookDir={AudiobookDir}, BasePath={BasePath}, File={File}", audiobook.Id, LogRedaction.SanitizeFilePath(existingDir), LogRedaction.SanitizeFilePath(audiobook.BasePath), LogRedaction.SanitizeFilePath(filePath));
                                try
                                {
                                    var historyEntry = new History
                                    {
                                        AudiobookId = audiobook.Id,
                                        AudiobookTitle = audiobookTitle,
                                        EventType = "File Association Refused",
                                        Message = $"Refused to associate file outside audiobook folder: {Path.GetFileName(filePath)}",
                                        Source = source ?? "Scan",
                                        Data = JsonSerializer.Serialize(new { FilePath = filePath, AudiobookDir = existingDir, BasePath = audiobook.BasePath }),
                                        Timestamp = DateTime.UtcNow
                                    };
                                    await historyRepository.AddAsync(historyEntry);

                                    try
                                    {
                                        await toastService.PublishToastAsync("warning", "File not associated", $"Refused to associate {Path.GetFileName(filePath)} to {audiobookTitle}");
                                    }
                                    catch (Exception thx) when (thx is not OperationCanceledException && thx is not OutOfMemoryException && thx is not StackOverflowException)
                                    {
                                        logger.LogDebug(thx, "Failed to publish toast for refused file association");
                                    }
                                }
                                catch (Exception hx) when (hx is not OperationCanceledException && hx is not OutOfMemoryException && hx is not StackOverflowException)
                                {
                                    logger.LogDebug(hx, "Failed to persist history for refused file association (AudiobookId={AudiobookId}, File={File})", audiobook.Id, LogRedaction.SanitizeFilePath(filePath));
                                }

                                return false;
                            }
                        }
                    }
                }
                catch (Exception exDir) when (exDir is not OperationCanceledException && exDir is not OutOfMemoryException && exDir is not StackOverflowException)
                {
                    logger.LogDebug(exDir, "Failed to verify audiobook folder containment for AudiobookId={AudiobookId} File={File}", audiobook.Id, LogRedaction.SanitizeFilePath(filePath));
                }

                AudioMetadata? meta = null;
                try
                {
                    var fileInfoForCache = new FileInfo(filePath);
                    var ticks = fileInfoForCache.Exists ? fileInfoForCache.LastWriteTimeUtc.Ticks : 0L;
                    var cacheKey = $"meta::{filePath}::{ticks}";
                    if (!memoryCache.TryGetValue(cacheKey, out var cachedObj) || !(cachedObj is AudioMetadata cachedMeta))
                    {
                        using var _ = await limiter.Sem.LockAsync();
                        meta = await metadataService.ExtractFileMetadataAsync(filePath);
                        memoryCache.Set(cacheKey, meta, TimeSpan.FromMinutes(5));
                    }
                    else
                    {
                        meta = cachedMeta;
                    }
                }
                catch (Exception mEx) when (mEx is not OperationCanceledException && mEx is not OutOfMemoryException && mEx is not StackOverflowException)
                {
                    logger.LogInformation(mEx, "Metadata extraction failed for {Path}", LogRedaction.SanitizeFilePath(filePath));
                }

                try
                {
                    var needRetry = meta == null || (meta.Duration == TimeSpan.Zero && string.IsNullOrEmpty(meta.Format));
                    if (needRetry)
                    {
                        var installTask = ffmpegService.EnsureFfprobeInstalledAsync();
                        var completed = await Task.WhenAny(installTask, Task.Delay(TimeSpan.FromSeconds(10)));
                        if (completed == installTask)
                        {
                            try
                            {
                                var ffpath = await installTask;
                                if (!string.IsNullOrEmpty(ffpath))
                                {
                                    using var _ = await limiter.Sem.LockAsync();
                                    meta = await metadataService.ExtractFileMetadataAsync(filePath);
                                    var fileInfoForCache2 = new FileInfo(filePath);
                                    var ticks2 = fileInfoForCache2.Exists ? fileInfoForCache2.LastWriteTimeUtc.Ticks : 0L;
                                    var cacheKey2 = $"meta::{filePath}::{ticks2}";
                                    memoryCache.Set(cacheKey2, meta, TimeSpan.FromMinutes(5));
                                }
                            }
                            catch (Exception rex) when (rex is not OperationCanceledException && rex is not OutOfMemoryException && rex is not StackOverflowException)
                            {
                                logger.LogInformation(rex, "Retry metadata extraction failed for {Path}", LogRedaction.SanitizeFilePath(filePath));
                            }
                        }
                    }
                }
                catch (Exception exRetry) when (exRetry is not OperationCanceledException && exRetry is not OutOfMemoryException && exRetry is not StackOverflowException)
                {
                    logger.LogDebug(exRetry, "Non-fatal error while attempting ffprobe install/retry for {Path}", LogRedaction.SanitizeFilePath(filePath));
                }

                var fi = new FileInfo(filePath);
                var fileRecord = new AudiobookFile
                {
                    AudiobookId = audiobook.Id,
                    Path = filePath,
                    Size = fi.Exists ? fi.Length : (long?)null,
                    Source = source,
                    CreatedAt = DateTime.UtcNow,
                    DurationSeconds = meta?.Duration.TotalSeconds,
                    Format = meta?.Format,
                    Container = meta?.Container,
                    Codec = meta?.Codec,
                    Bitrate = meta?.BitRate,
                    SampleRate = meta?.SampleRate,
                    Channels = meta?.Channels
                };

                var attempts = 0;
                while (true)
                {
                    try
                    {
                        await audiobookFileRepository.AddAsync(fileRecord);
                        logger.LogInformation("Created AudiobookFile for audiobook {AudiobookId}: {Path} Id={Id}", audiobook.Id, LogRedaction.SanitizeFilePath(filePath), fileRecord.Id);

                        // Add history entry and update audiobook backward-compat fields
                        try
                        {
                            var historyEntry = new History
                            {
                                AudiobookId = audiobook.Id,
                                AudiobookTitle = audiobook?.Title ?? "Unknown",
                                EventType = "File Added",
                                Message = $"File scanned and added: {Path.GetFileName(filePath)}",
                                Source = source ?? "Scan",
                                Data = JsonSerializer.Serialize(new
                                {
                                    FilePath = fileRecord.Path,
                                    FileSize = fileRecord.Size,
                                    Format = fileRecord.Format,
                                    Source = fileRecord.Source
                                }),
                                Timestamp = DateTime.UtcNow
                            };
                            await historyRepository.AddAsync(historyEntry);
                        }
                        catch (Exception hx) when (hx is not OperationCanceledException && hx is not OutOfMemoryException && hx is not StackOverflowException)
                        {
                            logger.LogDebug(hx, "Failed to create history entry for added audiobook file {Path}", LogRedaction.SanitizeFilePath(filePath));
                        }

                        return true;
                    }
                    catch (UniqueConstraintViolationException)
                    {
                        logger.LogInformation("AudiobookFile insertion conflict detected (likely already created): {Path}", LogRedaction.SanitizeFilePath(filePath));
                        return false;
                    }
                    catch (PersistenceException dbEx)
                    {
                        attempts++;
                        if (attempts >= 3)
                        {
                            logger.LogWarning(dbEx, "Failed to save AudiobookFile after {Attempts} attempts: {Path}", attempts, LogRedaction.SanitizeFilePath(filePath));
                            return false;
                        }
                        await Task.Delay(100 * attempts);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Failed to create AudiobookFile record for audiobook {AudiobookId} at {Path}", audiobook.Id, LogRedaction.SanitizeFilePath(filePath));
                return false;
            }
        }
    }
}
