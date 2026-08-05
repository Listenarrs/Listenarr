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

using System.Text.RegularExpressions;
using Listenarr.Application.Common.Exceptions;
using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    public sealed partial class LibraryBulkEditWorkflow
    {
        public async Task<IActionResult> BulkDeleteAsync(
            LibraryController.BulkDeleteRequest request,
            CancellationToken cancellationToken = default)
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
                BulkDeleteOutcome outcome;
                try
                {
                    outcome = await _filesystemMutationCoordinator.ExecuteExclusiveAsync(
                        globalToken => _audiobookOperationCoordinator.ExecuteExclusiveAsync(
                            id,
                            audiobookToken => DeleteOneAsync(id, audiobookToken),
                            globalToken),
                        cancellationToken);
                }
                catch (OperationCanceledException) when (deletedCount > 0)
                {
                    errors.Add(
                        "Bulk deletion stopped after request cancellation; no additional audiobooks were deleted.");
                    break;
                }

                deletedImagesCount += outcome.DeletedImages;
                if (outcome.Deleted)
                {
                    deletedCount++;
                    deletedIds.Add(id);
                }
                if (outcome.Error != null)
                {
                    errors.Add(outcome.Error);
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

        private async Task<BulkDeleteOutcome> DeleteOneAsync(
            int id,
            CancellationToken cancellationToken)
        {
            try
            {
                await _moveQueueService.EnsureFilesystemMutationAllowedAsync(
                    id,
                    cancellationToken);

                using var scope = _scopeFactory.CreateScope();
                var deletionCommitService = scope.ServiceProvider
                    .GetRequiredService<IAudiobookDeletionCommitService>();
                var commit = await deletionCommitService.DeleteAsync(
                    id,
                    cancellationToken);
                if (commit.Outcome == AudiobookDeletionCommitOutcome.NotFound)
                {
                    return new BulkDeleteOutcome(false, 0, $"Audiobook with ID {id} not found");
                }

                if (commit.Outcome != AudiobookDeletionCommitOutcome.Deleted
                    || commit.Audiobook == null)
                {
                    return new BulkDeleteOutcome(
                        false,
                        0,
                        $"Failed to delete audiobook with ID {id}");
                }

                var audiobook = commit.Audiobook;
                var deletedImages = await DeleteCachedImageAsync(audiobook);
                try
                {
                    await _historyRepository.AddAsync(new History
                    {
                        AudiobookId = audiobook.Id,
                        AudiobookTitle = audiobook.Title ?? "Unknown Title",
                        EventType = "Deleted",
                        Message = $"Audiobook '{audiobook.Title}' deleted via bulk operation",
                        Source = "BulkDelete",
                        Timestamp = DateTime.UtcNow
                    });
                }
                catch (Exception historyException) when (historyException is not (
                    OutOfMemoryException or StackOverflowException))
                {
                    _logger.LogWarning(
                        historyException,
                        "Audiobook {AudiobookId} was deleted, but its bulk-delete history event could not be recorded",
                        id);
                }

                _logger.LogInformation(
                    "Deleted audiobook '{Title}' (ID: {Id}) via bulk operation",
                    LogRedaction.SanitizeText(audiobook.Title),
                    id);
                return new BulkDeleteOutcome(true, deletedImages, null);
            }
            catch (ApplicationConflictException exception)
            {
                return new BulkDeleteOutcome(false, 0, exception.SafeDetail);
            }
            catch (Exception ex) when (ex is not (
                OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                _logger.LogError(ex, "Error during bulk delete for ID {Id}", id);
                return new BulkDeleteOutcome(
                    false,
                    0,
                    $"Error deleting audiobook with ID {id}");
            }
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
            catch (Exception ex) when (ex is not (
                OutOfMemoryException or StackOverflowException))
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
            catch (Exception ex) when (ex is not (
                OutOfMemoryException or StackOverflowException))
            {
                _logger.LogWarning(ex, "Failed to delete cached image based on stored ImageUrl for audiobook id {Id}", audiobook.Id);
            }

            return 0;
        }

        private static string ResolvePathWithOptionalBase(string? basePath, string candidatePath)
        {
            return FileUtils.CombineWithOptionalBase(basePath, candidatePath);
        }

        private sealed record BulkDeleteOutcome(bool Deleted, int DeletedImages, string? Error);
    }
}
