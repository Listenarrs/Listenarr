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

using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// Delete a single tracked file from an audiobook, optionally removing it
    /// from disk too. Disk-delete failures surface as warnings; the DB row is
    /// still removed.
    /// </summary>
    public sealed class LibraryFileDeleteWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IAudiobookFileService _audiobookFileService;
        private readonly ILogger<LibraryFileDeleteWorkflow> _logger;

        public LibraryFileDeleteWorkflow(
            IAudiobookRepository repo,
            IAudiobookFileService audiobookFileService,
            ILogger<LibraryFileDeleteWorkflow> logger)
        {
            _repo = repo;
            _audiobookFileService = audiobookFileService;
            _logger = logger;
        }

        public async Task<IActionResult> DeleteFileAsync(int id, int fileId, bool deleteFromDisk, CancellationToken ct)
        {
            var audiobook = await _repo.GetByIdAsync(id);
            if (audiobook == null)
            {
                return new NotFoundObjectResult(new { message = "Audiobook not found" });
            }

            DeleteAudiobookFileResult result;
            try
            {
                result = await _audiobookFileService.DeleteAudiobookFileAsync(audiobook, fileId, deleteFromDisk, source: "manual", ct: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to delete AudiobookFile {FileId} for audiobook {AudiobookId}", fileId, id);
                return new ObjectResult(new { message = "Failed to delete file" })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }

            return result.Outcome switch
            {
                DeleteAudiobookFileOutcome.NotFound =>
                    new NotFoundObjectResult(new { message = "File not found" }),

                DeleteAudiobookFileOutcome.DoesNotBelongToAudiobook =>
                    new BadRequestObjectResult(new { message = "File does not belong to this audiobook" }),

                DeleteAudiobookFileOutcome.Deleted =>
                    new OkObjectResult(new
                    {
                        message = "File removed",
                        fileId,
                        deletedFromDisk = result.DeletedFromDisk,
                        path = result.Path,
                        warnings = result.Warnings
                    }),

                _ => new ObjectResult(new { message = "Unknown delete outcome" })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                }
            };
        }
    }
}
