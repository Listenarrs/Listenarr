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

namespace Listenarr.Api.Features.Images
{
    [ApiController]
    [Route("api/v{version:apiVersion}/images")]
    [Tags("Images")]
    public class ImagesController : ControllerBase
    {
        private readonly IImageCacheService _imageCacheService;
        private readonly IAudiobookMetadataService _audiobookMetadataService;
        private readonly AudibleService _audibleService;
        private readonly IAudnexusService _audnexusService;
        private readonly IAudiobookRepository _audiobookRepository;
        private readonly IOpenLibraryService? _openLibraryService;
        private readonly ILogger<ImagesController> _logger;
        private readonly IApplicationPathService _applicationPathService;
        private readonly ImagePlaceholderResolver _placeholderResolver;
        private readonly ImageResponseBuilder _imageResponseBuilder;
        private readonly ImagePathValidator _imagePathValidator;
        private readonly ImageCachedPathValidator _cachedPathValidator;
        private readonly ImageFallbackDownloadWorkflow _fallbackDownloadWorkflow;
        private readonly ImageCandidateLookupWorkflow _imageCandidateLookupWorkflow;
        private readonly string _effectiveContentRootPath;
        private readonly IFileSystem _fileSystem;

        [ActivatorUtilitiesConstructor]
        public ImagesController(
            IImageCacheService imageCacheService,
            IAudiobookMetadataService audiobookMetadataService,
            AudibleService audibleService,
            IAudnexusService audnexusService,
            IAudiobookRepository audiobookRepository,
            ILogger<ImagesController> logger,
            IApplicationPathService applicationPathService,
            IFileSystem fileSystem)
            : this(
                imageCacheService,
                audiobookMetadataService,
                audibleService,
                audnexusService,
                audiobookRepository,
                openLibraryService: null,
                logger,
                applicationPathService,
                fileSystem,
                placeholderResolver: null)
        {
        }

        public ImagesController(
            IImageCacheService imageCacheService,
            IAudiobookMetadataService audiobookMetadataService,
            AudibleService audibleService,
            IAudnexusService audnexusService,
            IAudiobookRepository audiobookRepository,
            IOpenLibraryService? openLibraryService,
            ILogger<ImagesController> logger,
            IApplicationPathService applicationPathService,
            IFileSystem fileSystem,
            ImagePlaceholderResolver? placeholderResolver = null)
        {
            _imageCacheService = imageCacheService;
            _audiobookMetadataService = audiobookMetadataService;
            _audibleService = audibleService;
            _audnexusService = audnexusService;
            _audiobookRepository = audiobookRepository;
            _openLibraryService = openLibraryService;
            _logger = logger;
            _applicationPathService = applicationPathService;
            _fileSystem = fileSystem;
            _placeholderResolver = placeholderResolver ?? new ImagePlaceholderResolver(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ImagePlaceholderResolver>.Instance,
                fileSystem);
            _effectiveContentRootPath = applicationPathService.ContentRootPath;
            _imageResponseBuilder = new ImageResponseBuilder(_placeholderResolver, _logger, _effectiveContentRootPath);
            _imagePathValidator = new ImagePathValidator(_effectiveContentRootPath);
            _cachedPathValidator = new ImageCachedPathValidator(_imagePathValidator, fileSystem, _logger);
            _fallbackDownloadWorkflow = new ImageFallbackDownloadWorkflow(_imageCacheService, _logger);
            _imageCandidateLookupWorkflow = new ImageCandidateLookupWorkflow(
                _imageCacheService,
                _audiobookMetadataService,
                _audibleService,
                _audnexusService,
                _audiobookRepository,
                _openLibraryService,
                _fallbackDownloadWorkflow,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ImageCandidateLookupWorkflow>.Instance);
        }

        /// <summary>
        /// Get a cached cover image by identifier (ASIN, numeric ID, or author name). Optionally download on demand via a URL query parameter.
        /// </summary>
        /// <param name="identifier">Image identifier (typically an ASIN or database ID).</param>
        /// <returns>The image file with appropriate content type and caching headers.</returns>
        [HttpGet("{identifier}")]
        public async Task<IActionResult> GetImage(string identifier)
        {
            var identifierValidation = ImageIdentifierRequestValidator.ValidateGetImageIdentifier(identifier);
            if (identifierValidation.Failure == ImageIdentifierValidationFailure.Missing)
            {
                return BadRequest("Identifier is required");
            }

            identifier = identifierValidation.Identifier;
            if (identifierValidation.Failure == ImageIdentifierValidationFailure.Invalid)
            {
                _logger.LogWarning("Rejected invalid identifier: {Identifier}", LogRedaction.SanitizeText(identifier));
                return BadRequest("Invalid identifier");
            }

            // Check for url parameter to download on demand
            var url = Request.Query["url"].ToString();
            if (!string.IsNullOrWhiteSpace(url) && (url.StartsWith("http://") || url.StartsWith("https://")))
            {
                // Try to download and cache the image
                try
                {
                    var downloaded = await _imageCacheService.DownloadAndCacheImageAsync(url, identifier);
                    if (!string.IsNullOrWhiteSpace(downloaded))
                    {
                        _logger.LogInformation("Downloaded image on demand for identifier: {Identifier}", LogRedaction.SanitizeText(identifier));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to download image on demand for identifier: {Identifier}", LogRedaction.SanitizeText(identifier));
                }
            }

            try
            {
                // Get the cached image path (checks library first, then temp)
                var relativePath = await _imageCacheService.GetCachedImagePathAsync(identifier);
                _logger.LogInformation("ImagesController DEBUG: returned relativePath='{RelativePath}' for identifier {Identifier}", LogRedaction.SanitizeFilePath(relativePath), LogRedaction.SanitizeText(identifier));
                bool movedAttempted = false;

                // Shortcut: if the returned relative path clearly points to a temp cache
                // layout, attempt to move it into library storage immediately. This
                // handles cases where path normalization/validation later could
                // interfere with detection (unit tests expect the move to be invoked).
                if (!string.IsNullOrWhiteSpace(relativePath))
                {
                    try
                    {
                        var preNormalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                        if (preNormalized.IndexOf(Path.Join("cache", "images", "temp"), StringComparison.OrdinalIgnoreCase) >= 0 ||
                            preNormalized.IndexOf(Path.Join("config", "cache", "images", "temp"), StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var book = await _audiobookRepository.GetByAsinAsync(identifier);
                            if (book != null)
                            {
                                movedAttempted = true;
                                var moved = await _imageCacheService.MoveToLibraryStorageAsync(identifier, null);
                                if (!string.IsNullOrWhiteSpace(moved))
                                {
                                    relativePath = moved;
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Pre-validation move attempt failed for identifier {Identifier}", LogRedaction.SanitizeText(identifier));
                    }
                }

                // Track whether we currently have a valid image path. Recompute after
                // validation/moves because `relativePath` may be nullified or replaced.

                // Sanitize/validate the returned relative path to ensure it points inside
                // known image directories. Treat any unexpected location as not-found.
                relativePath = _cachedPathValidator.ValidateReturnedPath(identifier, relativePath);

                // If we found a temp cached image but the identifier corresponds to an audiobook in the library,
                // attempt to move it into permanent library storage so library images don't live in /temp.
                if (!string.IsNullOrWhiteSpace(relativePath))
                {
                    var normalizedRelative = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                    _logger.LogDebug("ImagesController: normalizedRelative for {Identifier}: {Normalized}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(normalizedRelative));
                    if (!movedAttempted && normalizedRelative.Contains(Path.Join("cache", "images", "temp")))
                    {
                        try
                        {
                            var book = await _audiobookRepository.GetByAsinAsync(identifier);
                            if (book != null)
                            {
                                _logger.LogInformation("Found temp cached image for library audiobook {Identifier}, attempting move to library storage", LogRedaction.SanitizeText(identifier));
                                var moved = await _imageCacheService.MoveToLibraryStorageAsync(identifier, null);
                                if (!string.IsNullOrWhiteSpace(moved))
                                {
                                    // Prefer the moved library path when serving the image
                                    // Validate moved path as well
                                    if (_cachedPathValidator.IsValidMovedPath(identifier, moved))
                                    {
                                        relativePath = moved;
                                    }
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogWarning(ex, "Failed to move temp image to library for {Identifier}", LogRedaction.SanitizeText(identifier));
                        }
                    }

                }

                var hasValidImagePath = !string.IsNullOrWhiteSpace(relativePath);
                var hasRequestedImageUrl = !string.IsNullOrWhiteSpace(url);
                if (!hasValidImagePath && !hasRequestedImageUrl)
                {
                    _logger.LogWarning("Image not found for identifier: {Identifier}", LogRedaction.SanitizeText(identifier));

                    // Cache is missing and caller did not provide a URL. Try metadata providers:
                    // ASIN => Audible, then Audnexus; ISBN => OpenLibrary cover URL.
                    relativePath = await _imageCandidateLookupWorkflow.TryResolveAsync(identifier!, relativePath, Request.Query["region"].ToString());

                    if (relativePath == null)
                    {
                        return CreatePlaceholderResult(
                            logContext: "missing identifier",
                            logValue: identifier,
                            notFoundMessage: "Image not found");
                    }
                }


                // Defensive: If relativePath is null, return NotFound
                if (relativePath == null)
                {
                    _logger.LogWarning("Image service returned null relativePath for identifier {Identifier}", LogRedaction.SanitizeText(identifier));
                    Response.Headers["Cache-Control"] = "public, max-age=300";
                    return NotFound(new { message = "Image not found" });
                }

                // Build the full file path
                var fullPath = ImageIdentifierHelper.ResolvePathWithOptionalBase(_effectiveContentRootPath, relativePath);

                if (!_fileSystem.FileExists(fullPath))
                {
                    _logger.LogWarning("Image file does not exist at path: {Path}", LogRedaction.SanitizeFilePath(fullPath));
                    return CreatePlaceholderResult(
                        logContext: "missing file",
                        logValue: fullPath,
                        notFoundMessage: "Image file not found");
                }

                return _imageResponseBuilder.CreateCachedImageResult(Response.Headers, identifier!, relativePath, fullPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error retrieving image for identifier: {Identifier}", LogRedaction.SanitizeText(identifier));
                return StatusCode(500, new { message = "Error retrieving image" });
            }
        }

        private IActionResult CreatePlaceholderResult(string logContext, string? logValue, string notFoundMessage)
        {
            return _imageResponseBuilder.CreatePlaceholderResult(
                Response.Headers,
                HttpContext?.Request?.Path ?? PathString.Empty,
                logContext,
                logValue,
                notFoundMessage);
        }

        /// <summary>
        /// Delete a cached cover image by identifier.
        /// </summary>
        /// <param name="identifier">Image identifier to delete.</param>
        [HttpDelete("{identifier}")]
        public async Task<IActionResult> DeleteImage(string identifier)
        {
            if (string.IsNullOrEmpty(identifier))
            {
                return BadRequest("Identifier is required");
            }

            try
            {
                var relativePath = await _imageCacheService.GetCachedImagePathAsync(identifier);

                if (relativePath == null)
                {
                    return NotFound(new { message = "Image not found" });
                }

                var fullPath = ImageIdentifierHelper.ResolvePathWithOptionalBase(_effectiveContentRootPath, relativePath);

                if (_fileSystem.FileExists(fullPath))
                {
                    if (!_fileSystem.TryValidateMutationTarget(fullPath, [_effectiveContentRootPath], out var safePath, out var reason))
                    {
                        _logger.LogWarning(
                            "Blocked image delete for identifier {Identifier}: {Reason}",
                            LogRedaction.SanitizeText(identifier),
                            LogRedaction.SanitizeText(reason));
                        return BadRequest(new { message = "Image path is outside the allowed cache root" });
                    }

                    _fileSystem.DeleteFile(safePath);
                    _logger.LogInformation("Deleted cached image for identifier: {Identifier}", LogRedaction.SanitizeText(identifier));
                    return Ok(new { message = "Image deleted successfully" });
                }

                return NotFound(new { message = "Image file not found" });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error deleting image for identifier: {Identifier}", LogRedaction.SanitizeText(identifier));
                return StatusCode(500, new { message = "Error deleting image" });
            }
        }
    }
}
