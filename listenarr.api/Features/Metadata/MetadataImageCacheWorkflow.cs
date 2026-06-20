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


namespace Listenarr.Api.Features.Metadata
{
    internal sealed class MetadataImageCacheWorkflow
    {
        private readonly IAudiobookRepository _audiobookRepository;
        private readonly IImageCacheService _imageCacheService;
        private readonly ILogger _logger;

        public MetadataImageCacheWorkflow(
            IAudiobookRepository audiobookRepository,
            IImageCacheService imageCacheService,
            ILogger logger)
        {
            _audiobookRepository = audiobookRepository;
            _imageCacheService = imageCacheService;
            _logger = logger;
        }

        public async Task<(string? Asin, string? CachedPath)> ProbeAuthorImageCacheAsync(string normalizedName, string region, string? hintedAsin)
        {
            var candidateAsins = new List<string>();

            if (!string.IsNullOrWhiteSpace(hintedAsin))
            {
                candidateAsins.Add(hintedAsin.Trim());
            }

            try
            {
                var cachedAuthor = await _audiobookRepository.GetCachedAuthorByNameAsync(normalizedName, region);
                if (!string.IsNullOrWhiteSpace(cachedAuthor?.AuthorAsin)
                    && !candidateAsins.Any(existing => string.Equals(existing, cachedAuthor.AuthorAsin, StringComparison.OrdinalIgnoreCase)))
                {
                    candidateAsins.Add(cachedAuthor.AuthorAsin);
                }

                var storedAuthorAsin = await _audiobookRepository.GetAuthorAsinByNameAsync(normalizedName);
                if (!string.IsNullOrWhiteSpace(storedAuthorAsin)
                    && !candidateAsins.Any(existing => string.Equals(existing, storedAuthorAsin, StringComparison.OrdinalIgnoreCase)))
                {
                    candidateAsins.Add(storedAuthorAsin);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to probe DB for cached author ASIN: {Author}", normalizedName);
            }

            foreach (var candidateAsin in candidateAsins)
            {
                var cachedPath = await ResolveCachedImagePathAsync(candidateAsin);
                if (!string.IsNullOrWhiteSpace(cachedPath))
                {
                    return (candidateAsin, cachedPath);
                }
            }

            return (candidateAsins.FirstOrDefault(), null);
        }

        public async Task<string?> ResolveCachedImagePathAsync(string? asin)
        {
            if (string.IsNullOrWhiteSpace(asin)) return null;

            try
            {
                var diskPath = await _imageCacheService.GetCachedImagePathAsync(asin);
                return string.IsNullOrWhiteSpace(diskPath)
                    ? null
                    : "/" + diskPath.TrimStart('/');
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to resolve cached author image path for ASIN {Asin}", asin);
                return null;
            }
        }
    }
}
