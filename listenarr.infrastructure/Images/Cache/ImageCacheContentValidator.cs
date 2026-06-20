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
using SixLabors.ImageSharp;

namespace Listenarr.Infrastructure.Images.Cache
{
    internal static class ImageCacheContentValidator
    {
        private static readonly HashSet<string> AllowedDownloadedImageMediaTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "image/jpeg",
            "image/png",
            "image/webp",
            "image/gif",
        };

        private static readonly HashSet<string> AllowedDownloadedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg",
            ".jpeg",
            ".png",
            ".webp",
            ".gif",
        };

        private static readonly IReadOnlyDictionary<string, string> ImageExtensionsByMediaType =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["image/jpeg"] = ".jpg",
                ["image/png"] = ".png",
                ["image/webp"] = ".webp",
                ["image/gif"] = ".gif",
            };

        public static bool IsAllowedDownloadedImageContent(string? mediaType, Uri finalUri)
        {
            if (!string.IsNullOrWhiteSpace(mediaType))
            {
                return AllowedDownloadedImageMediaTypes.Contains(mediaType.Trim());
            }

            var extension = GetUrlPathExtension(finalUri.ToString());
            return AllowedDownloadedImageExtensions.Contains(extension);
        }

        public static string GetImageExtension(string url, string? contentType)
        {
            if (!string.IsNullOrEmpty(contentType))
            {
                if (ImageExtensionsByMediaType.TryGetValue(contentType, out var mappedExtension))
                {
                    return mappedExtension;
                }
            }

            var urlExtension = GetUrlPathExtension(url);
            if (AllowedDownloadedImageExtensions.Contains(urlExtension))
            {
                return urlExtension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : urlExtension.ToLowerInvariant();
            }

            return ".jpg";
        }

        public static string? GetMediaTypeFromExtension(string ext)
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

        public static bool IsPlaceholderImage(byte[] data, string? mediaType, ILogger logger)
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
                logger.LogDebug(ex, "Failed to inspect image dimensions for placeholder detection");
            }

            return false;
        }

        private static string GetUrlPathExtension(string url)
        {
            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    return Path.GetExtension(uri.AbsolutePath) ?? string.Empty;
                }
            }
            catch (ArgumentException)
            {
                // Fall back to path parsing below.
            }

            return Path.GetExtension(url.Split('?', '#')[0]) ?? string.Empty;
        }
    }
}
