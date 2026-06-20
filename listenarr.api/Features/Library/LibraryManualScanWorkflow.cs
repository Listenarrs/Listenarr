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
using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    public sealed class LibraryManualScanWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly INotificationService? _notificationService;
        private readonly LibraryScanPathResolver _scanPathResolver;
        private readonly LibraryScanQueueWorkflow _scanQueueWorkflow;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger<LibraryManualScanWorkflow> _logger;

        public LibraryManualScanWorkflow(
            IAudiobookRepository repo,
            IServiceScopeFactory scopeFactory,
            LibraryScanPathResolver scanPathResolver,
            LibraryScanQueueWorkflow scanQueueWorkflow,
            IFileSystem fileSystem,
            ILogger<LibraryManualScanWorkflow> logger,
            INotificationService? notificationService = null)
        {
            _repo = repo;
            _scopeFactory = scopeFactory;
            _scanPathResolver = scanPathResolver;
            _scanQueueWorkflow = scanQueueWorkflow;
            _fileSystem = fileSystem;
            _logger = logger;
            _notificationService = notificationService;
        }

        public async Task<IActionResult> ScanAsync(int id, LibraryController.ScanRequest? request)
        {
            var audiobook = await _repo.GetByIdAsync(id);
            if (audiobook == null) return new NotFoundObjectResult(new { message = "Audiobook not found" });

            var queuedResult = await _scanQueueWorkflow.TryEnqueueAsync(audiobook, request?.Path);
            if (queuedResult != null)
            {
                return queuedResult;
            }

            var scanPathResolution = await _scanPathResolver.ResolveAsync(audiobook, request?.Path);
            if (scanPathResolution.ErrorResult != null)
            {
                return scanPathResolution.ErrorResult;
            }

            var scanRoot = scanPathResolution.ScanRoot;

            if (string.IsNullOrEmpty(scanRoot) || !_fileSystem.DirectoryExists(scanRoot))
            {
                return new BadRequestObjectResult(new { message = "Scan path not provided or does not exist", path = scanRoot });
            }

            _logger.LogInformation("Scanning for audiobook files for '{Title}' under: {Path}", LogRedaction.SanitizeText(audiobook.Title), LogRedaction.SanitizeFilePath(scanRoot));

            var foundFilesResult = FindMatchingAudioFiles(audiobook, scanRoot);
            if (foundFilesResult.ErrorResult != null)
            {
                return foundFilesResult.ErrorResult;
            }

            var foundFiles = foundFilesResult.FoundFiles;
            if (!foundFiles.Any())
            {
                return new OkObjectResult(new { message = "No files found during scan", scannedPath = scanRoot, found = 0 });
            }

            var basePath = LibraryPathPlanner.CalculateBasePath(foundFiles, _fileSystem, _logger);
            _logger.LogInformation("Calculated base path for audiobook '{Title}': {BasePath}", LogRedaction.SanitizeText(audiobook.Title), LogRedaction.SanitizeFilePath(basePath));

            var created = new List<AudiobookFile>();

            using var scope = _scopeFactory.CreateScope();
            var metadataService = scope.ServiceProvider.GetRequiredService<IMetadataService>();
            var audioFileRepository = scope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
            var historyRepository = scope.ServiceProvider.GetRequiredService<IHistoryRepository>();

            var existingFilesList = await audioFileRepository.GetByAudiobookIdAsync(audiobook.Id);

            foreach (var filePath in foundFiles)
            {
                try
                {
                    var relativePath = Path.GetRelativePath(basePath, filePath);

                    var existing = existingFilesList.FirstOrDefault(f => f.Path == relativePath);
                    if (existing != null)
                    {
                        _logger.LogInformation("Skipping existing AudiobookFile for audiobook {AudiobookId}: {Path}", audiobook.Id, relativePath);
                        continue;
                    }

                    AudioMetadata? meta = null;
                    try
                    {
                        meta = await metadataService.ExtractFileMetadataAsync(filePath);
                    }
                    catch (Exception mex) when (mex is not OperationCanceledException && mex is not OutOfMemoryException && mex is not StackOverflowException)
                    {
                        _logger.LogWarning(mex, "Failed to extract metadata for file {File}", filePath);
                    }

                    var fileRecord = new AudiobookFile
                    {
                        AudiobookId = audiobook.Id,
                        Path = relativePath,
                        Size = _fileSystem.GetFileLength(filePath),
                        Source = "scan",
                        CreatedAt = DateTime.UtcNow,
                        DurationSeconds = meta?.Duration.TotalSeconds,
                        Format = meta?.Format,
                        Bitrate = meta?.BitRate,
                        SampleRate = meta?.SampleRate,
                        Channels = meta?.Channels
                    };

                    await audioFileRepository.AddAsync(fileRecord);
                    created.Add(fileRecord);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to create AudiobookFile for {File}", filePath);
                }
            }

            if (!string.IsNullOrEmpty(basePath))
            {
                audiobook.BasePath = basePath;
                await _repo.UpdateAsync(audiobook);
            }

            foreach (var historyEntry in created.Select(fileRecord => new History
            {
                AudiobookId = audiobook.Id,
                AudiobookTitle = audiobook.Title ?? "Unknown",
                EventType = "File Added",
                Message = $"File scanned and added: {Path.GetFileName(fileRecord.Path)}",
                Source = "Scan",
                Data = JsonSerializer.Serialize(new
                {
                    FilePath = fileRecord.Path,
                    FileSize = fileRecord.Size,
                    Format = fileRecord.Format,
                    Source = fileRecord.Source
                }),
                Timestamp = DateTime.UtcNow
            }))
            {
                await historyRepository.AddAsync(historyEntry);
            }

            await ReconcileMissingFilesAsync(audiobook, foundFiles, basePath, audioFileRepository, historyRepository);
            await MigrateLegacyFilePathAsync(audiobook, created, audioFileRepository, historyRepository);

            var updated = await _repo.GetByIdAsync(audiobook.Id);

            await SendAvailableNotificationAsync(audiobook, created.Count, updated);

            return new OkObjectResult(new { message = "Scan complete", scannedPath = scanRoot, found = foundFiles.Count, created = created.Count, audiobook = updated });
        }

        private (List<string> FoundFiles, IActionResult? ErrorResult) FindMatchingAudioFiles(Audiobook audiobook, string scanRoot)
        {
            var titleToken = (audiobook.Title ?? string.Empty).Replace("\"", string.Empty).Trim();
            var authorToken = audiobook.Authors?.FirstOrDefault() ?? string.Empty;
            var foundFiles = new List<string>();

            try
            {
                var exts = FileUtils.AudioExtensions;
                var dirs = new Stack<string>();
                dirs.Push(scanRoot);

                while (dirs.Count > 0)
                {
                    var dir = dirs.Pop();
                    try
                    {
                        var normalizedDir = Path.GetFullPath(dir);

                        foreach (var file in _fileSystem.EnumerateFiles(normalizedDir))
                        {
                            try
                            {
                                var ext = Path.GetExtension(file);
                                if (!exts.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
                                var fname = Path.GetFileNameWithoutExtension(file);
                                if (!string.IsNullOrEmpty(titleToken) && fname.IndexOf(titleToken, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    foundFiles.Add(file);
                                    continue;
                                }
                                if (!string.IsNullOrEmpty(authorToken) && file.IndexOf(authorToken, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    foundFiles.Add(file);
                                }
                            }
                            catch (Exception innerFileEx) when (innerFileEx is not OperationCanceledException && innerFileEx is not OutOfMemoryException && innerFileEx is not StackOverflowException)
                            {
                                _logger.LogDebug(innerFileEx, "Skipped file while scanning {Dir}", normalizedDir);
                            }
                        }

                        foreach (var sub in _fileSystem.EnumerateDirectories(normalizedDir))
                        {
                            dirs.Push(sub);
                        }
                    }
                    catch (IOException ioEx)
                    {
                        _logger.LogWarning(ioEx, "IO error while enumerating directory during scan: {Dir}", dir);
                    }
                    catch (UnauthorizedAccessException uaEx)
                    {
                        _logger.LogWarning(uaEx, "Access denied while enumerating directory during scan: {Dir}", dir);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Unexpected error while enumerating directory during scan: {Dir}", dir);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error while scanning filesystem for audiobook files");
                return (foundFiles, new ObjectResult(new { message = "Error scanning filesystem", error = ex.Message })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                });
            }

            return (foundFiles, null);
        }

        private async Task ReconcileMissingFilesAsync(
            Audiobook audiobook,
            List<string> foundFiles,
            string basePath,
            IAudiobookFileRepository audioFileRepository,
            IHistoryRepository historyRepository)
        {
            try
            {
                var allExistingFiles = await audioFileRepository.GetByAudiobookIdAsync(audiobook.Id);

                var foundSet = new HashSet<string>(foundFiles.Select(f => Path.GetRelativePath(basePath, f)), StringComparer.OrdinalIgnoreCase);
                var toRemove = allExistingFiles
                    .Where(f => f.Path != null && FileUtils.IsAudioFile(f.Path) && !foundSet.Contains(f.Path))
                    .ToList();

                foreach (var rem in toRemove)
                {
                    try
                    {
                        await audioFileRepository.DeleteAsync(rem.Id);
                        _logger.LogInformation("Removing missing AudiobookFile DB row Id={Id} Path={Path}", rem.Id, rem.Path);

                        await historyRepository.AddAsync(new History
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
                        });
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Failed to remove AudiobookFile Id={Id} Path={Path}", rem.Id, rem.Path);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to reconcile audiobook files after scan for audiobook {AudiobookId}", audiobook.Id);
            }
        }

        private async Task MigrateLegacyFilePathAsync(
            Audiobook audiobook,
            List<AudiobookFile> created,
            IAudiobookFileRepository audioFileRepository,
            IHistoryRepository historyRepository)
        {
            try
            {
                var needsUpdate = false;
                if (!string.IsNullOrEmpty(audiobook.FilePath))
                {
                    if (_fileSystem.FileExists(audiobook.FilePath))
                    {
                        var existingFileRecord = await audioFileRepository.ExistsAtPathAsync(audiobook.Id, audiobook.FilePath!);

                        if (!existingFileRecord)
                        {
                            try
                            {
                                using var afScope = _scopeFactory.CreateScope();
                                var audioFileService = afScope.ServiceProvider.GetRequiredService<IAudiobookFileService>();
                                var migrated = await audioFileService.EnsureAudiobookFileAsync(audiobook, audiobook.FilePath, "scan-legacy");
                                if (migrated)
                                {
                                    _logger.LogInformation("Migrated legacy filePath to AudiobookFile record for audiobook {AudiobookId}: {Path}", audiobook.Id, audiobook.FilePath);
                                    created.Add(new AudiobookFile { Path = audiobook.FilePath });
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                _logger.LogWarning(ex, "Failed to migrate legacy filePath for audiobook {AudiobookId}: {Path}", audiobook.Id, audiobook.FilePath);
                            }
                        }
                    }
                    else
                    {
                        var missingFilePath = audiobook.FilePath;
                        audiobook.FilePath = null;
                        audiobook.FileSize = null;
                        needsUpdate = true;
                        _logger.LogInformation("Cleared missing legacy filePath for audiobook {AudiobookId}: {Path}", audiobook.Id, missingFilePath);

                        await historyRepository.AddAsync(new History
                        {
                            AudiobookId = audiobook.Id,
                            AudiobookTitle = audiobook.Title ?? "Unknown",
                            EventType = "File Removed",
                            Message = "Legacy file path cleared (file no longer exists)",
                            Source = "Scan",
                            Data = JsonSerializer.Serialize(new
                            {
                                FilePath = audiobook.FilePath,
                                Source = "legacy-migration"
                            }),
                            Timestamp = DateTime.UtcNow
                        });
                    }
                }

                if (needsUpdate)
                {
                    await _repo.UpdateAsync(audiobook);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to handle legacy filePath migration for audiobook {AudiobookId}", audiobook.Id);
            }
        }

        private async Task SendAvailableNotificationAsync(Audiobook audiobook, int createdCount, Audiobook? updated)
        {
            if (_notificationService == null || !audiobook.Monitored || createdCount <= 0)
            {
                return;
            }

            try
            {
                using var notificationScope = _scopeFactory.CreateScope();
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
                    filesImported = createdCount,
                    totalFiles = updated?.Files?.Count ?? 0
                };
                await _notificationService.SendNotificationAsync("book-available", availableData, settings.WebhookUrl, settings.EnabledNotificationTriggers);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to send book-available notification for audiobook {AudiobookId}", audiobook.Id);
            }
        }
    }
}
