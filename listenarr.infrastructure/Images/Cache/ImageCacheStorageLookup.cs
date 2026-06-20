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
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Images.Cache
{
    internal sealed class ImageCacheStorageLookup
    {
        private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".gif", ".svg"];

        private readonly ImageCachePathResolver _pathResolver;
        private readonly ILogger _logger;
        private readonly string _libraryImagePath;
        private readonly string _authorImagePath;
        private readonly string _seriesImagePath;
        private readonly string _tempCachePath;

        public ImageCacheStorageLookup(
            ImageCachePathResolver pathResolver,
            ILogger logger,
            string libraryImagePath,
            string authorImagePath,
            string seriesImagePath,
            string tempCachePath)
        {
            _pathResolver = pathResolver;
            _logger = logger;
            _libraryImagePath = libraryImagePath;
            _authorImagePath = authorImagePath;
            _seriesImagePath = seriesImagePath;
            _tempCachePath = tempCachePath;
        }

        public string? FindLibraryPath(string identifier)
        {
            return GetValidPath(identifier, _libraryImagePath, "library");
        }

        public string? FindAuthorPath(string identifier)
        {
            return GetValidPath(identifier, _authorImagePath, "author");
        }

        public string? FindSeriesPath(string identifier)
        {
            return GetValidPath(identifier, _seriesImagePath, "series");
        }

        public string? FindTempPath(string identifier)
        {
            foreach (var ext in ImageExtensions)
            {
                var path = _pathResolver.BuildFilePath(identifier, ext, _tempCachePath);
                if (!File.Exists(path)) continue;

                if (!IsValidCachedCoverFile(path, identifier, "temp"))
                {
                    continue;
                }

                return path;
            }

            return null;
        }

        public string? FindAnyCachedPath(string identifier)
        {
            return FindLibraryPath(identifier)
                ?? FindAuthorPath(identifier)
                ?? FindSeriesPath(identifier)
                ?? FindTempPath(identifier);
        }

        private string? GetValidPath(string identifier, string basePath, string bucket)
        {
            var path = _pathResolver.GetImagePath(identifier, basePath);
            return File.Exists(path) && IsValidCachedCoverFile(path, identifier, bucket)
                ? path
                : null;
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
                        var root = bucket switch
                        {
                            "library" => _libraryImagePath,
                            "author" => _authorImagePath,
                            "series" => _seriesImagePath,
                            _ => _tempCachePath
                        };

                        if (FileSystemSafety.TryValidateMutationTarget(filePath, [root], out var safePath, out var reason))
                        {
                            File.Delete(safePath);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Blocked cached image delete for {Identifier} in {Bucket}: {Reason}",
                                LogRedaction.SanitizeText(identifier),
                                bucket,
                                LogRedaction.SanitizeText(reason));
                        }
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
    }
}
