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
    internal sealed class MetadataLookupCacheWorkflow
    {
        private readonly IAudiobookRepository _audiobookRepository;
        private readonly IImageCacheService _imageCacheService;
        private readonly MetadataImageCacheWorkflow _imageCacheWorkflow;
        private readonly ILogger _logger;

        public MetadataLookupCacheWorkflow(
            IAudiobookRepository audiobookRepository,
            IImageCacheService imageCacheService,
            MetadataImageCacheWorkflow imageCacheWorkflow,
            ILogger logger)
        {
            _audiobookRepository = audiobookRepository;
            _imageCacheService = imageCacheService;
            _imageCacheWorkflow = imageCacheWorkflow;
            _logger = logger;
        }

        public async Task<AuthorCacheEntry?> ResolvePersistedAuthorCacheAsync(string normalizedName, string region, string? normalizedAsin)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(normalizedAsin))
                {
                    var byAsin = await _audiobookRepository.GetCachedAuthorByAsinAsync(normalizedAsin, region);
                    if (byAsin != null)
                    {
                        return byAsin;
                    }
                }

                var byName = await _audiobookRepository.GetCachedAuthorByNameAsync(normalizedName, region);
                if (byName != null)
                {
                    return byName;
                }

                var storedAuthorAsin = await _audiobookRepository.GetAuthorAsinByNameAsync(normalizedName);
                if (!string.IsNullOrWhiteSpace(storedAuthorAsin))
                {
                    return await _audiobookRepository.GetCachedAuthorByAsinAsync(storedAuthorAsin, region);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to resolve persisted author cache for {Author}", normalizedName);
            }

            return null;
        }

        public async Task<MetadataController.AuthorLookupResponse> MapPersistedAuthorLookupResponseAsync(
            AuthorCacheEntry entry,
            string fallbackName)
        {
            var cachedPath = await _imageCacheWorkflow.ResolveCachedImagePathAsync(entry.AuthorAsin);
            if (string.IsNullOrWhiteSpace(cachedPath) &&
                !string.IsNullOrWhiteSpace(entry.AuthorAsin) &&
                !string.IsNullOrWhiteSpace(entry.ImageUrl))
            {
                try
                {
                    cachedPath = await _imageCacheService.MoveToAuthorLibraryStorageAsync(entry.AuthorAsin, entry.ImageUrl);
                    if (!string.IsNullOrWhiteSpace(cachedPath))
                    {
                        cachedPath = "/" + cachedPath.TrimStart('/');
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to backfill cached author image for ASIN {Asin}", entry.AuthorAsin);
                }
            }

            return new MetadataController.AuthorLookupResponse
            {
                Asin = entry.AuthorAsin,
                Name = string.IsNullOrWhiteSpace(entry.AuthorName) ? fallbackName : entry.AuthorName,
                Image = entry.ImageUrl,
                CachedPath = cachedPath,
                Description = entry.Description,
                SimilarAuthors = (entry.SimilarAuthors ?? new List<CachedRelatedAuthor>())
                    .Where(author => !string.IsNullOrWhiteSpace(author.Name))
                    .Select(author => new MetadataController.RelatedAuthorItem
                    {
                        Asin = author.Asin,
                        Name = author.Name
                    })
                    .ToList()
            };
        }

        public async Task PersistAuthorLookupAsync(
            AuthorCacheEntry? existingEntry,
            string normalizedName,
            string region,
            MetadataController.AuthorLookupResponse response)
        {
            if (string.IsNullOrWhiteSpace(response.Name))
            {
                return;
            }

            try
            {
                var entry = existingEntry ?? new AuthorCacheEntry();
                entry.AuthorName = response.Name;
                entry.AuthorNameNormalized = MetadataCacheKeys.NormalizeAuthorCacheKey(normalizedName);
                entry.AuthorAsin = response.Asin;
                entry.Region = AudiobookIdentifierNormalizer.NormalizeRegion(region) ?? "us";
                entry.ImageUrl = response.Image;
                entry.Description = response.Description;
                entry.SimilarAuthors = response.SimilarAuthors
                    .Where(author => !string.IsNullOrWhiteSpace(author.Name))
                    .Select(author => new CachedRelatedAuthor
                    {
                        Asin = author.Asin,
                        Name = author.Name
                    })
                    .ToList();
                entry.LastFetchedAt = DateTime.UtcNow;

                await _audiobookRepository.UpsertCachedAuthorAsync(entry);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to persist author cache for {Author}", normalizedName);
            }
        }

        public async Task<SeriesCacheEntry?> ResolvePersistedSeriesCacheAsync(string normalizedName, string region, string? normalizedAsin)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(normalizedAsin))
                {
                    var byAsin = await _audiobookRepository.GetCachedSeriesByAsinAsync(normalizedAsin, region);
                    if (byAsin != null)
                    {
                        return byAsin;
                    }
                }

                return await _audiobookRepository.GetCachedSeriesByNameAsync(normalizedName, region);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to resolve persisted series cache for {Series}", normalizedName);
            }

            return null;
        }

        public async Task<MetadataController.SeriesLookupResponse> MapPersistedSeriesLookupResponseAsync(
            SeriesCacheEntry entry,
            string fallbackName)
        {
            var cachedPath = await _imageCacheWorkflow.ResolveCachedImagePathAsync(entry.SeriesAsin);
            if (string.IsNullOrWhiteSpace(cachedPath) &&
                !string.IsNullOrWhiteSpace(entry.SeriesAsin) &&
                !string.IsNullOrWhiteSpace(entry.ImageUrl))
            {
                try
                {
                    cachedPath = await _imageCacheService.MoveToSeriesLibraryStorageAsync(entry.SeriesAsin, entry.ImageUrl);
                    if (!string.IsNullOrWhiteSpace(cachedPath))
                    {
                        cachedPath = "/" + cachedPath.TrimStart('/');
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to backfill cached series image for ASIN {Asin}", entry.SeriesAsin);
                }
            }

            return new MetadataController.SeriesLookupResponse
            {
                Asin = entry.SeriesAsin,
                Name = string.IsNullOrWhiteSpace(entry.SeriesName) ? fallbackName : entry.SeriesName,
                Image = entry.ImageUrl,
                CachedPath = cachedPath,
                Description = entry.Description,
                TotalBooks = entry.CatalogBooks?.Count ?? 0
            };
        }

        public async Task PersistSeriesLookupAsync(
            SeriesCacheEntry? existingEntry,
            string normalizedName,
            string region,
            MetadataController.SeriesLookupResponse response,
            IEnumerable<AudibleSearchResult>? catalogBooks = null)
        {
            if (string.IsNullOrWhiteSpace(response.Name))
            {
                return;
            }

            try
            {
                var entry = existingEntry ?? new SeriesCacheEntry();
                entry.SeriesName = response.Name;
                entry.SeriesNameNormalized = MetadataCacheKeys.NormalizeSeriesCacheKey(normalizedName);
                entry.SeriesAsin = response.Asin;
                entry.Region = AudiobookIdentifierNormalizer.NormalizeRegion(region) ?? "us";
                entry.ImageUrl = response.Image;
                entry.Description = response.Description;
                if (catalogBooks != null)
                {
                    entry.CatalogBooks = catalogBooks.Select(MapCachedSeriesCatalogBook).ToList();
                }

                entry.LastFetchedAt = DateTime.UtcNow;

                await _audiobookRepository.UpsertCachedSeriesAsync(entry);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to persist series cache for {Series}", normalizedName);
            }
        }

        private static CachedSeriesCatalogBook MapCachedSeriesCatalogBook(AudibleSearchResult book)
        {
            var primarySeries = book.Series?.FirstOrDefault();

            return new CachedSeriesCatalogBook
            {
                Asin = book.Asin,
                Title = book.Title ?? "Unknown Title",
                Subtitle = book.Subtitle,
                Authors = MapNames(book.Authors, author => author.Name),
                ImageUrl = book.ImageUrl,
                Runtime = book.LengthMinutes ?? book.RuntimeLengthMin ?? book.RuntimeMinutes,
                Language = book.Language,
                Publisher = book.Publisher,
                Narrators = MapNames(book.Narrators, narrator => narrator.Name),
                Genres = MapNames(book.Genres, genre => genre.Name),
                Series = primarySeries?.Name,
                SeriesNumber = primarySeries?.Position,
                PublishedDate = book.ReleaseDate,
                Isbn = book.Isbn,
                Link = book.Link,
                MetadataSource = "Audible"
            };
        }

        private static List<string> MapNames<T>(IEnumerable<T>? values, Func<T, string?> selector)
        {
            return (values ?? Enumerable.Empty<T>())
                .Select(selector)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .ToList();
        }
    }
}
