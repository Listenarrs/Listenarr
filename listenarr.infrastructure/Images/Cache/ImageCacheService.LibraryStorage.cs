/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Images.Cache
{
    public partial class ImageCacheService : IImageCacheService, IDisposable
    {
        /// <summary>
        /// Moves an image from temp cache to permanent library storage.
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
                            if (!FileSystemSafety.TryValidateMutationTarget(file, [_tempCachePath], out var safeFile, out var reason))
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

            if (!FileSystemSafety.TryValidateMutationTarget(sourcePath, [sourceRoot], out safeSourcePath, out var sourceReason))
            {
                _logger.LogWarning("Blocked cached image move for {Identifier}: source invalid: {Reason}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(sourceReason));
                return false;
            }

            if (!FileSystemSafety.TryValidateMutationTarget(destinationPath, [destinationRoot], out safeDestinationPath, out var destinationReason))
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
