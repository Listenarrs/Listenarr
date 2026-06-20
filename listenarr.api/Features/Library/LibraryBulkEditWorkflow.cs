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
using System.Text.RegularExpressions;
using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    public sealed class LibraryBulkEditWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IImageCacheService _imageCacheService;
        private readonly IHistoryRepository _historyRepository;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IFileNamingService _fileNamingService;
        private readonly string _contentRootPath;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger<LibraryBulkEditWorkflow> _logger;

        public LibraryBulkEditWorkflow(
            IAudiobookRepository repo,
            IImageCacheService imageCacheService,
            IHistoryRepository historyRepository,
            IServiceScopeFactory scopeFactory,
            IFileNamingService fileNamingService,
            IApplicationPathService applicationPathService,
            IFileSystem fileSystem,
            ILogger<LibraryBulkEditWorkflow> logger)
        {
            _repo = repo;
            _imageCacheService = imageCacheService;
            _historyRepository = historyRepository;
            _scopeFactory = scopeFactory;
            _fileNamingService = fileNamingService;
            _contentRootPath = applicationPathService.ContentRootPath;
            _fileSystem = fileSystem;
            _logger = logger;
        }

        public async Task<IActionResult> BulkDeleteAsync(LibraryController.BulkDeleteRequest request)
        {
            if (request.Ids == null || !request.Ids.Any())
            {
                return new BadRequestObjectResult(new { message = "No audiobook IDs provided for bulk deletion" });
            }

            var deletedCount = 0;
            var deletedImagesCount = 0;
            var errors = new List<string>();
            var deletedIds = new List<int>();

            foreach (var id in request.Ids.Distinct())
            {
                try
                {
                    var audiobook = await _repo.GetByIdAsync(id);
                    if (audiobook == null)
                    {
                        errors.Add($"Audiobook with ID {id} not found");
                        continue;
                    }

                    deletedImagesCount += await DeleteCachedImageAsync(audiobook);

                    await _historyRepository.AddAsync(new History
                    {
                        AudiobookId = audiobook.Id,
                        AudiobookTitle = audiobook.Title ?? "Unknown Title",
                        EventType = "Deleted",
                        Message = $"Audiobook '{audiobook.Title}' deleted via bulk operation",
                        Source = "BulkDelete",
                        Timestamp = DateTime.UtcNow
                    });

                    var deleted = await _repo.DeleteByIdAsync(id);
                    if (deleted)
                    {
                        deletedCount++;
                        deletedIds.Add(id);
                        _logger.LogInformation("Deleted audiobook '{Title}' (ID: {Id}) via bulk operation", LogRedaction.SanitizeText(audiobook.Title), id);
                    }
                    else
                    {
                        errors.Add($"Failed to delete audiobook with ID {id}");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Error during bulk delete for ID {Id}: {Message}", id, ex.Message);
                    errors.Add($"Error deleting audiobook with ID {id}: {ex.Message}");
                }
            }

            if (deletedCount == 0 && errors.Any())
            {
                return new BadRequestObjectResult(new { message = "No audiobooks were successfully deleted", errors });
            }

            object result = errors.Any()
                ? new
                {
                    message = $"Partially successful: deleted {deletedCount} audiobook{(deletedCount != 1 ? "s" : "")}, {errors.Count} error{(errors.Count != 1 ? "s" : "")} occurred",
                    deletedCount,
                    deletedImagesCount,
                    ids = deletedIds,
                    errors
                }
                : new
                {
                    message = $"Successfully deleted {deletedCount} audiobook{(deletedCount != 1 ? "s" : "")}",
                    deletedCount,
                    deletedImagesCount,
                    ids = deletedIds
                };

            return new OkObjectResult(result);
        }

        public async Task<IActionResult> BulkUpdateAsync(LibraryController.BulkUpdateRequest request)
        {
            if (request?.Ids == null || !request.Ids.Any())
            {
                return new BadRequestObjectResult(new { message = "No audiobook IDs provided for bulk update" });
            }

            var results = new List<object>();
            var settings = await TryLoadApplicationSettingsAsync();

            foreach (var id in request.Ids.Distinct())
            {
                var entryErrors = new List<string>();
                var success = false;

                try
                {
                    var audiobook = await _repo.GetByIdAsync(id);
                    if (audiobook == null)
                    {
                        entryErrors.Add($"Audiobook with ID {id} not found");
                        results.Add(new { id, success, errors = entryErrors });
                        continue;
                    }

                    var changed = false;

                    if (request.Updates != null && request.Updates.TryGetValue("monitored", out var monitoredObj))
                    {
                        try
                        {
                            var monVal = monitoredObj is JsonElement je
                                ? je.ValueKind == JsonValueKind.True
                                : Convert.ToBoolean(monitoredObj);

                            audiobook.Monitored = monVal;
                            changed = true;
                            _logger.LogInformation("Set Monitored={Monitored} for audiobook id={Id}", monVal, id);

                            await AddBulkUpdateHistoryAsync(audiobook, $"Monitored set to {monVal}");
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            entryErrors.Add($"Invalid monitored value: {ex.Message}");
                        }
                    }

                    if (request.Updates != null && request.Updates.TryGetValue("qualityProfileId", out var qpObj))
                    {
                        try
                        {
                            var qpVal = qpObj is JsonElement jq
                                ? jq.GetInt32()
                                : Convert.ToInt32(qpObj);

                            audiobook.QualityProfileId = qpVal;
                            changed = true;
                            _logger.LogInformation("Set QualityProfileId={Profile} for audiobook id={Id}", qpVal, id);

                            await AddBulkUpdateHistoryAsync(audiobook, $"Quality profile set to {qpVal}");
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            entryErrors.Add($"Invalid qualityProfileId value: {ex.Message}");
                        }
                    }

                    if (request.Updates != null && request.Updates.TryGetValue("rootFolder", out var rootObj))
                    {
                        try
                        {
                            var rootPath = ExtractRootPath(rootObj);
                            if (!string.IsNullOrWhiteSpace(rootPath))
                            {
                                var fileNamingPattern = !string.IsNullOrWhiteSpace(settings?.FolderNamingPattern)
                                    ? settings!.FolderNamingPattern
                                    : settings?.FileNamingPattern ?? string.Empty;
                                var newBase = LibraryPathPlanner.ComputeAudiobookBaseDirectoryFromPattern(audiobook, rootPath, fileNamingPattern, _fileNamingService);
                                if (!_fileSystem.TryValidateMutationTarget(newBase, [rootPath], out newBase, out var reason))
                                {
                                    entryErrors.Add($"Computed audiobook path is outside the selected root folder: {reason}");
                                    continue;
                                }

                                try
                                {
                                    if (!_fileSystem.DirectoryExists(newBase))
                                    {
                                        _fileSystem.CreateDirectory(newBase);
                                        _logger.LogInformation("Created directory for audiobook id={Id} at {Path}", id, newBase);
                                    }

                                    audiobook.BasePath = newBase;
                                    changed = true;

                                    await AddBulkUpdateHistoryAsync(audiobook, $"BasePath set to {newBase} via bulk update");
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                {
                                    entryErrors.Add($"Failed to apply root folder for audiobook {id}: {ex.Message}");
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            entryErrors.Add($"Invalid rootFolder value: {ex.Message}");
                        }
                    }

                    if (changed)
                    {
                        await _repo.UpdateAsync(audiobook);
                        success = true;
                    }
                    else
                    {
                        entryErrors.Add("No valid updates provided for this audiobook");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    entryErrors.Add($"Unhandled error: {ex.Message}");
                }

                results.Add(new { id, success, errors = entryErrors });
            }

            return new OkObjectResult(new { message = "Bulk update completed", results });
        }

        private async Task<int> DeleteCachedImageAsync(Audiobook audiobook)
        {
            try
            {
                if (!string.IsNullOrEmpty(audiobook.Asin))
                {
                    var imagePath = await _imageCacheService.GetCachedImagePathAsync(audiobook.Asin);
                    if (imagePath != null)
                    {
                        var fullPath = ResolvePathWithOptionalBase(_contentRootPath, imagePath);
                        if (_fileSystem.FileExists(fullPath))
                        {
                            if (!_fileSystem.TryValidateMutationTarget(fullPath, [_contentRootPath], out var safePath, out var reason))
                            {
                                _logger.LogWarning(
                                    "Blocked cached image delete for ASIN {Asin}: {Reason}",
                                    LogRedaction.SanitizeText(audiobook.Asin),
                                    LogRedaction.SanitizeText(reason));
                                return 0;
                            }

                            _fileSystem.DeleteFile(safePath);
                            _logger.LogInformation("Deleted cached image for ASIN {Asin}", LogRedaction.SanitizeText(audiobook.Asin));
                            return 1;
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(audiobook.ImageUrl))
                {
                    return await DeleteCachedImageFromUrlAsync(audiobook);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to delete cached image for audiobook id {Id}", audiobook.Id);
            }

            return 0;
        }

        private async Task<int> DeleteCachedImageFromUrlAsync(Audiobook audiobook)
        {
            try
            {
                const string marker = "/config/cache/images/library/";
                var url = audiobook.ImageUrl!;
                var idx = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    return 0;
                }

                var filename = url.Substring(idx + marker.Length);
                filename = Path.GetFileName(filename);
                var identifier = Path.GetFileNameWithoutExtension(filename);

                if (string.IsNullOrEmpty(identifier) || !Regex.IsMatch(identifier, "^[A-Za-z0-9_\\-\\.]{1,128}$"))
                {
                    _logger.LogWarning("Image identifier from ImageUrl for audiobook id {Id} is invalid: {Identifier}", audiobook.Id, LogRedaction.SanitizeText(identifier));
                    return 0;
                }

                var imagePath = await _imageCacheService.GetCachedImagePathAsync(identifier);
                if (!string.IsNullOrEmpty(imagePath))
                {
                    var fullPath = ResolvePathWithOptionalBase(_contentRootPath, imagePath);
                    if (_fileSystem.FileExists(fullPath))
                    {
                        if (!_fileSystem.TryValidateMutationTarget(fullPath, [_contentRootPath], out var safePath, out var reason))
                        {
                            _logger.LogWarning(
                                "Blocked cached image delete for identifier {Identifier}: {Reason}",
                                LogRedaction.SanitizeText(identifier),
                                LogRedaction.SanitizeText(reason));
                            return 0;
                        }

                        _fileSystem.DeleteFile(safePath);
                        _logger.LogInformation("Deleted cached image for identifier (from ImageUrl): {Identifier}", LogRedaction.SanitizeText(identifier));
                        return 1;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to delete cached image based on stored ImageUrl for audiobook id {Id}", audiobook.Id);
            }

            return 0;
        }

        private async Task<ApplicationSettings?> TryLoadApplicationSettingsAsync()
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
                return await configService.GetApplicationSettingsAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to load application settings while performing bulk update");
                return null;
            }
        }

        private static string? ExtractRootPath(object rootObj)
        {
            if (rootObj is JsonElement jr)
            {
                return jr.ValueKind == JsonValueKind.String ? jr.GetString() : null;
            }

            return rootObj.ToString();
        }

        private async Task AddBulkUpdateHistoryAsync(Audiobook audiobook, string message)
        {
            await _historyRepository.AddAsync(new History
            {
                AudiobookId = audiobook.Id,
                AudiobookTitle = audiobook.Title ?? "Unknown",
                EventType = "Updated",
                Message = message,
                Source = "BulkUpdate",
                Timestamp = DateTime.UtcNow
            });
        }

        private static string ResolvePathWithOptionalBase(string? basePath, string candidatePath)
        {
            return FileUtils.CombineWithOptionalBase(basePath, candidatePath);
        }
    }
}
