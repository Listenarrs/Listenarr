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
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Listenarr.Infrastructure.Images.Thumbnails
{
    /// <summary>
    /// On-demand cover thumbnailer. Downscales a cached original once, stores the result under
    /// <c>config/cache/images/thumbs/&lt;size&gt;/</c>, and serves the cached copy thereafter.
    /// </summary>
    public sealed class CoverThumbnailService : ICoverThumbnailService
    {
        // Allow-list of named sizes (longest-edge pixels). Deliberately NOT arbitrary ints so a
        // caller cannot spray unbounded resize work / cache entries via the query string.
        private static readonly IReadOnlyDictionary<string, int> SizeMap =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["grid"] = 400,
                ["grid2x"] = 800,
            };

        private const int JpegQuality = 80;

        private readonly ILogger<CoverThumbnailService> _logger;
        private readonly string _thumbsRoot;
        private readonly AsyncKeyedLocker<string> _locks = new();

        public CoverThumbnailService(ILogger<CoverThumbnailService> logger, IApplicationPathService applicationPathService)
        {
            _logger = logger;
            _thumbsRoot = applicationPathService.ResolveFromConfig("cache", "images", "thumbs");
        }

        public async Task<string?> GetOrCreateThumbnailAsync(string sourceFullPath, string sizeKey, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sourceFullPath) ||
                string.IsNullOrWhiteSpace(sizeKey) ||
                !SizeMap.TryGetValue(sizeKey, out var maxEdge))
            {
                return null;
            }

            try
            {
                if (!File.Exists(sourceFullPath))
                {
                    return null;
                }

                var sizeDir = Path.Combine(_thumbsRoot, sizeKey.ToLowerInvariant());
                // Source file names are already sanitized cache names (e.g. "B00XYZ.jpg"), so reuse
                // the base name — guaranteed filesystem-safe and unique per identifier.
                var thumbPath = Path.Combine(sizeDir, Path.GetFileNameWithoutExtension(sourceFullPath) + ".jpg");

                // Fast path: a fresh thumbnail already exists.
                if (IsThumbFresh(thumbPath, sourceFullPath))
                {
                    return thumbPath;
                }

                // Serialize generation of the same thumbnail so a fresh library load does not spawn
                // N identical resizes of the same cover.
                using (await _locks.LockAsync(thumbPath, cancellationToken).ConfigureAwait(false))
                {
                    // Another request may have produced it while we waited for the lock.
                    if (IsThumbFresh(thumbPath, sourceFullPath))
                    {
                        return thumbPath;
                    }

                    Directory.CreateDirectory(sizeDir);

                    using var image = await Image.LoadAsync(sourceFullPath, cancellationToken).ConfigureAwait(false);

                    // Only ever downscale; never upscale a small source.
                    if (Math.Max(image.Width, image.Height) > maxEdge)
                    {
                        image.Mutate(x => x.Resize(new ResizeOptions
                        {
                            Mode = ResizeMode.Max,
                            Size = new Size(maxEdge, maxEdge),
                            Sampler = KnownResamplers.Lanczos3,
                        }));
                    }

                    // Write to a temp file then move, so a serve never observes a half-written thumb.
                    var tempPath = thumbPath + ".tmp";
                    await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await image.SaveAsJpegAsync(fs, new JpegEncoder { Quality = JpegQuality }, cancellationToken).ConfigureAwait(false);
                    }
                    File.Move(tempPath, thumbPath, overwrite: true);

                    return thumbPath;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to build {SizeKey} thumbnail for {File}", sizeKey, Path.GetFileName(sourceFullPath));
                return null;
            }
        }

        private static bool IsThumbFresh(string thumbPath, string sourceFullPath)
        {
            var thumb = new FileInfo(thumbPath);
            if (!thumb.Exists || thumb.Length == 0)
            {
                return false;
            }

            var source = new FileInfo(sourceFullPath);
            // Regenerate if the original was replaced (e.g. a cover refresh) after the thumb was built.
            return source.Exists && thumb.LastWriteTimeUtc >= source.LastWriteTimeUtc;
        }
    }
}
