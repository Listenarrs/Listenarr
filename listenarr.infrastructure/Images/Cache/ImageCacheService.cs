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

using AsyncKeyedLock;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Images.Cache
{
    public partial class ImageCacheService : IImageCacheService, IDisposable
    {
        private const long MaxDownloadedImageBytes = 10L * 1024L * 1024L;
        private readonly ILogger<ImageCacheService> _logger;
        private readonly HttpClient _httpClient;
        private readonly ImageDownloadValidator _downloadValidator;
        private readonly string _tempCachePath;
        private readonly string _libraryImagePath;
        private readonly string _authorImagePath;
        private readonly string _seriesImagePath;
        private readonly string _contentRootPath;
        private readonly ImageCachePathResolver _pathResolver;
        private readonly ImageCacheStorageLookup _storageLookup;
        private readonly AsyncKeyedLocker<string> _downloadLocks = new();

        public ImageCacheService(
            ILogger<ImageCacheService> logger,
            HttpClient httpClient,
            IApplicationPathService applicationPathService)
        {
            _logger = logger;
            _httpClient = httpClient;
            _downloadValidator = new ImageDownloadValidator(_httpClient, _logger);
            _contentRootPath = applicationPathService.ContentRootPath;
            _tempCachePath = applicationPathService.ResolveFromConfig("cache", "images", "temp");
            _libraryImagePath = applicationPathService.ResolveFromConfig("cache", "images", "library");
            _authorImagePath = applicationPathService.ResolveFromConfig("cache", "images", "authors");
            _seriesImagePath = applicationPathService.ResolveFromConfig("cache", "images", "series");
            _pathResolver = new ImageCachePathResolver(_contentRootPath);
            _storageLookup = new ImageCacheStorageLookup(
                _pathResolver,
                _logger,
                _libraryImagePath,
                _authorImagePath,
                _seriesImagePath,
                _tempCachePath);

            Directory.CreateDirectory(_tempCachePath);
            Directory.CreateDirectory(_libraryImagePath);
            Directory.CreateDirectory(_authorImagePath);
            Directory.CreateDirectory(_seriesImagePath);
        }

        /// <summary>
        /// Downloads an image from a URL and caches it temporarily
        /// </summary>
        public async Task<string?> DownloadAndCacheImageAsync(string imageUrl, string identifier)
        {
            if (string.IsNullOrWhiteSpace(imageUrl) || string.IsNullOrWhiteSpace(identifier))
            {
                _logger.LogWarning("Cannot cache image: URL or identifier is empty");
                return null;
            }
            if (!ImageDownloadValidator.TryValidateExternalImageUrl(imageUrl, out var validationReason))
            {
                _logger.LogWarning("Blocked image download URL for {Identifier}: {Reason}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(validationReason));
                return null;
            }

            try
            {
                // Check library storage first
                var libraryPath = _storageLookup.FindLibraryPath(identifier);
                if (!string.IsNullOrEmpty(libraryPath))
                {
                    _logger.LogInformation("Image already in library storage: {Identifier}", LogRedaction.SanitizeText(identifier));
                    return GetRelativePath(libraryPath);
                }

                // Also check authors storage (author images may be stored separately)
                var authorPath = _storageLookup.FindAuthorPath(identifier);
                if (!string.IsNullOrEmpty(authorPath))
                {
                    _logger.LogInformation("Image already in author storage: {Identifier}", LogRedaction.SanitizeText(identifier));
                    return GetRelativePath(authorPath);
                }

                var seriesPath = _storageLookup.FindSeriesPath(identifier);
                if (!string.IsNullOrEmpty(seriesPath))
                {
                    _logger.LogInformation("Image already in series storage: {Identifier}", LogRedaction.SanitizeText(identifier));
                    return GetRelativePath(seriesPath);
                }

                // Check temp cache for a valid (non-placeholder) image
                var tempExisting = _storageLookup.FindTempPath(identifier);
                if (!string.IsNullOrEmpty(tempExisting))
                {
                    _logger.LogInformation("Image already cached: {Identifier}", LogRedaction.SanitizeText(identifier));
                    return GetRelativePath(tempExisting);
                }

                _logger.LogInformation("Downloading image from {Url} for {Identifier}", LogRedaction.SanitizeText(imageUrl), LogRedaction.SanitizeText(identifier));

                // Skip known Amazon placeholder URL to avoid caching tiny grey-pixel images
                if (imageUrl.Contains("grey-pixel.gif", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Skipping known grey-pixel placeholder URL for {Identifier}", LogRedaction.SanitizeText(identifier));
                    return null;
                }

                // Use per-identifier lock to prevent concurrent downloads for same identifier
                using var _ = await _downloadLocks.LockAsync(identifier);

                // Re-check after acquiring lock
                libraryPath = _storageLookup.FindLibraryPath(identifier);
                if (!string.IsNullOrEmpty(libraryPath))
                {
                    _logger.LogInformation("Image already in library storage (after wait): {Identifier}", LogRedaction.SanitizeText(identifier));
                    return GetRelativePath(libraryPath);
                }

                // Also check author storage after lock
                authorPath = _storageLookup.FindAuthorPath(identifier);
                if (!string.IsNullOrEmpty(authorPath))
                {
                    _logger.LogInformation("Image already in author storage (after wait): {Identifier}", LogRedaction.SanitizeText(identifier));
                    return GetRelativePath(authorPath);
                }

                seriesPath = _storageLookup.FindSeriesPath(identifier);
                if (!string.IsNullOrEmpty(seriesPath))
                {
                    _logger.LogInformation("Image already in series storage (after wait): {Identifier}", LogRedaction.SanitizeText(identifier));
                    return GetRelativePath(seriesPath);
                }

                tempExisting = _storageLookup.FindTempPath(identifier);
                if (!string.IsNullOrEmpty(tempExisting))
                {
                    _logger.LogInformation("Image already cached (after wait): {Identifier}", LogRedaction.SanitizeText(identifier));
                    return GetRelativePath(tempExisting);
                }

                // Download image with manual redirect handling so every redirect target is revalidated.
                var download = await _downloadValidator.DownloadWithValidatedRedirectsAsync(imageUrl);
                using var response = download.Response;
                var finalUri = download.FinalUri;
                response.EnsureSuccessStatusCode();

                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (!ImageCacheContentValidator.IsAllowedDownloadedImageContent(mediaType, finalUri))
                {
                    _logger.LogWarning(
                        "Blocked image download for {Identifier} from {Url}: unsupported content type {ContentType}",
                        LogRedaction.SanitizeText(identifier),
                        LogRedaction.SanitizeText(finalUri.ToString()),
                        LogRedaction.SanitizeText(mediaType ?? "(none)"));
                    return null;
                }

                var contentLength = response.Content.Headers.ContentLength;
                if (contentLength.HasValue && contentLength.Value > MaxDownloadedImageBytes)
                {
                    _logger.LogWarning(
                        "Blocked image download for {Identifier} from {Url}: content length {ContentLength} exceeds {MaxBytes} bytes",
                        LogRedaction.SanitizeText(identifier),
                        LogRedaction.SanitizeText(finalUri.ToString()),
                        contentLength.Value,
                        MaxDownloadedImageBytes);
                    return null;
                }

                // Read bytes first so we can reject tiny placeholder images (for example 1x1).
                var imageBytes = await ImageCacheContentReader.ReadWithLimitAsync(response.Content, MaxDownloadedImageBytes);
                if (ImageCacheContentValidator.IsPlaceholderImage(imageBytes, mediaType, _logger))
                {
                    _logger.LogInformation("Skipping placeholder/tiny image for {Identifier} from {Url}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(imageUrl));
                    return null;
                }

                // Determine file extension from content type or URL
                var extension = ImageCacheContentValidator.GetImageExtension(finalUri.ToString(), mediaType);
                var filePath = _pathResolver.BuildTempFilePath(identifier, extension, _tempCachePath);

                // Save to temp cache
                if (!FileSystemSafety.TryValidateMutationTarget(filePath, [_tempCachePath], out filePath, out var tempReason))
                {
                    _logger.LogWarning("Blocked image cache write for {Identifier}: {Reason}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(tempReason));
                    return null;
                }

                await File.WriteAllBytesAsync(filePath, imageBytes);

                _logger.LogInformation("Image cached successfully: {FilePath}", LogRedaction.SanitizeText(filePath));
                return GetRelativePath(filePath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to download and cache image from {Url}", LogRedaction.SanitizeText(imageUrl));
                return null;
            }
        }
    }
}
