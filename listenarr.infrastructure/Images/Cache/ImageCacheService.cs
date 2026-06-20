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
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Images.Cache
{
    public class ImageCacheService : IImageCacheService, IDisposable
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
                if (!FileUtils.TryValidateMutationTarget(filePath, [_tempCachePath], out filePath, out var tempReason))
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

        /// <summary>
        /// Moves an image from temp cache to permanent library storage
        /// </summary>
        public async Task<string?> MoveToLibraryStorageAsync(string identifier, string? imageUrl = null)
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                _logger.LogWarning("Cannot move image: identifier is empty");
                return null;
            }

            try
            {
                // Check if already in library storage
                var libraryPath = GetImagePath(identifier, _libraryImagePath);
                if (File.Exists(libraryPath))
                {
                    _logger.LogInformation("Image already in library storage: {Identifier}", LogRedaction.SanitizeText(identifier));
                    return GetRelativePath(libraryPath);
                }

                // Find the temp cached file
                var tempPath = GetImagePath(identifier, _tempCachePath);
                if (!File.Exists(tempPath))
                {
                    _logger.LogWarning("Temp cached image not found for {Identifier}", LogRedaction.SanitizeText(identifier));
                    // If imageUrl provided, attempt to download to temp cache using the identifier
                    if (!string.IsNullOrWhiteSpace(imageUrl))
                    {
                        _logger.LogInformation("Attempting to download image for {Identifier} from provided URL", LogRedaction.SanitizeText(identifier));
                        var cached = await DownloadAndCacheImageAsync(imageUrl, identifier);
                        if (string.IsNullOrWhiteSpace(cached))
                        {
                            _logger.LogWarning("Download to temp cache failed for {Identifier}", LogRedaction.SanitizeText(identifier));
                            return null;
                        }

                        // Recompute tempPath after download
                        tempPath = GetImagePath(identifier, _tempCachePath);
                        if (!File.Exists(tempPath))
                        {
                            _logger.LogWarning("Downloaded file not found in temp cache for {Identifier}", LogRedaction.SanitizeText(identifier));
                            return null;
                        }
                    }
                    else
                    {
                        return null;
                    }
                }

                // Move to library storage
                Directory.CreateDirectory(_libraryImagePath);
                if (!TryValidateCacheMove(tempPath, _tempCachePath, libraryPath, _libraryImagePath, identifier, out tempPath, out libraryPath))
                {
                    return null;
                }

                File.Move(tempPath, libraryPath, overwrite: true);

                _logger.LogInformation("Image moved to library storage: {Identifier}", LogRedaction.SanitizeText(identifier));
                return GetRelativePath(libraryPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to move image to library storage for {Identifier}", LogRedaction.SanitizeText(identifier));
                return null;
            }
        }

        /// <summary>
        /// Moves an image from temp cache to permanent authors storage
        /// </summary>
        public async Task<string?> MoveToAuthorLibraryStorageAsync(string identifier, string? imageUrl = null, bool forceRefresh = false)
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                _logger.LogWarning("Cannot move author image: identifier is empty");
                return null;
            }

            try
            {
                var authorPath = GetImagePath(identifier, _authorImagePath);
                var tempPath = GetImagePath(identifier, _tempCachePath);

                if (forceRefresh && !string.IsNullOrWhiteSpace(imageUrl))
                {
                    var restored = await ImageCacheRefreshWorkflow.RefreshWithBackupAsync(
                        authorPath,
                        tempPath,
                        _authorImagePath,
                        _tempCachePath,
                        () => DownloadAndCacheImageAsync(imageUrl, identifier),
                        GetRelativePath);
                    if (!string.IsNullOrWhiteSpace(restored))
                    {
                        return restored;
                    }
                }

                // Check if already in author storage
                if (File.Exists(authorPath))
                {
                    _logger.LogInformation("Author image already in author storage: {Identifier}", LogRedaction.SanitizeText(identifier));
                    return GetRelativePath(authorPath);
                }

                // Find the temp cached file
                if (!File.Exists(tempPath))
                {
                    _logger.LogWarning("Temp cached author image not found for {Identifier}", LogRedaction.SanitizeText(identifier));
                    // If imageUrl provided, attempt to download to temp cache using the identifier
                    if (!string.IsNullOrWhiteSpace(imageUrl))
                    {
                        _logger.LogInformation("Attempting to download author image for {Identifier} from provided URL", LogRedaction.SanitizeText(identifier));
                        var cached = await DownloadAndCacheImageAsync(imageUrl, identifier);
                        if (string.IsNullOrWhiteSpace(cached))
                        {
                            _logger.LogWarning("Download to temp cache failed for {Identifier}", LogRedaction.SanitizeText(identifier));
                            return null;
                        }

                        // Recompute tempPath after download
                        tempPath = GetImagePath(identifier, _tempCachePath);
                        if (!File.Exists(tempPath))
                        {
                            _logger.LogWarning("Downloaded file not found in temp cache for {Identifier}", LogRedaction.SanitizeText(identifier));
                            return null;
                        }
                    }
                    else
                    {
                        return null;
                    }
                }

                // Move to author storage
                Directory.CreateDirectory(_authorImagePath);
                if (!TryValidateCacheMove(tempPath, _tempCachePath, authorPath, _authorImagePath, identifier, out tempPath, out authorPath))
                {
                    return null;
                }

                File.Move(tempPath, authorPath, overwrite: true);

                _logger.LogInformation("Author image moved to author storage: {Identifier}", LogRedaction.SanitizeText(identifier));
                return GetRelativePath(authorPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to move author image to author storage for {Identifier}", LogRedaction.SanitizeText(identifier));
                return null;
            }
        }

        public async Task<string?> MoveToSeriesLibraryStorageAsync(string identifier, string? imageUrl = null, bool forceRefresh = false)
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                _logger.LogWarning("Cannot move series image: identifier is empty");
                return null;
            }

            try
            {
                var seriesPath = GetImagePath(identifier, _seriesImagePath);
                var tempPath = GetImagePath(identifier, _tempCachePath);

                if (forceRefresh && !string.IsNullOrWhiteSpace(imageUrl))
                {
                    var restored = await ImageCacheRefreshWorkflow.RefreshWithBackupAsync(
                        seriesPath,
                        tempPath,
                        _seriesImagePath,
                        _tempCachePath,
                        () => DownloadAndCacheImageAsync(imageUrl, identifier),
                        GetRelativePath);
                    if (!string.IsNullOrWhiteSpace(restored))
                    {
                        return restored;
                    }
                }

                if (File.Exists(seriesPath))
                {
                    _logger.LogInformation("Series image already in series storage: {Identifier}", LogRedaction.SanitizeText(identifier));
                    return GetRelativePath(seriesPath);
                }

                if (!File.Exists(tempPath))
                {
                    _logger.LogWarning("Temp cached series image not found for {Identifier}", LogRedaction.SanitizeText(identifier));
                    if (!string.IsNullOrWhiteSpace(imageUrl))
                    {
                        _logger.LogInformation("Attempting to download series image for {Identifier} from provided URL", LogRedaction.SanitizeText(identifier));
                        var cached = await DownloadAndCacheImageAsync(imageUrl, identifier);
                        if (string.IsNullOrWhiteSpace(cached))
                        {
                            _logger.LogWarning("Download to temp cache failed for series {Identifier}", LogRedaction.SanitizeText(identifier));
                            return null;
                        }

                        tempPath = GetImagePath(identifier, _tempCachePath);
                        if (!File.Exists(tempPath))
                        {
                            _logger.LogWarning("Downloaded series file not found in temp cache for {Identifier}", LogRedaction.SanitizeText(identifier));
                            return null;
                        }
                    }
                    else
                    {
                        return null;
                    }
                }

                Directory.CreateDirectory(_seriesImagePath);
                if (!TryValidateCacheMove(tempPath, _tempCachePath, seriesPath, _seriesImagePath, identifier, out tempPath, out seriesPath))
                {
                    return null;
                }

                File.Move(tempPath, seriesPath, overwrite: true);

                _logger.LogInformation("Series image moved to series storage: {Identifier}", LogRedaction.SanitizeText(identifier));
                return GetRelativePath(seriesPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to move series image to series storage for {Identifier}", LogRedaction.SanitizeText(identifier));
                return null;
            }
        }

        /// <summary>
        /// Gets the cached image path if it exists
        /// </summary>
        public Task<string?> GetCachedImagePathAsync(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier))
                return Task.FromResult<string?>(null);

            // Special-case for built-in unavailable cover asset
            if (string.Equals(identifier, "cover-unavailable", StringComparison.OrdinalIgnoreCase))
            {
                var staticPath = Path.Join(_contentRootPath, "wwwroot", "images", "cover-unavailable.svg");
                if (File.Exists(staticPath))
                    return Task.FromResult<string?>(GetRelativePath(staticPath));
            }


            // Check library storage first
            var libraryPath = _storageLookup.FindLibraryPath(identifier);
            if (!string.IsNullOrEmpty(libraryPath))
                return Task.FromResult<string?>(GetRelativePath(libraryPath));

            // Check authors storage next
            var authorPath = _storageLookup.FindAuthorPath(identifier);
            if (!string.IsNullOrEmpty(authorPath))
                return Task.FromResult<string?>(GetRelativePath(authorPath));

            var seriesPath = _storageLookup.FindSeriesPath(identifier);
            if (!string.IsNullOrEmpty(seriesPath))
                return Task.FromResult<string?>(GetRelativePath(seriesPath));

            // Check temp cache and prefer non-placeholder images
            var tempBest = _storageLookup.FindTempPath(identifier);
            if (!string.IsNullOrEmpty(tempBest))
                return Task.FromResult<string?>(GetRelativePath(tempBest));

            return Task.FromResult<string?>(null);
        }

        /// <summary>
        /// Clears all temporary cached images
        /// </summary>
        public Task ClearTempCacheAsync()
        {
            try
            {
                _logger.LogInformation("Clearing temp image cache");

                if (Directory.Exists(_tempCachePath))
                {
                    var files = Directory.GetFiles(_tempCachePath);
                    foreach (var file in files)
                    {
                        try
                        {
                            if (!FileUtils.TryValidateMutationTarget(file, [_tempCachePath], out var safeFile, out var reason))
                            {
                                _logger.LogWarning("Blocked temp cache delete for {File}: {Reason}", LogRedaction.SanitizeFilePath(file), LogRedaction.SanitizeText(reason));
                                continue;
                            }

                            File.Delete(safeFile);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogWarning(ex, "Failed to delete cached file: {File}", file);
                        }
                    }
                    _logger.LogInformation("Temp cache cleared: {Count} files deleted", files.Length);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to clear temp cache");
            }

            return Task.CompletedTask;
        }

        private string GetImagePath(string identifier, string basePath)
        {
            return _pathResolver.GetImagePath(identifier, basePath);
        }

        private string GetRelativePath(string fullPath)
        {
            return _pathResolver.GetRelativePath(fullPath);
        }

        private bool TryValidateCacheMove(
            string sourcePath,
            string sourceRoot,
            string destinationPath,
            string destinationRoot,
            string identifier,
            out string safeSourcePath,
            out string safeDestinationPath)
        {
            safeSourcePath = sourcePath;
            safeDestinationPath = destinationPath;

            if (!FileUtils.TryValidateMutationTarget(sourcePath, [sourceRoot], out safeSourcePath, out var sourceReason))
            {
                _logger.LogWarning("Blocked cached image move for {Identifier}: source invalid: {Reason}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(sourceReason));
                return false;
            }

            if (!FileUtils.TryValidateMutationTarget(destinationPath, [destinationRoot], out safeDestinationPath, out var destinationReason))
            {
                _logger.LogWarning("Blocked cached image move for {Identifier}: destination invalid: {Reason}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(destinationReason));
                return false;
            }

            return true;
        }

        public void Dispose()
        {
            try
            {
                _httpClient.Dispose();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed disposing HttpClient in ImageCacheService");
            }
        }
    }
}
