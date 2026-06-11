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

using Listenarr.Application.Interfaces;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;

namespace Listenarr.Infrastructure.Services
{
    public class ImageSharpCoverImageProbe : ICoverImageProbe
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<ImageSharpCoverImageProbe> _logger;

        public ImageSharpCoverImageProbe(HttpClient httpClient, ILogger<ImageSharpCoverImageProbe> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<ImageDimensions?> ProbeAsync(string url, CancellationToken cancellationToken = default)
        {
            try
            {
                using var resp = await _httpClient.GetAsync(url, cancellationToken);
                if (!resp.IsSuccessStatusCode)
                    return null;

                using var ms = new MemoryStream(await resp.Content.ReadAsByteArrayAsync(cancellationToken));
                using var img = Image.Load(ms);
                return img.Height == 0 ? null : new ImageDimensions(img.Width, img.Height);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to measure image dimensions for cover {Url}", url);
                return null;
            }
        }
    }
}
