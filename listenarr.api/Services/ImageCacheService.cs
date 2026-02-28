/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2025 Robbie Davis
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

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;

namespace Listenarr.Api.Services
{
    public interface IImageCacheService
    {
        Task<string?> DownloadAndCacheImageAsync(string imageUrl, string identifier);
        Task<string?> MoveToLibraryStorageAsync(string identifier, string? imageUrl = null);
        Task<string?> MoveToAuthorLibraryStorageAsync(string identifier, string? imageUrl = null);
        Task<string?> GetCachedImagePathAsync(string identifier);
        Task ClearTempCacheAsync();
    }

    public class ImageCacheService : IImageCacheService, IDisposable
    {
        private const int MaxImageRedirects = 5;
        private readonly ILogger<ImageCacheService> _logger;
        private readonly HttpClient _httpClient;
        private readonly HttpClient _httpClientNoRedirect;
        private readonly string _tempCachePath;
        private readonly string _libraryImagePath;
        private readonly string _authorImagePath;
    private readonly string _contentRootPath;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.SemaphoreSlim> _downloadLocks = new();
    public ImageCacheService(ILogger<ImageCacheService> logger, IHttpClientFactory httpClientFactory, string contentRootPath)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
        _httpClientNoRedirect = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false
        })
        {
            Timeout = _httpClient.Timeout
        };
        _contentRootPath = contentRootPath;

        // Set up cache directories relative to content root
        var baseDir = Path.Combine(contentRootPath, "config");
        _tempCachePath = Path.Combine(baseDir, "cache", "images", "temp");
        _libraryImagePath = Path.Combine(baseDir, "cache", "images", "library");
        _authorImagePath = Path.Combine(baseDir, "cache", "images", "authors");

        // Ensure directories exist
        Directory.CreateDirectory(_tempCachePath);
        Directory.CreateDirectory(_libraryImagePath);
        Directory.CreateDirectory(_authorImagePath);
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
                _logger.LogWarning("Blocked image download URL for {Identifier}: {Reason}", identifier, validationReason);
                return null;
            }

            try
            {
                // Check library storage first
                var libraryPath = GetImagePath(identifier, _libraryImagePath);
                if (File.Exists(libraryPath))
                {
                    if (IsValidCachedCoverFile(libraryPath, identifier, "library"))
                    {
                        _logger.LogInformation("Image already in library storage: {Identifier}", identifier);
                        return GetRelativePath(libraryPath);
                    }
                }

                // Also check authors storage (author images may be stored separately)
                var authorPath = GetImagePath(identifier, _authorImagePath);
                if (File.Exists(authorPath))
                {
                    if (IsValidCachedCoverFile(authorPath, identifier, "author"))
                    {
                        _logger.LogInformation("Image already in author storage: {Identifier}", identifier);
                        return GetRelativePath(authorPath);
                    }
                }

                // Check temp cache for a valid (non-placeholder) image
                var tempExisting = GetBestTempImagePathIfValid(identifier);
                if (!string.IsNullOrEmpty(tempExisting))
                {
                    _logger.LogInformation("Image already cached: {Identifier}", identifier);
                    return GetRelativePath(tempExisting);
                }

                _logger.LogInformation("Downloading image from {Url} for {Identifier}", imageUrl, identifier);

                // Skip known Amazon placeholder URL to avoid caching tiny grey-pixel images
                if (imageUrl.Contains("grey-pixel.gif", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Skipping known grey-pixel placeholder URL for {Identifier}", identifier);
                    return null;
                }

                // Use per-identifier lock to prevent concurrent downloads for same identifier
                var sem = _downloadLocks.GetOrAdd(identifier, _ => new System.Threading.SemaphoreSlim(1, 1));
                await sem.WaitAsync();
                try
                {
                    // Re-check after acquiring lock
                    libraryPath = GetImagePath(identifier, _libraryImagePath);
                    if (File.Exists(libraryPath))
                    {
                        if (IsValidCachedCoverFile(libraryPath, identifier, "library"))
                        {
                            _logger.LogInformation("Image already in library storage (after wait): {Identifier}", identifier);
                            return GetRelativePath(libraryPath);
                        }
                    }

                    // Also check author storage after lock
                    authorPath = GetImagePath(identifier, _authorImagePath);
                    if (File.Exists(authorPath))
                    {
                        if (IsValidCachedCoverFile(authorPath, identifier, "author"))
                        {
                            _logger.LogInformation("Image already in author storage (after wait): {Identifier}", identifier);
                            return GetRelativePath(authorPath);
                        }
                    }

                    tempExisting = GetBestTempImagePathIfValid(identifier);
                    if (!string.IsNullOrEmpty(tempExisting))
                    {
                        _logger.LogInformation("Image already cached (after wait): {Identifier}", identifier);
                        return GetRelativePath(tempExisting);
                    }

                    // Download image with manual redirect handling so every redirect target is revalidated.
                    var download = await DownloadWithValidatedRedirectsAsync(imageUrl);
                    using var response = download.Response;
                    var finalUri = download.FinalUri;
                    response.EnsureSuccessStatusCode();

                    // Read bytes first so we can reject tiny placeholder images (for example 1x1)
                    var imageBytes = await response.Content.ReadAsByteArrayAsync();
                    var mediaType = response.Content.Headers.ContentType?.MediaType;
                    if (IsPlaceholderImage(imageBytes, mediaType))
                    {
                        _logger.LogInformation("Skipping placeholder/tiny image for {Identifier} from {Url}", identifier, imageUrl);
                        return null;
                    }

                    // Determine file extension from content type or URL
                    var extension = GetImageExtension(finalUri.ToString(), response.Content.Headers.ContentType?.MediaType);
                    var fileName = $"{SanitizeFileName(identifier)}{extension}";
                    var filePath = Path.Combine(_tempCachePath, fileName);

                    // Save to temp cache
                    await File.WriteAllBytesAsync(filePath, imageBytes);

                    _logger.LogInformation("Image cached successfully: {FilePath}", filePath);
                    return GetRelativePath(filePath);
                }
                finally
                {
                    sem.Release();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Failed to download and cache image from {Url}", imageUrl);
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
                    _logger.LogInformation("Image already in library storage: {Identifier}", identifier);
                    return GetRelativePath(libraryPath);
                }

                // Find the temp cached file
                var tempPath = GetImagePath(identifier, _tempCachePath);
                if (!File.Exists(tempPath))
                {
                    _logger.LogWarning("Temp cached image not found for {Identifier}", identifier);
                    // If imageUrl provided, attempt to download to temp cache using the identifier
                    if (!string.IsNullOrWhiteSpace(imageUrl))
                    {
                        _logger.LogInformation("Attempting to download image for {Identifier} from provided URL", identifier);
                        var cached = await DownloadAndCacheImageAsync(imageUrl, identifier);
                        if (string.IsNullOrWhiteSpace(cached))
                        {
                            _logger.LogWarning("Download to temp cache failed for {Identifier}", identifier);
                            return null;
                        }

                        // Recompute tempPath after download
                        tempPath = GetImagePath(identifier, _tempCachePath);
                        if (!File.Exists(tempPath))
                        {
                            _logger.LogWarning("Downloaded file not found in temp cache for {Identifier}", identifier);
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

                _logger.LogInformation("Image moved to library storage: {Identifier}", identifier);
                return GetRelativePath(libraryPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Failed to move image to library storage for {Identifier}", identifier);
                return null;
            }
        }

        /// <summary>
        /// Moves an image from temp cache to permanent authors storage
        /// </summary>
        public async Task<string?> MoveToAuthorLibraryStorageAsync(string identifier, string? imageUrl = null)
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                _logger.LogWarning("Cannot move author image: identifier is empty");
                return null;
            }

            try
            {
                // Check if already in author storage
                var authorPath = GetImagePath(identifier, _authorImagePath);
                if (File.Exists(authorPath))
                {
                    _logger.LogInformation("Author image already in author storage: {Identifier}", identifier);
                    return GetRelativePath(authorPath);
                }

                // Find the temp cached file
                var tempPath = GetImagePath(identifier, _tempCachePath);
                if (!File.Exists(tempPath))
                {
                    _logger.LogWarning("Temp cached author image not found for {Identifier}", identifier);
                    // If imageUrl provided, attempt to download to temp cache using the identifier
                    if (!string.IsNullOrWhiteSpace(imageUrl))
                    {
                        _logger.LogInformation("Attempting to download author image for {Identifier} from provided URL", identifier);
                        var cached = await DownloadAndCacheImageAsync(imageUrl, identifier);
                        if (string.IsNullOrWhiteSpace(cached))
                        {
                            _logger.LogWarning("Download to temp cache failed for {Identifier}", identifier);
                            return null;
                        }

                        // Recompute tempPath after download
                        tempPath = GetImagePath(identifier, _tempCachePath);
                        if (!File.Exists(tempPath))
                        {
                            _logger.LogWarning("Downloaded file not found in temp cache for {Identifier}", identifier);
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

                _logger.LogInformation("Author image moved to author storage: {Identifier}", identifier);
                return GetRelativePath(authorPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Failed to move author image to author storage for {Identifier}", identifier);
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
                var staticPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "images", "cover-unavailable.svg");
                if (File.Exists(staticPath))
                    return Task.FromResult<string?>(GetRelativePath(staticPath));
            }


            // Check library storage first
            var libraryPath = GetImagePath(identifier, _libraryImagePath);
                if (File.Exists(libraryPath))
                {
                    if (IsValidCachedCoverFile(libraryPath, identifier, "library"))
                        return Task.FromResult<string?>(GetRelativePath(libraryPath));
                }

            // Check authors storage next
            var authorPath = GetImagePath(identifier, _authorImagePath);
                if (File.Exists(authorPath))
                {
                    if (IsValidCachedCoverFile(authorPath, identifier, "author"))
                        return Task.FromResult<string?>(GetRelativePath(authorPath));
                }

            // Check temp cache and prefer non-placeholder images
            var tempBest = GetBestTempImagePathIfValid(identifier);
            if (!string.IsNullOrEmpty(tempBest))
                return Task.FromResult<string?>(GetRelativePath(tempBest));

            return Task.FromResult<string?>(null);
        }

        private string? GetBestTempImagePathIfValid(string identifier)
        {
            var sanitized = SanitizeFileName(identifier);
            var extensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".svg" };

            foreach (var ext in extensions)
            {
                var path = Path.Combine(_tempCachePath, sanitized + ext);
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
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                            _logger.LogWarning(ex, "Failed to delete cached file: {File}", file);
                        }
                    }
                    _logger.LogInformation("Temp cache cleared: {Count} files deleted", files.Length);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Failed to clear temp cache");
            }

            return Task.CompletedTask;
        }

        private string GetImagePath(string identifier, string basePath)
        {
            // Try to find existing file with any extension
            var sanitized = SanitizeFileName(identifier);
            var extensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".svg" };

            foreach (var ext in extensions)
            {
                var path = Path.Combine(basePath, sanitized + ext);
                if (File.Exists(path))
                    return path;
            }

            // Default to .jpg if not found
            return Path.Combine(basePath, sanitized + ".jpg");
        }

        private string GetRelativePath(string fullPath)
        {
            var relativePath = Path.GetRelativePath(_contentRootPath, fullPath).Replace("\\", "/");
            return relativePath;
        }

        private string SanitizeFileName(string fileName)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return string.Join("_", fileName.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
        }

        private string GetImageExtension(string url, string? contentType)
        {
            // Try to get extension from content type
            if (!string.IsNullOrEmpty(contentType))
            {
                if (contentType.Contains("jpeg")) return ".jpg";
                if (contentType.Contains("png")) return ".png";
                if (contentType.Contains("webp")) return ".webp";
                if (contentType.Contains("gif")) return ".gif";
                if (contentType.Contains("svg+xml")) return ".svg";
            }

            // Try to get extension from URL
            var urlExtension = Path.GetExtension(url).ToLower();
            if (!string.IsNullOrEmpty(urlExtension) && urlExtension.Length <= 5)
                return urlExtension;

            // Default to .jpg
            return ".jpg";
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
                response = await _httpClientNoRedirect.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

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
                    _logger.LogWarning("Blocked image URL because DNS resolution returned no addresses: {Host}", host);
                    return false;
                }

                var privateOrLoopback = addresses.FirstOrDefault(IsPrivateOrLoopback);
                if (privateOrLoopback != null)
                {
                    _logger.LogWarning(
                        "Blocked image URL because DNS resolved to private/loopback address. Host={Host}, Address={Address}",
                        host,
                        privateOrLoopback);
                    return false;
                }

                return true;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "Blocked image URL because DNS resolution failed for host {Host}", uri.Host);
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogWarning(ex, "Blocked image URL due to unexpected DNS validation error for host {Host}", uri.Host);
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
                var mediaType = GetMediaTypeFromExtension(Path.GetExtension(filePath));
                if (IsPlaceholderImage(bytes, mediaType))
                {
                    _logger.LogInformation("Deleting placeholder/tiny cached image for {Identifier} in {Bucket}: {Path}", identifier, bucket, filePath);
                    try
                    {
                        File.Delete(filePath);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                        _logger.LogDebug(ex, "Failed deleting invalid cached image for {Identifier} in {Bucket}: {Path}", identifier, bucket, filePath);
                    }
                    return false;
                }
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogWarning(ex, "Failed validating cached image file for {Identifier}: {Path}", identifier, filePath);
                return false;
            }
        }

        private static string? GetMediaTypeFromExtension(string ext)
        {
            return ext.ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".svg" => "image/svg+xml",
                _ => null
            };
        }

        private bool IsPlaceholderImage(byte[] data, string? mediaType)
        {
            if (data == null || data.Length == 0) return true;
            if (!string.IsNullOrWhiteSpace(mediaType) && mediaType.Contains("gif", StringComparison.OrdinalIgnoreCase) && data.Length < 2048)
                return true;

            try
            {
                var info = Image.Identify(data);
                if (info != null && (info.Width <= 1 || info.Height <= 1))
                    return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                // If dimensions can't be detected, keep existing behavior and allow caching.
                // We do not treat undecodable images as placeholders because some valid images
                // may not be recognized by Identify for edge codecs/content.
                _logger.LogDebug(ex, "Failed to inspect image dimensions for placeholder detection");
            }

            return false;
        }

        public void Dispose()
        {
            try
            {
                _httpClientNoRedirect.Dispose();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogWarning(ex, "Failed disposing no-redirect HttpClient in ImageCacheService");
            }

            try
            {
                _httpClient.Dispose();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogWarning(ex, "Failed disposing HttpClient in ImageCacheService");
            }
        }
    }
}

