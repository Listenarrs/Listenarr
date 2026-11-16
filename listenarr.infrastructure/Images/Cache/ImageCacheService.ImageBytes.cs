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


using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Images.Cache
{
    public partial class ImageCacheService
    {
        public async Task<byte[]?> GetImageBytesAsync(string imageReference)
        {
            if (string.IsNullOrWhiteSpace(imageReference))
            {
                return null;
            }

            try
            {
                var relativePath = imageReference;
                if (Uri.TryCreate(imageReference, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    // Stable per URL, so repeated imports of the same cover reuse the cached file.
                    var identifier = "cover-" + Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(imageReference)))[..16];
                    var cached = await DownloadAndCacheImageAsync(imageReference, identifier);
                    if (string.IsNullOrWhiteSpace(cached))
                    {
                        return null;
                    }

                    relativePath = cached;
                }

                return await ReadCachedImageAsync(relativePath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to read image bytes for {Reference}", LogRedaction.SanitizeText(imageReference));
                return null;
            }
        }

        private async Task<byte[]?> ReadCachedImageAsync(string relativePath)
        {
            if (Path.IsPathRooted(relativePath))
            {
                return null;
            }

            var fullPath = Path.GetFullPath(Path.Join(_contentRootPath, relativePath));
            var imageRoots = new[] { _tempCachePath, _libraryImagePath, _authorImagePath, _seriesImagePath };
            if (!imageRoots.Any(root => IsInsideDirectory(fullPath, root)))
            {
                _logger.LogWarning("Refused to read image outside the image cache: {Path}", LogRedaction.SanitizeFilePath(relativePath));
                return null;
            }

            var file = new FileInfo(fullPath);
            if (!file.Exists || file.Length > MaxDownloadedImageBytes)
            {
                return null;
            }

            return await File.ReadAllBytesAsync(fullPath);
        }

        private static bool IsInsideDirectory(string fullPath, string directory)
        {
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(root, StringComparison.Ordinal);
        }
    }
}
