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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using Listenarr.Domain.Utils;
using Listenarr.Api.Services.Metadata;

namespace Listenarr.Api.Services
{
    public class AudioFileService : IAudioFileService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<AudioFileService> _logger;
        private readonly IMemoryCache _memoryCache;
        private readonly MetadataExtractionLimiter _limiter;

        public AudioFileService(IServiceScopeFactory scopeFactory, ILogger<AudioFileService> logger, IMemoryCache memoryCache, MetadataExtractionLimiter limiter)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _memoryCache = memoryCache;
            _limiter = limiter;
        }

        public async Task<bool> EnsureAudiobookFileAsync(int audiobookId, string filePath, string? source = "scan", bool forceMetadataRefresh = false)
        {
            try
            {
                if (!FileUtils.IsAudioFile(filePath))
                {
                    _logger.LogInformation("Skipping non-audio audiobook file registration for audiobook {AudiobookId}: {Path}", audiobookId, LogRedaction.SanitizeFilePath(filePath));
                    return false;
                }

                using var scope = _scopeFactory.CreateScope();
                var fileRepository = scope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
                var audiobookRepository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
                var historyRepository = scope.ServiceProvider.GetRequiredService<IHistoryRepository>();
                var metadataService = scope.ServiceProvider.GetRequiredService<IMetadataService>();

                // Check for existing
                var exists = await fileRepository.ExistsAtPathAsync(audiobookId, filePath);
                if (exists)
                {
                    if (forceMetadataRefresh)
                    {
                        _logger.LogDebug("AudiobookFile already exists for audiobook {AudiobookId} at {Path}; re-extracting metadata for backfill", audiobookId, LogRedaction.SanitizeFilePath(filePath));
                        await RefreshAudiobookMetadataFromFileAsync(audiobookId, filePath, scope, metadataService, audiobookRepository);
                    }
                    else
                    {
                        _logger.LogDebug("AudiobookFile already exists for audiobook {AudiobookId} at path {Path}", audiobookId, LogRedaction.SanitizeFilePath(filePath));
                    }
                    return false;
                }

                // Skip if already registered to a different audiobook
                var registeredElsewhere = await fileRepository.IsPathUsedByOtherAsync(audiobookId, filePath);
                if (registeredElsewhere)
                {
                    _logger.LogInformation("Skipping file {Path} for audiobook {AudiobookId} — already registered to another audiobook", LogRedaction.SanitizeFilePath(filePath), audiobookId);
                    return false;
                }

                // Conservative safety: if the audiobook already has a stored FilePath prefer
                // to only associate files in the same containing directory or BasePath.
                try
                {
                    var audiobook = await audiobookRepository.GetByIdAsync(audiobookId);
                    if (audiobook != null && !string.IsNullOrWhiteSpace(audiobook.FilePath))
                    {
                        var existingDir = NormalizePath(Path.GetDirectoryName(audiobook.FilePath));
                        var candidateDir = NormalizePath(Path.GetDirectoryName(filePath));
                        var candidateFull = NormalizePath(filePath);
                        var normalizedBasePath = NormalizePath(audiobook.BasePath);

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
                                _logger.LogWarning("Refusing to associate file outside audiobook folder. AudiobookId={AudiobookId}, AudiobookDir={AudiobookDir}, BasePath={BasePath}, File={File}", audiobookId, LogRedaction.SanitizeFilePath(existingDir), LogRedaction.SanitizeFilePath(audiobook.BasePath), LogRedaction.SanitizeFilePath(filePath));
                                try
                                {
                                    var historyEntry = new History
                                    {
                                        AudiobookId = audiobookId,
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
                                        var toastSvc = scope.ServiceProvider.GetService<IToastService>();
                                        if (toastSvc != null)
                                        {
                                            await toastSvc.PublishToastAsync("warning", "File not associated", $"Refused to associate {Path.GetFileName(filePath)} to {audiobookTitle}");
                                        }
                                    }
                                    catch (Exception thx) when (thx is not OperationCanceledException && thx is not OutOfMemoryException && thx is not StackOverflowException)
                                    {
                                        _logger.LogDebug(thx, "Failed to publish toast for refused file association");
                                    }
                                }
                                catch (Exception hx) when (hx is not OperationCanceledException && hx is not OutOfMemoryException && hx is not StackOverflowException)
                                {
                                    _logger.LogDebug(hx, "Failed to persist history for refused file association (AudiobookId={AudiobookId}, File={File})", audiobookId, LogRedaction.SanitizeFilePath(filePath));
                                }

                                return false;
                            }
                        }
                    }
                }
                catch (Exception exDir) when (exDir is not OperationCanceledException && exDir is not OutOfMemoryException && exDir is not StackOverflowException)
                {
                    _logger.LogDebug(exDir, "Failed to verify audiobook folder containment for AudiobookId={AudiobookId} File={File}", audiobookId, LogRedaction.SanitizeFilePath(filePath));
                }

                AudioMetadata? meta = null;
                try
                {
                    var fileInfoForCache = new FileInfo(filePath);
                    var ticks = fileInfoForCache.Exists ? fileInfoForCache.LastWriteTimeUtc.Ticks : 0L;
                    var cacheKey = $"meta::{filePath}::{ticks}";
                    if (!_memoryCache.TryGetValue(cacheKey, out var cachedObj) || !(cachedObj is AudioMetadata cachedMeta))
                    {
                        using var _ = await _limiter.Sem.LockAsync();
                        meta = await metadataService.ExtractFileMetadataAsync(filePath);
                        _memoryCache.Set(cacheKey, meta, TimeSpan.FromMinutes(5));
                    }
                    else
                    {
                        meta = cachedMeta;
                    }
                }
                catch (Exception mEx) when (mEx is not OperationCanceledException && mEx is not OutOfMemoryException && mEx is not StackOverflowException)
                {
                    _logger.LogInformation(mEx, "Metadata extraction failed for {Path}", LogRedaction.SanitizeFilePath(filePath));
                }

                try
                {
                    var needRetry = meta == null || (meta.Duration == TimeSpan.Zero && string.IsNullOrEmpty(meta.Format));
                    if (needRetry)
                    {
                        using var scope2 = _scopeFactory.CreateScope();
                        var ffmpegSvc = scope2.ServiceProvider.GetService<IFfmpegService>();
                        if (ffmpegSvc != null)
                        {
                            var installTask = ffmpegSvc.EnsureFfprobeInstalledAsync();
                            var completed = await Task.WhenAny(installTask, Task.Delay(TimeSpan.FromSeconds(10)));
                            if (completed == installTask)
                            {
                                try
                                {
                                    var ffpath = await installTask;
                                    if (!string.IsNullOrEmpty(ffpath))
                                    {
                                        using var _ = await _limiter.Sem.LockAsync();
                                        meta = await metadataService.ExtractFileMetadataAsync(filePath);
                                        var fileInfoForCache2 = new FileInfo(filePath);
                                        var ticks2 = fileInfoForCache2.Exists ? fileInfoForCache2.LastWriteTimeUtc.Ticks : 0L;
                                        var cacheKey2 = $"meta::{filePath}::{ticks2}";
                                        _memoryCache.Set(cacheKey2, meta, TimeSpan.FromMinutes(5));
                                    }
                                }
                                catch (Exception rex) when (rex is not OperationCanceledException && rex is not OutOfMemoryException && rex is not StackOverflowException)
                                {
                                    _logger.LogInformation(rex, "Retry metadata extraction failed for {Path}", LogRedaction.SanitizeFilePath(filePath));
                                }
                            }
                        }
                    }
                }
                catch (Exception exRetry) when (exRetry is not OperationCanceledException && exRetry is not OutOfMemoryException && exRetry is not StackOverflowException)
                {
                    _logger.LogDebug(exRetry, "Non-fatal error while attempting ffprobe install/retry for {Path}", LogRedaction.SanitizeFilePath(filePath));
                }

                var fi = new FileInfo(filePath);
                var fileRecord = new AudiobookFile
                {
                    AudiobookId = audiobookId,
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
                        await fileRepository.AddAsync(fileRecord);
                        _logger.LogInformation("Created AudiobookFile for audiobook {AudiobookId}: {Path} Id={Id}", audiobookId, LogRedaction.SanitizeFilePath(filePath), fileRecord.Id);

                        // Add history entry and update audiobook backward-compat fields
                        try
                        {
                            var audiobook = await audiobookRepository.GetByIdAsync(audiobookId);
                            var historyEntry = new History
                            {
                                AudiobookId = audiobookId,
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

                            try
                            {
                                var audiobookToUpdate = await audiobookRepository.GetByIdAsync(audiobookId);
                                if (audiobookToUpdate != null)
                                {
                                    audiobookToUpdate.FilePath = fileRecord.Path;
                                    audiobookToUpdate.FileSize = fileRecord.Size;

                                    if (meta != null)
                                    {
                                        await PromoteLocalMetadataAsync(audiobookToUpdate, meta, scope, filePath);
                                    }

                                    await audiobookRepository.UpdateAsync(audiobookToUpdate);
                                }
                            }
                            catch (Exception aubEx) when (aubEx is not OperationCanceledException && aubEx is not OutOfMemoryException && aubEx is not StackOverflowException)
                            {
                                _logger.LogDebug(aubEx, "Failed to update Audiobook file summary fields for AudiobookId {AudiobookId}", audiobookId);
                            }
                        }
                        catch (Exception hx) when (hx is not OperationCanceledException && hx is not OutOfMemoryException && hx is not StackOverflowException)
                        {
                            _logger.LogDebug(hx, "Failed to create history entry for added audiobook file {Path}", LogRedaction.SanitizeFilePath(filePath));
                        }

                        return true;
                    }
                    catch (DbUpdateException dbEx)
                    {
                        attempts++;
                        var inner = dbEx.InnerException?.Message ?? dbEx.Message;
                        if (inner != null && inner.IndexOf("UNIQUE", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            _logger.LogInformation("AudiobookFile insertion conflict detected (likely already created): {Path}", LogRedaction.SanitizeFilePath(filePath));
                            return false;
                        }
                        if (attempts >= 3)
                        {
                            _logger.LogWarning(dbEx, "Failed to save AudiobookFile after {Attempts} attempts: {Path}", attempts, LogRedaction.SanitizeFilePath(filePath));
                            return false;
                        }
                        await Task.Delay(100 * attempts);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to create AudiobookFile record for audiobook {AudiobookId} at {Path}", audiobookId, LogRedaction.SanitizeFilePath(filePath));
                return false;
            }
        }

        /// <summary>
        /// Re-extracts metadata from an already-tracked file and backfills any blank library-level
        /// fields on the parent audiobook record. Used by the force-refresh scan flow to enrich
        /// existing books without touching their AudiobookFile rows.
        /// </summary>
        private async Task RefreshAudiobookMetadataFromFileAsync(
            int audiobookId,
            string filePath,
            IServiceScope scope,
            IMetadataService metadataService,
            IAudiobookRepository audiobookRepository)
        {
            if (!File.Exists(filePath))
            {
                _logger.LogDebug("Skipping force-refresh: file missing on disk for audiobook {AudiobookId} at {Path}", audiobookId, LogRedaction.SanitizeFilePath(filePath));
                return;
            }

            AudioMetadata? meta = null;
            try
            {
                using var _ = await _limiter.Sem.LockAsync();
                meta = await metadataService.ExtractFileMetadataAsync(filePath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Force-refresh: metadata extraction failed for {Path}", LogRedaction.SanitizeFilePath(filePath));
                return;
            }
            if (meta == null) return;

            var audiobook = await audiobookRepository.GetByIdAsync(audiobookId);
            if (audiobook == null) return;

            var mutated = await PromoteLocalMetadataAsync(audiobook, meta, scope, filePath);
            if (mutated)
            {
                await audiobookRepository.UpdateAsync(audiobook);
            }
        }

        /// <summary>
        /// Fills blank library-level fields on the audiobook record from extracted file metadata,
        /// and extracts an embedded cover into library storage when the audiobook has no image yet.
        /// Never overwrites existing non-blank fields. Audiobook is mutated in place; caller persists.
        /// Returns true if any field was changed (caller can use this to skip a no-op UpdateAsync).
        /// </summary>
        private async Task<bool> PromoteLocalMetadataAsync(Audiobook audiobook, AudioMetadata meta, IServiceScope scope, string filePath)
        {
            var anyChange = PromoteBlankFieldsFromMetadata(audiobook, meta, out var identifiersChanged);

            // Cover extraction is gated on: (a) audiobook has no image yet, (b) ffprobe reported
            // an embedded picture stream. Both keep us from running TagLib# for every file in a
            // multi-file book once the cover has been pulled from the first one.
            var hasAttachedPic = meta.AdditionalData != null && meta.AdditionalData.ContainsKey("AttachedPicCodec");
            if (string.IsNullOrWhiteSpace(audiobook.ImageUrl) && hasAttachedPic)
            {
                try
                {
                    var metadataService = scope.ServiceProvider.GetRequiredService<IMetadataService>();
                    var imageCache = scope.ServiceProvider.GetService<IImageCacheService>();
                    if (imageCache != null)
                    {
                        var (bytes, ext) = await metadataService.ExtractEmbeddedCoverAsync(filePath);
                        if (bytes != null && bytes.Length > 0)
                        {
                            var identifier = !string.IsNullOrWhiteSpace(audiobook.Asin)
                                ? audiobook.Asin!
                                : "audiobook-" + audiobook.Id;
                            var stored = await imageCache.StoreLibraryImageBytesAsync(identifier, bytes, ext ?? ".jpg");
                            if (!string.IsNullOrWhiteSpace(stored))
                            {
                                audiobook.ImageUrl = "/" + stored;
                                anyChange = true;
                                _logger.LogInformation("Promoted embedded cover art to audiobook {AudiobookId} from {File}", audiobook.Id, LogRedaction.SanitizeFilePath(filePath));
                            }
                        }
                    }
                }
                catch (Exception coverEx) when (coverEx is not OperationCanceledException && coverEx is not OutOfMemoryException && coverEx is not StackOverflowException)
                {
                    _logger.LogDebug(coverEx, "Embedded cover promotion failed for audiobook {AudiobookId} file {File}", audiobook.Id, LogRedaction.SanitizeFilePath(filePath));
                }
            }

            if (identifiersChanged)
            {
                AudiobookIdentifierSync.Sync(audiobook, AudiobookExternalIdentifierSource.Imported);
            }

            return anyChange;
        }

        /// <summary>
        /// Pure-function promotion of file-tag values into blank Audiobook fields.
        /// Returns true if any field was mutated. Identifier-bearing fields surface separately
        /// via <paramref name="identifiersChanged"/> so callers can re-sync ExternalIdentifiers.
        /// </summary>
        internal static bool PromoteBlankFieldsFromMetadata(Audiobook audiobook, AudioMetadata meta) =>
            PromoteBlankFieldsFromMetadata(audiobook, meta, out _);

        internal static bool PromoteBlankFieldsFromMetadata(Audiobook audiobook, AudioMetadata meta, out bool identifiersChanged)
        {
            identifiersChanged = false;
            if (audiobook == null || meta == null) return false;

            var anyChange = false;

            if (string.IsNullOrWhiteSpace(audiobook.Title) && !string.IsNullOrWhiteSpace(meta.Title))
            { audiobook.Title = meta.Title; anyChange = true; }
            if (string.IsNullOrWhiteSpace(audiobook.Subtitle) && !string.IsNullOrWhiteSpace(meta.Subtitle))
            { audiobook.Subtitle = meta.Subtitle; anyChange = true; }
            if (string.IsNullOrWhiteSpace(audiobook.Series) && !string.IsNullOrWhiteSpace(meta.Series))
            { audiobook.Series = meta.Series; anyChange = true; }
            if (string.IsNullOrWhiteSpace(audiobook.SeriesNumber) && meta.SeriesPosition.HasValue)
            { audiobook.SeriesNumber = meta.SeriesPosition.Value.ToString(System.Globalization.CultureInfo.InvariantCulture); anyChange = true; }
            if (string.IsNullOrWhiteSpace(audiobook.Publisher) && !string.IsNullOrWhiteSpace(meta.Publisher))
            { audiobook.Publisher = meta.Publisher; anyChange = true; }
            if (string.IsNullOrWhiteSpace(audiobook.Language) && !string.IsNullOrWhiteSpace(meta.Language))
            { audiobook.Language = meta.Language; anyChange = true; }
            if (string.IsNullOrWhiteSpace(audiobook.Description) && !string.IsNullOrWhiteSpace(meta.Description))
            { audiobook.Description = meta.Description; anyChange = true; }
            if (string.IsNullOrWhiteSpace(audiobook.PublishYear) && meta.Year.HasValue)
            { audiobook.PublishYear = meta.Year.Value.ToString(System.Globalization.CultureInfo.InvariantCulture); anyChange = true; }

            if ((audiobook.Authors == null || audiobook.Authors.Count == 0))
            {
                var candidate = FirstNonEmpty(meta.AlbumArtist, meta.Artist);
                if (!string.IsNullOrWhiteSpace(candidate) &&
                    !(meta.Narrator != null && string.Equals(candidate.Trim(), meta.Narrator.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    audiobook.Authors = SplitListTag(candidate);
                    anyChange = true;
                }
            }

            if ((audiobook.Narrators == null || audiobook.Narrators.Count == 0) && !string.IsNullOrWhiteSpace(meta.Narrator))
            {
                audiobook.Narrators = SplitListTag(meta.Narrator);
                anyChange = true;
            }

            if (string.IsNullOrWhiteSpace(audiobook.Asin) && !string.IsNullOrWhiteSpace(meta.Asin) &&
                AudiobookIdentifierNormalizer.TryNormalize(AudiobookExternalIdentifierType.Asin, meta.Asin, out var normalizedAsin, out _))
            {
                audiobook.Asin = normalizedAsin;
                identifiersChanged = true;
                anyChange = true;
            }

            if ((audiobook.Isbn == null || audiobook.Isbn.Count == 0) && !string.IsNullOrWhiteSpace(meta.Isbn) &&
                AudiobookIdentifierNormalizer.TryNormalize(AudiobookExternalIdentifierType.Isbn, meta.Isbn, out var normalizedIsbn, out _))
            {
                audiobook.Isbn = new List<string> { normalizedIsbn };
                identifiersChanged = true;
                anyChange = true;
            }

            return anyChange;
        }

        // Splits only on unambiguous list separators. Commas are excluded because
        // "Last, First"-style single-author tags are common and would split into two authors.
        private static List<string> SplitListTag(string value) =>
            value.Split(new[] { ';', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

        private static string? FirstNonEmpty(params string?[] candidates)
        {
            foreach (var c in candidates.Where(static c => !string.IsNullOrWhiteSpace(c))) return c;
            return null;
        }

        private static string? NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return FileUtils.NormalizeStoredPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                return null;
            }
        }
    }
}
