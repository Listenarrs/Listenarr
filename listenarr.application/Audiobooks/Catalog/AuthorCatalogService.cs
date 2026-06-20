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
using static Listenarr.Application.Audiobooks.Catalog.AuthorCatalogMapping;

namespace Listenarr.Application.Audiobooks.Catalog
{
    public class AuthorCatalogService : IAuthorCatalogService
    {
        private readonly AudibleService _audibleService;
        private readonly IAudnexusService _audnexusService;
        private readonly IAudiobookRepository _audiobookRepository;
        private readonly ISearchService _searchService;
        private readonly ILogger<AuthorCatalogService> _logger;

        public AuthorCatalogService(
            AudibleService audibleService,
            IAudnexusService audnexusService,
            IAudiobookRepository audiobookRepository,
            ISearchService searchService,
            ILogger<AuthorCatalogService> logger)
        {
            _audibleService = audibleService;
            _audnexusService = audnexusService;
            _audiobookRepository = audiobookRepository;
            _searchService = searchService;
            _logger = logger;
        }

        public async Task<AuthorCatalogFetchResult?> GetCatalogAsync(
            string name,
            string region = "us",
            int limit = 250,
            string? language = null,
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var normalizedName = name.Trim();
            var normalizedRegion = NormalizeRegion(region);
            var normalizedLanguage = NormalizeLanguage(language);
            var cachedEntry = await ResolvePersistedCacheAsync(normalizedName, normalizedRegion);

            if (!forceRefresh &&
                cachedEntry?.CatalogBooks != null &&
                cachedEntry.CatalogBooks.Count > 0)
            {
                return new AuthorCatalogFetchResult
                {
                    Author = MapCachedAuthor(cachedEntry, normalizedName, normalizedRegion),
                    Books = FilterCatalogByLanguage(
                        cachedEntry.CatalogBooks.Select(MapCachedCatalogBook),
                        normalizedLanguage)
                };
            }

            var author = await ResolveAuthorAsync(normalizedName, normalizedRegion, cachedEntry);
            if (author == null || string.IsNullOrWhiteSpace(author.Asin))
            {
                return null;
            }

            var totalLimit = Math.Clamp(limit, 1, 500);
            cancellationToken.ThrowIfCancellationRequested();
            var directCatalogResult = await _audibleService.GetAllBooksByAuthorAsync(
                normalizedName,
                author.Asin,
                totalLimit,
                normalizedRegion,
                language: null);

            var allBooks = directCatalogResult?.Results ?? new List<AudibleSearchResult>();
            var seenKeys = new HashSet<string>(
                allBooks.Select(BuildAuthorCatalogBookKey),
                StringComparer.OrdinalIgnoreCase);

            if (ShouldSupplementWithSearchFallback(allBooks.Count, totalLimit))
            {
                await SupplementWithSearchFallbackAsync(
                    normalizedName,
                    normalizedRegion,
                    null,
                    totalLimit,
                    allBooks,
                    seenKeys,
                    cancellationToken);
            }

            if (allBooks.Count == 0 &&
                cachedEntry?.CatalogBooks != null &&
                cachedEntry.CatalogBooks.Count > 0)
            {
                _logger.LogWarning(
                    "Author catalog refresh produced no books for {Author}; keeping persisted catalog cache",
                    normalizedName);

                return new AuthorCatalogFetchResult
                {
                    Author = MapCachedAuthor(cachedEntry, normalizedName, normalizedRegion),
                    Books = FilterCatalogByLanguage(
                        cachedEntry.CatalogBooks.Select(MapCachedCatalogBook),
                        normalizedLanguage)
                };
            }

            await PersistCatalogAsync(
                cachedEntry,
                normalizedName,
                normalizedRegion,
                author,
                allBooks,
                cancellationToken);

            return new AuthorCatalogFetchResult
            {
                Author = author,
                Books = FilterCatalogByLanguage(allBooks, normalizedLanguage)
            };
        }

        private async Task<AuthorLookupItem?> ResolveAuthorAsync(
            string normalizedName,
            string region,
            AuthorCacheEntry? cachedEntry)
        {
            if (cachedEntry != null && !string.IsNullOrWhiteSpace(cachedEntry.AuthorAsin))
            {
                return MapCachedAuthor(cachedEntry, normalizedName, region);
            }

            var author = await _audibleService.LookupAuthorAsync(normalizedName, region);
            if (!string.IsNullOrWhiteSpace(author?.Asin))
            {
                return author;
            }

            try
            {
                var authorAsin = await _audiobookRepository.GetAuthorAsinByNameAsync(normalizedName);
                if (!string.IsNullOrWhiteSpace(authorAsin))
                {
                    var cachedByAsin = await _audiobookRepository.GetCachedAuthorByAsinAsync(authorAsin, region);
                    if (cachedByAsin != null)
                    {
                        return MapCachedAuthor(cachedByAsin, normalizedName, region);
                    }

                    return new AuthorLookupItem
                    {
                        Asin = authorAsin,
                        Name = author?.Name ?? normalizedName,
                        Image = author?.Image,
                        Region = region
                    };
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to resolve cached author ASIN for {Author}", normalizedName);
            }

            try
            {
                var audnexResults = await _audnexusService.SearchAuthorsAsync(normalizedName, region);
                var audnexAuthor = audnexResults?.FirstOrDefault(a =>
                    !string.IsNullOrWhiteSpace(a.Name) &&
                    a.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
                    ?? audnexResults?.FirstOrDefault();

                if (audnexAuthor != null)
                {
                    return new AuthorLookupItem
                    {
                        Asin = audnexAuthor.Asin,
                        Name = audnexAuthor.Name ?? normalizedName,
                        Image = audnexAuthor.Image,
                        Region = region
                    };
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Audnexus author fallback failed for '{Author}'", normalizedName);
            }

            return author;
        }

        private async Task<AuthorCacheEntry?> ResolvePersistedCacheAsync(string normalizedName, string region)
        {
            try
            {
                var cachedByName = await _audiobookRepository.GetCachedAuthorByNameAsync(normalizedName, region);
                if (cachedByName != null)
                {
                    return cachedByName;
                }

                var authorAsin = await _audiobookRepository.GetAuthorAsinByNameAsync(normalizedName);
                if (!string.IsNullOrWhiteSpace(authorAsin))
                {
                    return await _audiobookRepository.GetCachedAuthorByAsinAsync(authorAsin, region);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to resolve persisted author catalog cache for {Author}", normalizedName);
            }

            return null;
        }

        private async Task SupplementWithSearchFallbackAsync(
            string authorName,
            string region,
            string? language,
            int totalLimit,
            List<AudibleSearchResult> allBooks,
            HashSet<string> seenKeys,
            CancellationToken cancellationToken)
        {
            try
            {
                var remaining = totalLimit - allBooks.Count;
                if (remaining <= 0)
                {
                    return;
                }

                _logger.LogInformation(
                    "Author catalog fallback search triggered for {Author}. Current catalog count: {Count}",
                    authorName,
                    allBooks.Count);

                var searchResults = await _searchService.IntelligentSearchAsync(
                    authorName,
                    candidateLimit: Math.Clamp(totalLimit * 2, 25, 200),
                    returnLimit: Math.Clamp(totalLimit * 2, 25, 200),
                    region: region,
                    language: language,
                    ct: cancellationToken);

                foreach (var result in searchResults)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!MatchesAuthor(result, authorName))
                    {
                        continue;
                    }

                    var mapped = MapFallbackSearchResult(result);
                    var key = BuildAuthorCatalogBookKey(mapped);
                    if (!seenKeys.Add(key))
                    {
                        continue;
                    }

                    allBooks.Add(mapped);
                    if (allBooks.Count >= totalLimit)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Author catalog fallback search failed for {Author}", authorName);
            }
        }

        private async Task PersistCatalogAsync(
            AuthorCacheEntry? cachedEntry,
            string authorName,
            string region,
            AuthorLookupItem author,
            List<AudibleSearchResult> books,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var entry = cachedEntry ?? new AuthorCacheEntry();
                entry.AuthorName = string.IsNullOrWhiteSpace(author.Name) ? authorName : author.Name;
                entry.AuthorNameNormalized = NormalizeAuthorCacheKey(authorName);
                entry.AuthorAsin = author.Asin;
                entry.Region = region;
                entry.ImageUrl = author.Image ?? entry.ImageUrl;
                entry.Description ??= author.Description;
                entry.CatalogBooks = books.Select(MapCachedCatalogBook).ToList();
                entry.LastFetchedAt = DateTime.UtcNow;

                await _audiobookRepository.UpsertCachedAuthorAsync(entry);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to persist author catalog cache for {Author}", authorName);
            }
        }

    }
}
