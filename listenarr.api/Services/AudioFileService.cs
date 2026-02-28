using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using System.IO;

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

        public async Task<bool> EnsureAudiobookFileAsync(int audiobookId, string filePath, string? source = "scan")
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
                var metadataService = scope.ServiceProvider.GetRequiredService<IMetadataService>();

                // Check for existing
                var exists = await db.AudiobookFiles.AnyAsync(x => x.AudiobookId == audiobookId && x.Path == filePath);
                if (exists)
                {
                    _logger.LogDebug("AudiobookFile already exists for audiobook {AudiobookId} at path {Path}", audiobookId, filePath);
                    return false;
                }

                // Conservative safety: if the audiobook already has a stored FilePath (legacy
                // single-file representation) prefer to only associate files that live in the
                // same containing directory. This prevents accidental associations when a
                // completed download move erroneously places a file in a sibling folder.
                // However, allow files in the audiobook's BasePath (multi-file import scenario).
                try
                {
                    var audiobook = await db.Audiobooks.FindAsync(audiobookId);
                    if (audiobook != null && !string.IsNullOrWhiteSpace(audiobook.FilePath))
                    {
                        var existingDir = Path.GetFullPath(Path.GetDirectoryName(audiobook.FilePath) ?? string.Empty);
                        var candidateDir = Path.GetFullPath(Path.GetDirectoryName(filePath) ?? string.Empty);
                        var candidateFull = Path.GetFullPath(filePath);

                        if (!string.IsNullOrEmpty(existingDir) && !string.IsNullOrEmpty(candidateDir))
                        {
                            // Ensure candidate is the same directory or a subdirectory of the existing dir
                            var isInExistingDir = candidateDir.Equals(existingDir, StringComparison.OrdinalIgnoreCase) ||
                                                   candidateDir.StartsWith(existingDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                            
                            // Also allow if file is within the audiobook's BasePath (multi-file migration)
                            var isInBasePath = !string.IsNullOrWhiteSpace(audiobook.BasePath) &&
                                               candidateFull.StartsWith(Path.GetFullPath(audiobook.BasePath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

                            if (!isInExistingDir && !isInBasePath)
                            {
                                var audiobookTitle = audiobook.Title ?? "Unknown";
                                _logger.LogWarning("Refusing to associate file outside audiobook folder. AudiobookId={AudiobookId}, AudiobookDir={AudiobookDir}, BasePath={BasePath}, File={File}", audiobookId, existingDir, audiobook.BasePath, filePath);
                                // Create a history entry so the UI can show that an attempted association was refused
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

                                    db.History.Add(historyEntry);
                                    await db.SaveChangesAsync();

                                    // Broadcast a UI toast message so clients see immediate feedback
                                    try
                                    {
                                        var toastSvc = scope.ServiceProvider.GetService<IToastService>();
                                        if (toastSvc != null)
                                        {
                                            await toastSvc.PublishToastAsync("warning", "File not associated", $"Refused to associate {Path.GetFileName(filePath)} to {audiobookTitle}");
                                        }
                                    }
                                    catch (Exception thx) when (thx is not OperationCanceledException && thx is not OutOfMemoryException && thx is not StackOverflowException) {
                                        _logger.LogDebug(thx, "Failed to publish toast for refused file association");
                                    }
                                }
                                catch (Exception hx) when (hx is not OperationCanceledException && hx is not OutOfMemoryException && hx is not StackOverflowException) {
                                    _logger.LogDebug(hx, "Failed to persist history for refused file association (AudiobookId={AudiobookId}, File={File})", audiobookId, filePath);
                                }

                                return false;
                            }
                        }
                    }
                }
                catch (Exception exDir) when (exDir is not OperationCanceledException && exDir is not OutOfMemoryException && exDir is not StackOverflowException) {
                    _logger.LogDebug(exDir, "Failed to verify audiobook folder containment for AudiobookId={AudiobookId} File={File}", audiobookId, filePath);
                }

                AudioMetadata? meta = null;
                try
                {
                    // Use file last write time as part of cache key so updates invalidate
                    var fileInfoForCache = new FileInfo(filePath);
                    var ticks = fileInfoForCache.Exists ? fileInfoForCache.LastWriteTimeUtc.Ticks : 0L;
                    var cacheKey = $"meta::{filePath}::{ticks}";
                    if (!_memoryCache.TryGetValue(cacheKey, out var cachedObj) || !(cachedObj is AudioMetadata cachedMeta))
                    {
                        await _limiter.Sem.WaitAsync();
                        try
                        {
                            meta = await metadataService.ExtractFileMetadataAsync(filePath);
                            // Cache for 5 minutes
                            _memoryCache.Set(cacheKey, meta, TimeSpan.FromMinutes(5));
                        }
                        finally
                        {
                            _limiter.Sem.Release();
                        }
                    }
                    else
                    {
                        meta = cachedMeta;
                    }
                }
                catch (Exception mEx) when (mEx is not OperationCanceledException && mEx is not OutOfMemoryException && mEx is not StackOverflowException) {
                    _logger.LogInformation(mEx, "Metadata extraction failed for {Path}", filePath);
                }
                // If metadata extraction produced minimal results, attempt to ensure ffprobe is installed
                // and retry extraction once. This helps scans capture technical metadata even when ffprobe
                // wasn't available at startup. We keep the retry short to avoid blocking scans for too long.
                try
                {
                    var needRetry = meta == null || (meta.Duration == TimeSpan.Zero && string.IsNullOrEmpty(meta?.Format));
                    if (needRetry)
                    {
                        using var scope2 = _scopeFactory.CreateScope();
                        var ffmpegSvc = scope2.ServiceProvider.GetService<IFfmpegService>();
                        if (ffmpegSvc != null)
                        {
                            // Try to ensure ffprobe is installed, but don't wait indefinitely. Use a short timeout.
                            var installTask = ffmpegSvc.EnsureFfprobeInstalledAsync();
                            var completed = await Task.WhenAny(installTask, Task.Delay(TimeSpan.FromSeconds(10)));
                            if (completed == installTask)
                            {
                                try
                                {
                                    var ffpath = await installTask; // may be null
                                    if (!string.IsNullOrEmpty(ffpath))
                                    {
                                        // Retry metadata extraction once under limiter
                                        await _limiter.Sem.WaitAsync();
                                        try
                                        {
                                            meta = await metadataService.ExtractFileMetadataAsync(filePath);
                                            // Update cache
                                            var fileInfoForCache2 = new FileInfo(filePath);
                                            var ticks2 = fileInfoForCache2.Exists ? fileInfoForCache2.LastWriteTimeUtc.Ticks : 0L;
                                            var cacheKey2 = $"meta::{filePath}::{ticks2}";
                                            _memoryCache.Set(cacheKey2, meta, TimeSpan.FromMinutes(5));
                                        }
                                        finally { _limiter.Sem.Release(); }
                                    }
                                }
                                catch (Exception rex) when (rex is not OperationCanceledException && rex is not OutOfMemoryException && rex is not StackOverflowException) {
                                    _logger.LogInformation(rex, "Retry metadata extraction failed for {Path}", filePath);
                                }
                            }
                        }
                    }
                }
                catch (Exception exRetry) when (exRetry is not OperationCanceledException && exRetry is not OutOfMemoryException && exRetry is not StackOverflowException) {
                    _logger.LogDebug(exRetry, "Non-fatal error while attempting ffprobe install/retry for {Path}", filePath);
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
                    Bitrate = meta?.Bitrate,
                    SampleRate = meta?.SampleRate,
                    Channels = meta?.Channels
                };

                db.AudiobookFiles.Add(fileRecord);
                // Retry on unique constraint violation to avoid race conditions
                var attempts = 0;
                while (true)
                {
                    try
                    {
                        await db.SaveChangesAsync();
                        try
                        {
                            var conn = db.Database.GetDbConnection();
                            _logger.LogInformation("Created AudiobookFile for audiobook {AudiobookId}: {Path} (Db: {Db}) Id={Id}", audiobookId, filePath, conn?.ConnectionString, fileRecord.Id);
                        }
                        catch (Exception logEx) when (logEx is not OperationCanceledException && logEx is not OutOfMemoryException && logEx is not StackOverflowException) {
                            _logger.LogInformation("Created AudiobookFile for audiobook {AudiobookId}: {Path} (Db: unknown) Id={Id}", audiobookId, filePath, fileRecord.Id);
                            _logger.LogDebug(logEx, "Failed to log DB connection string for AudiobookFile creation");
                        }

                        // Add history entry for file creation so scans/downloads show in the UI
                        try
                        {
                            // Retrieve audiobook title for denormalized display
                            var audiobook = await db.Audiobooks.FindAsync(audiobookId);
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
                            db.History.Add(historyEntry);
                            await db.SaveChangesAsync();

                            // Update audiobook single-file fields so the frontend recognizes the
                            // audiobook as having a file (the UI currently checks `filePath`).
                            //
                            // NOTE: `FilePath`/`FileSize` are kept for backward compatibility with
                            // the existing frontend and older DTOs. The canonical representation
                            // is the `Files` collection (multi-file support). We populate the
                            // single-file fields here to avoid regressions in the Wanted view
                            // and other UI consumers which still rely on `filePath`. When the
                            // frontend is updated to prefer `Files` this compatibility layer can
                            // be removed.
                            try
                            {
                                var audiobookToUpdate = await db.Audiobooks.FindAsync(audiobookId);
                                if (audiobookToUpdate != null)
                                {
                                    // Prefer to populate FilePath/FileSize for backward compatibility
                                    audiobookToUpdate.FilePath = fileRecord.Path;
                                    audiobookToUpdate.FileSize = fileRecord.Size;

                                    // Persist change; keep it quiet on errors
                                    db.Audiobooks.Update(audiobookToUpdate);
                                    await db.SaveChangesAsync();
                                }
                            }
                            catch (Exception aubEx) when (aubEx is not OperationCanceledException && aubEx is not OutOfMemoryException && aubEx is not StackOverflowException) {
                                _logger.LogDebug(aubEx, "Failed to update Audiobook file summary fields for AudiobookId {AudiobookId}", audiobookId);
                            }
                        }
                        catch (Exception hx) when (hx is not OperationCanceledException && hx is not OutOfMemoryException && hx is not StackOverflowException) {
                            _logger.LogDebug(hx, "Failed to create history entry for added audiobook file {Path}", filePath);
                        }

                        return true;
                    }
                    catch (DbUpdateException dbEx)
                    {
                        attempts++;
                        // If the exception is due to unique constraint (another worker inserted it), treat as already created
                        var inner = dbEx.InnerException?.Message ?? dbEx.Message;
                        if (inner != null && inner.IndexOf("UNIQUE", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            _logger.LogInformation("AudiobookFile insertion conflict detected (likely already created): {Path}", filePath);
                            return false;
                        }
                        if (attempts >= 3)
                        {
                            _logger.LogWarning(dbEx, "Failed to save AudiobookFile after {Attempts} attempts: {Path}", attempts, filePath);
                            return false;
                        }
                        // small backoff
                        await Task.Delay(100 * attempts);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogWarning(ex, "Failed to create AudiobookFile record for audiobook {AudiobookId} at {Path}", audiobookId, filePath);
                return false;
            }
        }
    }
}


