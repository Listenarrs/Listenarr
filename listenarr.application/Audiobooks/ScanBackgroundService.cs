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
using System.Text.Json;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Application.Mapping;
using Listenarr.Application.Notification;
using Listenarr.Application.Security;
using Listenarr.Domain.Common;
using Listenarr.Domain.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks
{
    public class ScanBackgroundService : BackgroundService
    {
        private readonly IScanQueueService _queue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ScanBackgroundService> _logger;
        private readonly IHubContext<DownloadHub> _hubContext;

        public ScanBackgroundService(IScanQueueService queue, IServiceScopeFactory scopeFactory, ILogger<ScanBackgroundService> logger, IHubContext<DownloadHub> hubContext)
        {
            _queue = queue;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _hubContext = hubContext;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("ScanBackgroundService started");
            if (_queue is ScanQueueService sq)
            {
                var reader = sq.Reader;
                _logger.LogInformation("ScanBackgroundService awaiting jobs from queue");
                try
                {
                    await foreach (var job in reader.ReadAllAsync(stoppingToken))
                    {
                        _logger.LogDebug("Dequeued scan job {JobId} from channel", job.Id);
                        try
                        {
                            _logger.LogInformation("Processing scan job {JobId} for audiobook {AudiobookId}", job.Id, job.AudiobookId);
                            // notify clients that job is now processing
                            try
                            {
                                await _hubContext.Clients.All.SendAsync("ScanJobUpdate", new { jobId = job.Id.ToString(), audiobookId = job.AudiobookId, status = "Processing", startedAt = DateTime.UtcNow });
                            }
                            catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException)
                            {
                                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                            }
                            // update in-memory job status
                            try { _queue.UpdateJobStatus(job.Id, "Processing"); }
                            catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException)
                            {
                                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                            }
                            using var scope = _scopeFactory.CreateScope();
                            var audiobookRepository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
                            var fileRepository = scope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
                            var historyRepository = scope.ServiceProvider.GetRequiredService<IHistoryRepository>();
                            var audiobook = await audiobookRepository.GetByIdAsync(job.AudiobookId);
                            if (audiobook == null)
                            {
                                _logger.LogWarning("Audiobook {Id} not found for scan job {JobId}", job.AudiobookId, job.Id);
                                continue;
                            }

                            var scanRoot = job.Path;
                            var usedBasePath = false;

                            // If audiobook has a BasePath configured, always scan that path for safety
                            // and to avoid scanning the global output root which may be large/unrelated.
                            if (!string.IsNullOrEmpty(audiobook.BasePath))
                            {
                                scanRoot = audiobook.BasePath;
                                usedBasePath = true;
                                _logger.LogDebug("Using audiobook BasePath as scan root for job {JobId}: {ScanRoot}", job.Id, scanRoot);
                            }
                            else
                            {
                                // No BasePath - allow explicit job path, otherwise fall back to settings OutputPath
                                if (string.IsNullOrEmpty(scanRoot))
                                {
                                    try
                                    {
                                        var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
                                        var settings = await configService.GetApplicationSettingsAsync();
                                        scanRoot = settings.OutputPath;
                                    }
                                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                    {
                                        _logger.LogWarning(ex, "Failed to read settings for scan job {JobId}", job.Id);
                                    }
                                }
                            }

                            if (usedBasePath && (string.IsNullOrEmpty(scanRoot) || !Directory.Exists(scanRoot)))
                            {
                                _logger.LogWarning("Audiobook BasePath missing for job {JobId}: {Path}. Removing tracked files.", job.Id, LogRedaction.SanitizeFilePath(scanRoot));

                                try
                                {
                                    var existingFiles = await fileRepository.GetByAudiobookIdAsync(audiobook.Id);

                                    List<object> removedFilesDto = new();
                                    if (existingFiles.Count > 0)
                                    {
                                        foreach (var rem in existingFiles)
                                        {
                                            removedFilesDto.Add(new { id = rem.Id, path = rem.Path });
                                            await fileRepository.DeleteAsync(rem.Id);
                                            _logger.LogInformation("Removing AudiobookFile DB row Id={Id} Path={Path} due to missing BasePath", rem.Id, LogRedaction.SanitizeFilePath(rem.Path));

                                            var historyEntry = new History
                                            {
                                                AudiobookId = audiobook.Id,
                                                AudiobookTitle = audiobook.Title ?? "Unknown",
                                                EventType = "File Removed",
                                                Message = "File removed (base path missing)",
                                                Source = "Scan",
                                                Data = JsonSerializer.Serialize(new
                                                {
                                                    FilePath = rem.Path,
                                                    FileSize = rem.Size,
                                                    Format = rem.Format,
                                                    Source = rem.Source
                                                }),
                                                Timestamp = DateTime.UtcNow
                                            };
                                            await historyRepository.AddAsync(historyEntry);
                                        }
                                    }

                                    audiobook.BasePath = null;
                                    await audiobookRepository.UpdateAsync(audiobook);

                                    if (removedFilesDto.Count > 0)
                                    {
                                        try
                                        {
                                            await _hubContext.Clients.All.SendAsync("FilesRemoved", new { audiobookId = audiobook.Id, removed = removedFilesDto });
                                        }
                                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                        {
                                            _logger.LogDebug(ex, "Failed to broadcast FilesRemoved event for audiobook {AudiobookId}", audiobook.Id);
                                        }
                                    }

                                    try
                                    {
                                        var audiobookDto = AudiobookDtoFactory.BuildFromEntity(audiobook);
                                        await _hubContext.Clients.All.SendAsync("AudiobookUpdate", audiobookDto);
                                    }
                                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                    {
                                        _logger.LogDebug(ex, "Failed to broadcast AudiobookUpdate for audiobook {AudiobookId}", audiobook.Id);
                                    }

                                    try { _queue.UpdateJobStatus(job.Id, "Completed"); }
                                    catch (Exception caughtEx_3) when (caughtEx_3 is not OperationCanceledException && caughtEx_3 is not OutOfMemoryException && caughtEx_3 is not StackOverflowException)
                                    {
                                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                    }
                                    try { await _hubContext.Clients.All.SendAsync("ScanJobUpdate", new { jobId = job.Id.ToString(), audiobookId = job.AudiobookId, status = "Completed", found = 0, created = 0, completedAt = DateTime.UtcNow }); }
                                    catch (Exception caughtEx_4) when (caughtEx_4 is not OperationCanceledException && caughtEx_4 is not OutOfMemoryException && caughtEx_4 is not StackOverflowException)
                                    {
                                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                    }
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                {
                                    _logger.LogWarning(ex, "Failed to remove audiobook files for missing BasePath (job {JobId})", job.Id);
                                    try { _queue.UpdateJobStatus(job.Id, "Failed", "BasePath missing"); }
                                    catch (Exception caughtEx_5) when (caughtEx_5 is not OperationCanceledException && caughtEx_5 is not OutOfMemoryException && caughtEx_5 is not StackOverflowException)
                                    {
                                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                    }
                                    try { await _hubContext.Clients.All.SendAsync("ScanJobUpdate", new { jobId = job.Id.ToString(), audiobookId = job.AudiobookId, status = "Failed", error = "BasePath missing", failedAt = DateTime.UtcNow }); }
                                    catch (Exception caughtEx_6) when (caughtEx_6 is not OperationCanceledException && caughtEx_6 is not OutOfMemoryException && caughtEx_6 is not StackOverflowException)
                                    {
                                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                    }
                                }
                                continue;
                            }

                            if (string.IsNullOrEmpty(scanRoot) || !Directory.Exists(scanRoot))
                            {
                                _logger.LogWarning("Scan path not found for job {JobId}: {Path}", job.Id, LogRedaction.SanitizeFilePath(scanRoot));
                                continue;
                            }

                            var titleToken = (audiobook.Title ?? string.Empty).Replace("\"", string.Empty).Trim();
                            var authorToken = audiobook.Authors?.FirstOrDefault() ?? string.Empty;

                            var exts = FileUtils.AudioExtensions;

                            // Collect candidate audio files first
                            var candidates = new List<string>();
                            // Walk directories iteratively and safely, catching IO/Access exceptions per directory.
                            var dirs = new Stack<string>();
                            dirs.Push(scanRoot);

                            while (dirs.Count > 0)
                            {
                                var dir = dirs.Pop();
                                try
                                {
                                    // normalize path to full path to avoid odd relative issues
                                    var normalizedDir = Path.GetFullPath(dir);

                                    // enumerate files in this directory
                                    foreach (var file in Directory.EnumerateFiles(normalizedDir))
                                    {
                                        try
                                        {
                                            var ext = Path.GetExtension(file);
                                            if (!exts.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
                                            candidates.Add(file);
                                        }
                                        catch (Exception innerFileEx) when (innerFileEx is not OperationCanceledException && innerFileEx is not OutOfMemoryException && innerFileEx is not StackOverflowException)
                                        {
                                            _logger.LogDebug(innerFileEx, "Skipped file while scanning {Dir}", normalizedDir);
                                            continue;
                                        }
                                    }

                                    // enqueue subdirectories
                                    foreach (var sub in Directory.EnumerateDirectories(normalizedDir))
                                    {
                                        dirs.Push(sub);
                                    }
                                }
                                catch (System.IO.IOException ioEx)
                                {
                                    _logger.LogWarning(ioEx, "IO error while enumerating directory for scan job {JobId}: {Dir}", job.Id, dir);
                                    // don't fail the whole job - continue scanning other directories
                                    continue;
                                }
                                catch (UnauthorizedAccessException uaEx)
                                {
                                    _logger.LogWarning(uaEx, "Access denied while enumerating directory for scan job {JobId}: {Dir}", job.Id, dir);
                                    continue;
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                {
                                    _logger.LogWarning(ex, "Unexpected error while enumerating directory for scan job {JobId}: {Dir}", job.Id, dir);
                                    continue;
                                }
                            }

                            // Decide which candidates belong to this audiobook.
                            // Strategy: if title/author tokens are empty, accept all candidates.
                            // Otherwise, group candidates by parent directory; if any file or the directory name matches the title/author token, accept the whole directory (useful for disc/chapter sets).
                            var foundFiles = new List<string>();
                            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                            if (candidates.Count > 0)
                            {
                                if (string.IsNullOrEmpty(titleToken) && string.IsNullOrEmpty(authorToken))
                                {
                                    foundFiles.AddRange(candidates.Where(unique.Add));
                                }
                                else
                                {
                                    var groups = candidates.GroupBy(f => Path.GetDirectoryName(f) ?? string.Empty);
                                    foreach (var group in groups)
                                    {
                                        var dirName = Path.GetFileName(group.Key) ?? string.Empty;
                                        var groupHasMatch = group.Any(f =>
                                        {
                                            bool fileNameMatchesTitle = !string.IsNullOrEmpty(titleToken) && Path.GetFileNameWithoutExtension(f).IndexOf(titleToken, StringComparison.OrdinalIgnoreCase) >= 0;
                                            bool filePathMatchesAuthor = !string.IsNullOrEmpty(authorToken) && f.IndexOf(authorToken, StringComparison.OrdinalIgnoreCase) >= 0;
                                            bool dirMatchesTitle = !string.IsNullOrEmpty(titleToken) && dirName.IndexOf(titleToken, StringComparison.OrdinalIgnoreCase) >= 0;
                                            return fileNameMatchesTitle || filePathMatchesAuthor || dirMatchesTitle;
                                        });

                                        if (groupHasMatch)
                                        {
                                            foundFiles.AddRange(group.Where(unique.Add));
                                        }
                                        else
                                        {
                                            // include files that individually match
                                            foreach (var f in group)
                                            {
                                                var fname = Path.GetFileNameWithoutExtension(f);
                                                var matchesTitle = !string.IsNullOrEmpty(titleToken)
                                                    && fname.IndexOf(titleToken, StringComparison.OrdinalIgnoreCase) >= 0;
                                                var matchesAuthor = !string.IsNullOrEmpty(authorToken)
                                                    && f.IndexOf(authorToken, StringComparison.OrdinalIgnoreCase) >= 0;
                                                if ((matchesTitle || matchesAuthor) && unique.Add(f)) foundFiles.Add(f);
                                            }
                                        }
                                    }
                                }
                            }

                            // Calculate base path for the audiobook files
                            var basePath = CalculateBasePath(foundFiles);
                            if (!string.IsNullOrEmpty(basePath))
                            {
                                var basePathChanged = !string.Equals(audiobook.BasePath, basePath, StringComparison.OrdinalIgnoreCase);
                                audiobook.BasePath = basePath;
                                _logger.LogInformation("Set base path for audiobook '{Title}' (ID: {AudiobookId}): {BasePath}", LogRedaction.SanitizeText(audiobook.Title), audiobook.Id, LogRedaction.SanitizeFilePath(basePath));

                                // Persist the recalculated base path before delegating to AudioFileService.
                                // That service resolves the audiobook in a separate scope/db context and
                                // uses BasePath for containment checks, so delayed SaveChanges can cause
                                // legitimate sibling parts to be rejected during multifile scans.
                                if (basePathChanged)
                                {
                                    await audiobookRepository.UpdateAsync(audiobook);
                                }
                            }

                            var createdFiles = 0;
                            foreach (var filePath in foundFiles)
                            {
                                try
                                {
                                    using var afScope = _scopeFactory.CreateScope();
                                    var audioFileService = afScope.ServiceProvider.GetRequiredService<IAudiobookFileService>();

                                    // Store absolute path - metadata extraction needs full path
                                    var created = await audioFileService.EnsureAudiobookFileAsync(audiobook, filePath, "scan");
                                    if (created) createdFiles++;
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                {
                                    _logger.LogWarning(ex, "Failed to add file {File} during scan job {JobId}", filePath, job.Id);
                                }
                            }

                            // Remove AudiobookFile DB rows for files that no longer exist on disk
                            try
                            {
                                var existingFiles = await fileRepository.GetByAudiobookIdAsync(audiobook.Id);

                                // Create set of found files (absolute paths)
                                var foundSet = new HashSet<string>(foundFiles, StringComparer.OrdinalIgnoreCase);

                                // Check which existing files still exist
                                var toRemove = new List<AudiobookFile>();
                                foreach (var existingFile in existingFiles
                                    .Where(existingFile => !string.IsNullOrEmpty(existingFile.Path))
                                    .Where(existingFile => FileUtils.IsAudioFile(existingFile.Path!)))
                                {
                                    // Normalize path: if relative, make it absolute using basePath
                                    var fullPath = existingFile.Path!;
                                    if (!Path.IsPathRooted(fullPath) && !string.IsNullOrEmpty(basePath))
                                    {
                                        fullPath = Path.GetFullPath(Path.Join(basePath, fullPath));
                                    }

                                    // Check if file still exists on disk
                                    if (!foundSet.Contains(fullPath))
                                    {
                                        toRemove.Add(existingFile);
                                    }
                                }

                                List<object> removedFilesDto = new();
                                if (toRemove.Count > 0)
                                {
                                    foreach (var rem in toRemove)
                                    {
                                        try
                                        {
                                            removedFilesDto.Add(new { id = rem.Id, path = rem.Path });
                                            await fileRepository.DeleteAsync(rem.Id);
                                            _logger.LogInformation("Removing missing AudiobookFile DB row Id={Id} Path={Path}", rem.Id, LogRedaction.SanitizeFilePath(rem.Path));

                                            // Add history entry for removed file
                                            var historyEntry = new History
                                            {
                                                AudiobookId = audiobook.Id,
                                                AudiobookTitle = audiobook.Title ?? "Unknown",
                                                EventType = "File Removed",
                                                Message = $"File removed (no longer exists): {Path.GetFileName(rem.Path)}",
                                                Source = "Scan",
                                                Data = JsonSerializer.Serialize(new
                                                {
                                                    FilePath = rem.Path,
                                                    FileSize = rem.Size,
                                                    Format = rem.Format,
                                                    Source = rem.Source
                                                }),
                                                Timestamp = DateTime.UtcNow
                                            };
                                            await historyRepository.AddAsync(historyEntry);
                                        }
                                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                        {
                                            _logger.LogWarning(ex, "Failed to remove AudiobookFile Id={Id} Path={Path}", rem.Id, LogRedaction.SanitizeFilePath(rem.Path));
                                        }
                                    }

                                    // Broadcast a friendly message about removed files so UI can show a notice
                                    try
                                    {
                                        await _hubContext.Clients.All.SendAsync("FilesRemoved", new { audiobookId = audiobook.Id, removed = removedFilesDto });
                                    }
                                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                    {
                                        _logger.LogDebug(ex, "Failed to broadcast FilesRemoved event for audiobook {AudiobookId}", audiobook.Id);
                                    }
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                _logger.LogWarning(ex, "Failed to reconcile audiobook files after scan job {JobId}", job.Id);
                            }

                            // Handle legacy filePath field migration
                            try
                            {
                                var needsUpdate = false;
                                if (!string.IsNullOrEmpty(audiobook.FilePath))
                                {
                                    // Check if the legacy filePath exists
                                    if (System.IO.File.Exists(audiobook.FilePath))
                                    {
                                        // File exists - check if we already have an AudiobookFile record for it
                                        var alreadyExists = await fileRepository.ExistsAtPathAsync(audiobook.Id, audiobook.FilePath);
                                        var existingFileRecord = alreadyExists ? new AudiobookFile() : null;

                                        if (existingFileRecord == null)
                                        {
                                            // Create AudiobookFile record for the legacy filePath
                                            try
                                            {
                                                using var afScope = _scopeFactory.CreateScope();
                                                var audioFileService = afScope.ServiceProvider.GetRequiredService<IAudiobookFileService>();
                                                var created = await audioFileService.EnsureAudiobookFileAsync(audiobook, audiobook.FilePath, "scan-legacy");
                                                if (created)
                                                {
                                                    _logger.LogInformation("Migrated legacy filePath to AudiobookFile record for audiobook {AudiobookId}: {Path}", audiobook.Id, LogRedaction.SanitizeFilePath(audiobook.FilePath));
                                                    createdFiles++;
                                                }
                                            }
                                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                            {
                                                _logger.LogWarning(ex, "Failed to migrate legacy filePath for audiobook {AudiobookId}: {Path}", audiobook.Id, LogRedaction.SanitizeFilePath(audiobook.FilePath));
                                            }
                                        }
                                    }
                                    else
                                    {
                                        // File doesn't exist - clear the legacy filePath and related fields
                                        audiobook.FilePath = null;
                                        audiobook.FileSize = null;
                                        needsUpdate = true;
                                        _logger.LogInformation("Cleared missing legacy filePath for audiobook {AudiobookId}: {Path}", audiobook.Id, LogRedaction.SanitizeFilePath(audiobook.FilePath));

                                        // Add history entry for cleared filePath
                                        var historyEntry = new History
                                        {
                                            AudiobookId = audiobook.Id,
                                            AudiobookTitle = audiobook.Title ?? "Unknown",
                                            EventType = "File Removed",
                                            Message = $"Legacy file path cleared (file no longer exists)",
                                            Source = "Scan",
                                            Data = JsonSerializer.Serialize(new
                                            {
                                                FilePath = audiobook.FilePath,
                                                Source = "legacy-migration"
                                            }),
                                            Timestamp = DateTime.UtcNow
                                        };
                                        await historyRepository.AddAsync(historyEntry);
                                    }
                                }

                                if (needsUpdate)
                                {
                                    await audiobookRepository.UpdateAsync(audiobook);
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                _logger.LogWarning(ex, "Failed to handle legacy filePath migration for audiobook {AudiobookId}", audiobook.Id);
                            }

                            // Send "book-available" notification if the audiobook is monitored and files were imported
                            if (audiobook.Monitored && createdFiles > 0)
                            {
                                try
                                {
                                    using var notificationScope = _scopeFactory.CreateScope();
                                    var notificationService = notificationScope.ServiceProvider.GetService<NotificationService>();
                                    var configService = notificationScope.ServiceProvider.GetRequiredService<IConfigurationService>();
                                    var settings = await configService.GetApplicationSettingsAsync();
                                    var availableData = new
                                    {
                                        id = audiobook.Id,
                                        title = audiobook.Title ?? "Unknown Title",
                                        authors = audiobook.Authors,
                                        asin = audiobook.Asin,
                                        imageUrl = audiobook.ImageUrl,
                                        description = audiobook.Description,
                                        monitored = audiobook.Monitored,
                                        qualityProfileId = audiobook.QualityProfileId,
                                        filesImported = createdFiles,
                                        totalFiles = 0 // Will be updated below
                                    };
                                    if (notificationService != null)
                                    {
                                        await notificationService.SendNotificationAsync("book-available", availableData, settings.WebhookUrl, settings.EnabledNotificationTriggers);
                                    }
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                {
                                    _logger.LogWarning(ex, "Failed to send book-available notification for audiobook {AudiobookId} in background scan", audiobook.Id);
                                }
                            }

                            var updated = await audiobookRepository.GetByIdAsync(audiobook.Id);
                            if (updated != null)
                            {
                                // Build an authoritative Audiobook DTO and broadcast it
                                var audiobookDto = AudiobookDtoFactory.BuildFromEntity(updated);
                                await _hubContext.Clients.All.SendAsync("AudiobookUpdate", audiobookDto);
                                await _hubContext.Clients.All.SendAsync("ScanJobUpdate", new { jobId = job.Id.ToString(), audiobookId = job.AudiobookId, status = "Completed", found = foundFiles.Count, created = createdFiles, completedAt = DateTime.UtcNow });
                                _logger.LogInformation("Broadcasted AudiobookUpdate for AudiobookId {AudiobookId} after scan job {JobId}", audiobook.Id, job.Id);

                                // Mark job as completed in queue to prevent deduplication issues
                                try { _queue.UpdateJobStatus(job.Id, "Completed"); }
                                catch (Exception caughtEx_8) when (caughtEx_8 is not OperationCanceledException && caughtEx_8 is not OutOfMemoryException && caughtEx_8 is not StackOverflowException)
                                {
                                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogError(ex, "Error processing scan job {JobId}", job.Id);
                            try { _queue.UpdateJobStatus(job.Id, "Failed", ex.Message); }
                            catch (Exception caughtEx_9) when (caughtEx_9 is not OperationCanceledException && caughtEx_9 is not OutOfMemoryException && caughtEx_9 is not StackOverflowException)
                            {
                                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                            }
                            try { await _hubContext.Clients.All.SendAsync("ScanJobUpdate", new { jobId = job.Id.ToString(), audiobookId = job.AudiobookId, status = "Failed", error = ex.Message, failedAt = DateTime.UtcNow }); }
                            catch (Exception caughtEx_10) when (caughtEx_10 is not OperationCanceledException && caughtEx_10 is not OutOfMemoryException && caughtEx_10 is not StackOverflowException)
                            {
                                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("ScanBackgroundService cancellation requested");
                }
                catch (OperationCanceledException ex)
                {
                    _logger.LogWarning(ex, "ScanBackgroundService job stream canceled/timed out unexpectedly; continuing");
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Unhandled error in ScanBackgroundService loop");
                }
            }
        }

        private string CalculateBasePath(List<string> filePaths)
        {
            if (!filePaths.Any())
                return string.Empty;

            // Convert all paths to directory paths (get parent directory for each file)
            var directories = filePaths
                .Select(p => FileUtils.NormalizeStoredPath(Path.GetDirectoryName(p) ?? p))
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (directories.Count == 1)
            {
                // All files are in the same directory
                return directories[0];
            }

            // Find the common ancestor directory
            var commonPath = GetCommonPath(directories);

            // Walk up the directory tree until we find a directory that has more than 1 subdirectory or file
            var currentPath = commonPath;
            while (!string.IsNullOrEmpty(currentPath))
            {
                try
                {
                    var parent = Directory.GetParent(currentPath)?.FullName;
                    if (string.IsNullOrEmpty(parent))
                        break;

                    // Count subdirectories and files in parent
                    var subDirs = Directory.GetDirectories(parent).Length;
                    var files = Directory.GetFiles(parent).Length;

                    // If parent has more than 1 thing (subdirs + files), we've found our base path
                    if (subDirs + files > 1)
                    {
                        return currentPath;
                    }

                    currentPath = parent;
                }
                catch (Exception caughtEx_11) when (caughtEx_11 is not OperationCanceledException && caughtEx_11 is not OutOfMemoryException && caughtEx_11 is not StackOverflowException)
                {
                    // If we can't access the directory, stop here
                    break;
                }
            }

            return commonPath;
        }

        private string GetCommonPath(List<string> paths)
        {
            if (!paths.Any())
                return string.Empty;

            var firstPath = FileUtils.NormalizeStoredPath(paths[0]);
            var commonPath = firstPath;

            foreach (var path in paths.Skip(1).Select(rawPath => FileUtils.NormalizeStoredPath(rawPath)))
            {
                var minLength = Math.Min(commonPath.Length, path.Length);
                var commonLength = 0;

                for (int i = 0; i < minLength; i++)
                {
                    if (commonPath[i] == path[i])
                        commonLength++;
                    else
                        break;
                }

                // Ensure we don't break in the middle of a directory name
                if (commonLength < commonPath.Length)
                {
                    var lastSep = commonPath.LastIndexOf(Path.DirectorySeparatorChar, commonLength - 1);
                    commonLength = lastSep >= 0 ? lastSep + 1 : 0;
                }

                commonPath = commonPath.Substring(0, commonLength);

                if (string.IsNullOrEmpty(commonPath))
                    break;
            }

            // Ensure it's a valid directory path
            if (!string.IsNullOrEmpty(commonPath) && !Directory.Exists(commonPath))
            {
                var parent = Directory.GetParent(commonPath)?.FullName;
                return parent ?? commonPath;
            }

            return commonPath;
        }
    }
}



