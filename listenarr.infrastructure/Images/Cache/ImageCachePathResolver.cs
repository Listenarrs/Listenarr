/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Images.Cache
{
    internal sealed class ImageCachePathResolver
    {
        private readonly string _contentRootPath;

        public ImageCachePathResolver(string contentRootPath)
        {
            _contentRootPath = contentRootPath;
        }

        public string GetImagePath(string identifier, string basePath)
        {
            var sanitized = SanitizeFileName(identifier);
            var extensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".svg" };

            foreach (var ext in extensions)
            {
                var path = FileUtils.CombineRelativePath(basePath, NormalizeRelativeFileName(sanitized + ext));
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return FileUtils.CombineRelativePath(basePath, NormalizeRelativeFileName(sanitized + ".jpg"));
        }

        public string GetRelativePath(string fullPath)
        {
            return Path.GetRelativePath(_contentRootPath, fullPath).Replace('\\', '/');
        }

        public string BuildTempFilePath(string identifier, string extension, string tempCachePath)
        {
            return BuildFilePath(identifier, extension, tempCachePath);
        }

        public string BuildFilePath(string identifier, string extension, string basePath)
        {
            var fileName = NormalizeRelativeFileName($"{SanitizeFileName(identifier)}{extension}");
            return FileUtils.CombineRelativePath(basePath, fileName);
        }

        private static string SanitizeFileName(string fileName)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            return string.Join("_", fileName.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries)).Trim();
        }

        private static string NormalizeRelativeFileName(string fileName)
        {
            var normalized = Path.GetFileName(fileName);
            return normalized.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
