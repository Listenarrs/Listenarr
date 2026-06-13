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
using Listenarr.Application.Security;
using Listenarr.Application.Interfaces;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;

namespace Listenarr.Infrastructure.Cache
{
    public class ImageCacheService : IImageCacheService, IDisposable
    {
        private const int MaxImageRedirects = 5;
        private const long MaxDownloadedImageBytes = 10L * 1024L * 1024L;
        private readonly ILogger<ImageCacheService> _logger;
        private readonly HttpClient _httpClient;
        private readonly string _tempCachePath;
        private readonly string _libraryImagePath;
        private readonly string _authorImagePath;
        private readonly string _seriesImagePath;
        private readonly string _contentRootPath;
        private readonly ImageCachePathResolver _pathResolver;
        private readonly AsyncKeyedLocker<string> _downloadLocks = new();

        public ImageCacheService(
            ILogger<ImageCacheService> logger,
            HttpClient httpClient,
            IApplicationPathService applicationPathService)
        {
            _logger = logger;
            _httpClient = httpClient;
            _contentRootPath = applicationPathService.ContentRootPath;
            _tempCachePath = applicationPathService.ResolveFromConfig("cache", "images", "temp");
            _libraryImagePath = applicationPathService.ResolveFromConfig("cache", "images", "library");
            _authorImagePath = applicationPathService.ResolveFromConfig("cache", "images", "authors");
            _seriesImagePath = applicationPathService.ResolveFromConfig("cache", "images", "series");
            _pathResolver = new ImageCachePathResolver(_contentRootPath);

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
            if (!TryValidateExternalImageUrl(imageUrl, out var validationReason))
            {
                _logger.LogWarning("Blocked image download URL for {Identifier}: {Reason}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(validationReason));
                return null;
            }

            try
            {
                // Check library storage first
                var libraryPath = GetImagePath(identifier, _libraryImagePath);
                if (File.Exists(libraryPath) && IsValidCachedCoverFile(libraryPath, identifier, "library"))
                {
                    _logger.LogInformation("Image already in library storage: {Identifier}", LogRedaction.SanitizeText(identifier));
                    return GetRelativePath(libraryPath);
                }

                // Also check authors storage (author images may be stored separately)
                var authorPath = GetImagePath(identifier, _authorImagePath);
                if (File.Exists(authorPath) && IsValidCachedCoverFile(authorPath, identifier, "author"))
                {
                    _logger.LogInformation("Image already in author storage: {Identifier}", LogRedaction.SanitizeText(identifier));
                    return GetRelativePath(authorPath);
                }

                var seriesPath = GetImagePath(identifier, _seriesImagePath);
                if (File.Exists(seriesPath) && IsValidCachedCoverFile(seriesPath, identifier, "series"))
                {
                    _logger.LogInformation("Image already in series storage: {Identifier}", LogRedaction.SanitizeText(identifier));
                    return GetRelativePath(seriesPath);
                }

                // Check temp cache for a valid (non-placeholder) image
                var tempExisting = GetBestTempImagePathIfValid(identifier);
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
                libraryPath = GetImagePath(identifier, _libraryImagePath);
                if (File.Exists(libraryPath) && IsValidCachedCoverFile(libraryPath, identifier, "library"))
                {
                    _logger.LogInformation("Image already in library storage (after wait): {Identifier}", LogRedaction.SanitizeText(identifier));
                    return GetRelativePath(libraryPath);
                }

                // Also check author storage after lock
                authorPath = GetImagePath(identifier, _authorImagePath);
                if (File.Exists(authorPath) && IsValidCachedCoverFile(authorPath, identifier, "author"))
                {
                    _logger.LogInformation("Image already in author storage (after wait): {Identifier}", LogRedaction.SanitizeText(identifier));
                    return GetRelativePath(authorPath);
                }

                seriesPath = GetImagePath(identifier, _seriesImagePath);
                if (File.Exists(seriesPath) && IsValidCachedCoverFile(seriesPath, identifier, "series"))
                {
                    _logger.LogInformation("Image already in series storage (after wait): {Identifier}", LogRedaction.SanitizeText(identifier));
                    return GetRelativePath(seriesPath);
                }

                tempExisting = GetBestTempImagePathIfValid(identifier);
                if (!string.IsNullOrEmpty(tempExisting))
                {
                    _logger.LogInformation("Image already cached (after wait): {Identifier}", LogRedaction.SanitizeText(identifier));
                    return GetRelativePath(tempExisting);
                }

                // Download image with manual redirect handling so every redirect target is revalidated.
                var download = await DownloadWithValidatedRedirectsAsync(imageUrl);
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
                var imageBytes = await ReadContentWithLimitAsync(response.Content, MaxDownloadedImageBytes);
                if (ImageCacheContentValidator.IsPlaceholderImage(imageBytes, mediaType, _logger))
                {
                    _logger.LogInformation("Skipping placeholder/tiny image for {Identifier} from {Url}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(imageUrl));
                    return null;
                }

                // Determine file extension from content type or URL
                var extension = ImageCacheContentValidator.GetImageExtension(finalUri.ToString(), mediaType);
                var filePath = _pathResolver.BuildTempFilePath(identifier, extension, _tempCachePath);

                // Save to temp cache
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
                    string? backupAuthorPath = null;

                    try
                    {
                        if (File.Exists(authorPath))
                        {
                            backupAuthorPath = authorPath + ".bak";
                            File.Copy(authorPath, backupAuthorPath, overwrite: true);
                            File.Delete(authorPath);
                        }

                        if (File.Exists(tempPath))
                        {
                            File.Delete(tempPath);
                        }

                        var refreshed = await DownloadAndCacheImageAsync(imageUrl, identifier);
                        if (string.IsNullOrWhiteSpace(refreshed) && !string.IsNullOrWhiteSpace(backupAuthorPath))
                        {
                            File.Move(backupAuthorPath, authorPath, overwrite: true);
                            return GetRelativePath(authorPath);
                        }

                        if (!string.IsNullOrWhiteSpace(backupAuthorPath) && File.Exists(backupAuthorPath))
                        {
                            File.Delete(backupAuthorPath);
                        }
                    }
                    catch
                    {
                        if (!string.IsNullOrWhiteSpace(backupAuthorPath) &&
                            File.Exists(backupAuthorPath) &&
                            !File.Exists(authorPath))
                        {
                            File.Move(backupAuthorPath, authorPath, overwrite: true);
                        }

                        throw;
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
                    string? backupSeriesPath = null;

                    try
                    {
                        if (File.Exists(seriesPath))
                        {
                            backupSeriesPath = seriesPath + ".bak";
                            File.Copy(seriesPath, backupSeriesPath, overwrite: true);
                            File.Delete(seriesPath);
                        }

                        if (File.Exists(tempPath))
                        {
                            File.Delete(tempPath);
                        }

                        var refreshed = await DownloadAndCacheImageAsync(imageUrl, identifier);
                        if (string.IsNullOrWhiteSpace(refreshed) && !string.IsNullOrWhiteSpace(backupSeriesPath))
                        {
                            File.Move(backupSeriesPath, seriesPath, overwrite: true);
                            return GetRelativePath(seriesPath);
                        }

                        if (!string.IsNullOrWhiteSpace(backupSeriesPath) && File.Exists(backupSeriesPath))
                        {
                            File.Delete(backupSeriesPath);
                        }
                    }
                    catch
                    {
                        if (!string.IsNullOrWhiteSpace(backupSeriesPath) &&
                            File.Exists(backupSeriesPath) &&
                            !File.Exists(seriesPath))
                        {
                            File.Move(backupSeriesPath, seriesPath, overwrite: true);
                        }

                        throw;
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
            var libraryPath = GetImagePath(identifier, _libraryImagePath);
            if (File.Exists(libraryPath) && IsValidCachedCoverFile(libraryPath, identifier, "library"))
                return Task.FromResult<string?>(GetRelativePath(libraryPath));

            // Check authors storage next
            var authorPath = GetImagePath(identifier, _authorImagePath);
            if (File.Exists(authorPath) && IsValidCachedCoverFile(authorPath, identifier, "author"))
                return Task.FromResult<string?>(GetRelativePath(authorPath));

            var seriesPath = GetImagePath(identifier, _seriesImagePath);
            if (File.Exists(seriesPath) && IsValidCachedCoverFile(seriesPath, identifier, "series"))
                return Task.FromResult<string?>(GetRelativePath(seriesPath));

            // Check temp cache and prefer non-placeholder images
            var tempBest = GetBestTempImagePathIfValid(identifier);
            if (!string.IsNullOrEmpty(tempBest))
                return Task.FromResult<string?>(GetRelativePath(tempBest));

            return Task.FromResult<string?>(null);
        }

        private string? GetBestTempImagePathIfValid(string identifier)
        {
            var extensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".svg" };

            foreach (var ext in extensions)
            {
                var path = _pathResolver.BuildFilePath(identifier, ext, _tempCachePath);
                if (!File.Exists(path)) continue;

                // Remove placeholder images (e.g. 1x1) from temp cache so fallback can continue.
                if (!IsValidCachedCoverFile(path, identifier, "temp"))
                {
                    continue;
                }

                return path;
            }

            return null;
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
                            File.Delete(file);
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

        private static async Task<byte[]> ReadContentWithLimitAsync(HttpContent content, long maxBytes)
        {
            await using var contentStream = await content.ReadAsStreamAsync();
            using var bufferStream = new MemoryStream();
            var buffer = new byte[81920];
            long totalBytes = 0;

            while (true)
            {
                var read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length));
                if (read == 0)
                {
                    break;
                }

                totalBytes += read;
                if (totalBytes > maxBytes)
                {
                    throw new InvalidOperationException($"Downloaded image exceeds the {maxBytes} byte limit.");
                }

                bufferStream.Write(buffer, 0, read);
            }

            return bufferStream.ToArray();
        }

        private async Task<(HttpResponseMessage Response, Uri FinalUri)> DownloadWithValidatedRedirectsAsync(string imageUrl)
        {
            if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var currentUri))
            {
                throw new InvalidOperationException("Invalid image URL format");
            }

            HttpResponseMessage? response = null;

            for (var redirectCount = 0; redirectCount <= MaxImageRedirects; redirectCount++)
            {
                if (!TryValidateExternalImageUri(currentUri, out var uriValidationReason))
                {
                    throw new InvalidOperationException($"Blocked image URL: {uriValidationReason}");
                }

                if (!await TryValidateResolvedExternalImageUriAsync(currentUri))
                {
                    throw new InvalidOperationException("Blocked image URL: DNS resolved to private or loopback address");
                }

                response?.Dispose();
                using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                if (IsRedirectStatusCode(response.StatusCode))
                {
                    var location = response.Headers.Location;
                    if (location == null)
                    {
                        throw new HttpRequestException($"Redirect response from {currentUri} did not include a Location header.");
                    }

                    var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                    if (!TryValidateExternalImageUri(nextUri, out var redirectValidationReason))
                    {
                        throw new InvalidOperationException($"Blocked redirect target: {redirectValidationReason}");
                    }

                    currentUri = nextUri;
                    continue;
                }

                var finalUri = response.RequestMessage?.RequestUri ?? currentUri;
                if (!TryValidateExternalImageUri(finalUri, out var finalValidationReason))
                {
                    throw new InvalidOperationException($"Blocked final image URL: {finalValidationReason}");
                }

                if (!await TryValidateResolvedExternalImageUriAsync(finalUri))
                {
                    throw new InvalidOperationException("Blocked final image URL: DNS resolved to private or loopback address");
                }

                return (response, finalUri);
            }

            response?.Dispose();
            throw new HttpRequestException($"Too many redirects while downloading image (>{MaxImageRedirects}).");
        }

        private static bool TryValidateExternalImageUrl(string imageUrl, out string reason)
        {
            reason = string.Empty;
            if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
            {
                reason = "Invalid URL format";
                return false;
            }

            return TryValidateExternalImageUri(uri, out reason);
        }

        private static bool TryValidateExternalImageUri(Uri uri, out string reason)
        {
            reason = string.Empty;

            if (!uri.IsAbsoluteUri)
            {
                reason = "URL must be absolute";
                return false;
            }

            if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                reason = $"Unsupported URL scheme '{uri.Scheme}'";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(uri.UserInfo))
            {
                reason = "URLs with embedded credentials are not allowed";
                return false;
            }

            var host = uri.Host ?? string.Empty;
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Localhost or local-network hostnames are not allowed";
                return false;
            }

            if (IPAddress.TryParse(host, out var ip) && IsPrivateOrLoopback(ip))
            {
                reason = "Private or loopback IP targets are not allowed";
                return false;
            }

            return true;
        }

        private async Task<bool> TryValidateResolvedExternalImageUriAsync(Uri uri)
        {
            try
            {
                var host = uri.Host;
                if (string.IsNullOrWhiteSpace(host))
                {
                    return false;
                }

                if (IPAddress.TryParse(host, out var ip))
                {
                    return !IsPrivateOrLoopback(ip);
                }

                var addresses = await Dns.GetHostAddressesAsync(host);
                if (addresses == null || addresses.Length == 0)
                {
                    _logger.LogWarning("Blocked image URL because DNS resolution returned no addresses: {Host}", LogRedaction.SanitizeText(host));
                    return false;
                }

                var privateOrLoopback = addresses.FirstOrDefault(IsPrivateOrLoopback);
                if (privateOrLoopback != null)
                {
                    _logger.LogWarning(
                        "Blocked image URL because DNS resolved to private/loopback address. Host={Host}, Address={Address}",
                        LogRedaction.SanitizeText(host),
                        privateOrLoopback);
                    return false;
                }

                return true;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "Blocked image URL because DNS resolution failed for host {Host}", LogRedaction.SanitizeText(uri.Host));
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Blocked image URL due to unexpected DNS validation error for host {Host}", LogRedaction.SanitizeText(uri.Host));
                return false;
            }
        }

        private static bool IsRedirectStatusCode(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.Moved
                || statusCode == HttpStatusCode.Redirect
                || statusCode == HttpStatusCode.RedirectMethod
                || statusCode == HttpStatusCode.TemporaryRedirect
                || (int)statusCode == 308; // Permanent Redirect
        }

        private static bool IsPrivateOrLoopback(System.Net.IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }

            if (System.Net.IPAddress.IsLoopback(ip)) return true;

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                if (b[0] == 10) return true;
                if (b[0] == 127) return true;
                if (b[0] == 169 && b[1] == 254) return true;
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
                if (b[0] == 192 && b[1] == 168) return true;
                return false;
            }

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
                var b = ip.GetAddressBytes();
                if (b.Length > 0 && (b[0] & 0xFE) == 0xFC) return true; // fc00::/7
                return false;
            }

            return false;
        }

        private bool IsValidCachedCoverFile(string filePath, string identifier, string bucket)
        {
            try
            {
                if (!File.Exists(filePath)) return false;
                var bytes = File.ReadAllBytes(filePath);
                var mediaType = ImageCacheContentValidator.GetMediaTypeFromExtension(Path.GetExtension(filePath));
                if (ImageCacheContentValidator.IsPlaceholderImage(bytes, mediaType, _logger))
                {
                    _logger.LogInformation("Deleting placeholder/tiny cached image for {Identifier} in {Bucket}: {Path}", LogRedaction.SanitizeText(identifier), bucket, LogRedaction.SanitizeText(filePath));
                    try
                    {
                        File.Delete(filePath);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogDebug(ex, "Failed deleting invalid cached image for {Identifier} in {Bucket}: {Path}", LogRedaction.SanitizeText(identifier), bucket, LogRedaction.SanitizeText(filePath));
                    }
                    return false;
                }
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed validating cached image file for {Identifier}: {Path}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(filePath));
                return false;
            }
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
