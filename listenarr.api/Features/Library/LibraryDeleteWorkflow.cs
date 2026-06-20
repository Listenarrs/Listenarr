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
using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    public sealed class LibraryDeleteWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IImageCacheService _imageCacheService;
        private readonly IAudiobookFilesystemDeleteService _audiobookFilesystemDeleteService;
        private readonly string _contentRootPath;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger<LibraryDeleteWorkflow> _logger;

        public LibraryDeleteWorkflow(
            IAudiobookRepository repo,
            IImageCacheService imageCacheService,
            IAudiobookFilesystemDeleteService audiobookFilesystemDeleteService,
            IApplicationPathService applicationPathService,
            IFileSystem fileSystem,
            ILogger<LibraryDeleteWorkflow> logger)
        {
            _repo = repo;
            _imageCacheService = imageCacheService;
            _audiobookFilesystemDeleteService = audiobookFilesystemDeleteService;
            _contentRootPath = applicationPathService.ContentRootPath;
            _fileSystem = fileSystem;
            _logger = logger;
        }

        public async Task<IActionResult> DeleteAsync(int id, bool deleteFiles, bool deleteFolder)
        {
            var audiobook = await _repo.GetByIdAsync(id);
            if (audiobook == null)
            {
                return new NotFoundObjectResult(new { message = "Audiobook not found" });
            }

            deleteFiles = deleteFiles || deleteFolder;

            AudiobookFilesystemDeleteResult? filesystemResult = null;
            if (deleteFiles)
            {
                filesystemResult = await _audiobookFilesystemDeleteService.DeleteAsync(audiobook, deleteFolder);
            }

            await DeleteCachedImageAsync(audiobook);

            var deleted = await _repo.DeleteByIdAsync(id);
            if (deleted)
            {
                var message = filesystemResult?.BuildDeleteMessage() ?? "Audiobook deleted successfully.";
                return new OkObjectResult(new
                {
                    message,
                    id,
                    deletedFiles = filesystemResult?.DeletedFiles ?? 0,
                    deletedFolder = filesystemResult?.DeletedFolder,
                    deletedParentFolder = filesystemResult?.DeletedParentFolder,
                    warnings = filesystemResult?.Warnings ?? new List<string>()
                });
            }

            return new ObjectResult(new { message = "Failed to delete audiobook" })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }

        private async Task DeleteCachedImageAsync(Audiobook audiobook)
        {
            try
            {
                if (!string.IsNullOrEmpty(audiobook.Asin))
                {
                    await DeleteCachedImageByIdentifierAsync(audiobook.Asin, "ASIN");
                }
                else if (!string.IsNullOrEmpty(audiobook.ImageUrl))
                {
                    await DeleteCachedImageFromUrlAsync(audiobook);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to delete cached image for audiobook id {Id}", audiobook.Id);
            }
        }

        private async Task DeleteCachedImageByIdentifierAsync(string identifier, string source)
        {
            var imagePath = await _imageCacheService.GetCachedImagePathAsync(identifier);
            if (imagePath == null)
            {
                return;
            }

            var fullPath = FileUtils.CombineWithOptionalBase(_contentRootPath, imagePath);
            if (_fileSystem.FileExists(fullPath))
            {
                if (!_fileSystem.TryValidateMutationTarget(fullPath, [_contentRootPath], out var safePath, out var reason))
                {
                    _logger.LogWarning(
                        "Blocked cached image delete for {Source} {Identifier}: {Reason}",
                        source,
                        LogRedaction.SanitizeText(identifier),
                        LogRedaction.SanitizeText(reason));
                    return;
                }

                _fileSystem.DeleteFile(safePath);
                _logger.LogInformation("Deleted cached image for {Source} {Identifier}", source, LogRedaction.SanitizeText(identifier));
            }
        }

        private async Task DeleteCachedImageFromUrlAsync(Audiobook audiobook)
        {
            try
            {
                const string marker = "/config/cache/images/library/";
                var url = audiobook.ImageUrl!;
                var idx = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    return;
                }

                var filename = url.Substring(idx + marker.Length);
                filename = Path.GetFileName(filename);
                var identifier = Path.GetFileNameWithoutExtension(filename);

                if (!string.IsNullOrEmpty(identifier) && Regex.IsMatch(identifier, "^[A-Za-z0-9_\\-\\.]{1,128}$"))
                {
                    await DeleteCachedImageByIdentifierAsync(identifier, "identifier (from ImageUrl)");
                }
                else
                {
                    _logger.LogWarning("Image identifier from ImageUrl for audiobook id {Id} is invalid: {Identifier}", audiobook.Id, LogRedaction.SanitizeText(identifier));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to delete cached image based on stored ImageUrl for audiobook id {Id}", audiobook.Id);
            }
        }
    }
}
