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
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Application.Metadata;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks
{
    public class AuthorCatalogService : IAuthorCatalogService
    {
        private static readonly char[] AuthorCandidateSeparators = [',', ';', '&'];
        private static readonly Dictionary<string, string> LanguageAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["english"] = "english",
            ["en"] = "english",
            ["eng"] = "english",
            ["en-us"] = "english",
            ["en-gb"] = "english",
            ["spanish"] = "spanish",
            ["es"] = "spanish",
            ["spa"] = "spanish",
            ["es-es"] = "spanish",
            ["german"] = "german",
            ["de"] = "german",
            ["deu"] = "german",
            ["ger"] = "german",
            ["de-de"] = "german",
            ["hungarian"] = "hungarian",
            ["hu"] = "hungarian",
            ["hun"] = "hungarian",
            ["french"] = "french",
            ["fr"] = "french",
            ["fra"] = "french",
            ["fre"] = "french",
            ["fr-fr"] = "french",
            ["polish"] = "polish",
            ["pl"] = "polish",
            ["pol"] = "polish",
            ["pl-pl"] = "polish",
            ["italian"] = "italian",
            ["it"] = "italian",
            ["ita"] = "italian",
            ["it-it"] = "italian",
            ["russian"] = "russian",
            ["ru"] = "russian",
            ["rus"] = "russian",
            ["ru-ru"] = "russian",
            ["all"] = "all"
        };

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

        private static string BuildAuthorCatalogBookKey(AudibleSearchResult book)
        {
            if (!string.IsNullOrWhiteSpace(book.Asin))
            {
                return $"asin:{NormalizeCatalogToken(book.Asin)}";
            }

            var title = NormalizeCatalogToken(book.Title);
            var authors = string.Join("|", (book.Authors ?? new List<AudibleAuthor>())
                .Select(a => NormalizeCatalogToken(a.Name))
                .Where(a => !string.IsNullOrWhiteSpace(a)));

            return $"title:{title}:authors:{authors}";
        }

        private static string NormalizeCatalogToken(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return new string(value.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
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

        private static bool ShouldSupplementWithSearchFallback(int currentCount, int totalLimit)
        {
            if (currentCount == 0)
            {
                return true;
            }

            return currentCount < Math.Min(3, totalLimit);
        }

        private static bool MatchesAuthor(MetadataSearchResult result, string authorName)
        {
            var target = NormalizeAuthorMatchToken(authorName);
            if (string.IsNullOrWhiteSpace(target))
            {
                return false;
            }

            return ExpandAuthorCandidates(result)
                .Any(candidate => NormalizeAuthorMatchToken(candidate) == target);
        }

        private static IEnumerable<string> ExpandAuthorCandidates(MetadataSearchResult result)
        {
            var values = new[]
            {
                result.Author,
                result.Artist
            };

            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                foreach (var trimmed in value.Split(
                             AuthorCandidateSeparators,
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    yield return trimmed;
                }
            }
        }

        private static string NormalizeAuthorMatchToken(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return new string(value
                .Trim()
                .ToUpperInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray());
        }

        private static AudibleSearchResult MapFallbackSearchResult(MetadataSearchResult result)
        {
            var authors = ExpandAuthorCandidates(result)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(author => new AudibleAuthor { Name = author })
                .ToList();

            var narrators = string.IsNullOrWhiteSpace(result.Narrator)
                ? new List<AudibleNarrator>()
                : new List<AudibleNarrator> { new() { Name = result.Narrator.Trim() } };

            var genres = (result.Genres ?? new List<string>())
                .Where(genre => !string.IsNullOrWhiteSpace(genre))
                .Select(genre => new AudibleGenre { Name = genre })
                .ToList();

            var series = string.IsNullOrWhiteSpace(result.Series)
                ? null
                : new List<AudibleSeries>
                {
                    new()
                    {
                        Name = result.Series,
                        Position = result.SeriesNumber
                    }
                };

            return new AudibleSearchResult
            {
                Asin = result.Asin,
                Title = result.Title,
                Subtitle = result.Subtitle,
                Authors = authors,
                ImageUrl = result.ImageUrl,
                Language = result.Language,
                Publisher = result.Publisher,
                Narrators = narrators,
                Genres = genres,
                Series = series,
                ReleaseDate = result.PublishedDate,
                Link = result.ProductUrl ?? result.SourceLink,
                Isbn = result.Isbn.FirstOrDefault()
            };
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

        private static AuthorLookupItem MapCachedAuthor(AuthorCacheEntry entry, string fallbackName, string region)
        {
            return new AuthorLookupItem
            {
                Asin = entry.AuthorAsin,
                Name = string.IsNullOrWhiteSpace(entry.AuthorName) ? fallbackName : entry.AuthorName,
                Image = entry.ImageUrl,
                Description = entry.Description,
                Region = region
            };
        }

        private static AudibleSearchResult MapCachedCatalogBook(CachedAuthorCatalogBook book)
        {
            return new AudibleSearchResult
            {
                Asin = book.Asin,
                Title = book.Title,
                Subtitle = book.Subtitle,
                Authors = (book.Authors ?? new List<string>())
                    .Where(author => !string.IsNullOrWhiteSpace(author))
                    .Select(author => new AudibleAuthor { Name = author })
                    .ToList(),
                ImageUrl = book.ImageUrl,
                LengthMinutes = book.Runtime,
                RuntimeLengthMin = book.Runtime,
                Language = book.Language,
                Publisher = book.Publisher,
                Narrators = (book.Narrators ?? new List<string>())
                    .Where(narrator => !string.IsNullOrWhiteSpace(narrator))
                    .Select(narrator => new AudibleNarrator { Name = narrator })
                    .ToList(),
                Genres = (book.Genres ?? new List<string>())
                    .Where(genre => !string.IsNullOrWhiteSpace(genre))
                    .Select(genre => new AudibleGenre { Name = genre })
                    .ToList(),
                Series = string.IsNullOrWhiteSpace(book.Series)
                    ? null
                    : new List<AudibleSeries>
                    {
                        new()
                        {
                            Name = book.Series,
                            Position = book.SeriesNumber
                        }
                    },
                ReleaseDate = book.PublishedDate,
                Isbn = book.Isbn,
                Link = book.Link
            };
        }

        private static CachedAuthorCatalogBook MapCachedCatalogBook(AudibleSearchResult book)
        {
            var primarySeries = book.Series?.FirstOrDefault();
            var runtime = book.LengthMinutes ?? book.RuntimeLengthMin ?? book.RuntimeMinutes;

            return new CachedAuthorCatalogBook
            {
                Asin = book.Asin,
                Title = book.Title ?? string.Empty,
                Subtitle = book.Subtitle,
                Authors = (book.Authors ?? new List<AudibleAuthor>())
                    .Select(author => author.Name)
                    .Where(author => !string.IsNullOrWhiteSpace(author))
                    .Cast<string>()
                    .ToList(),
                ImageUrl = book.ImageUrl,
                Runtime = runtime,
                Language = book.Language,
                Publisher = book.Publisher,
                Narrators = (book.Narrators ?? new List<AudibleNarrator>())
                    .Select(narrator => narrator.Name)
                    .Where(narrator => !string.IsNullOrWhiteSpace(narrator))
                    .Cast<string>()
                    .ToList(),
                Genres = (book.Genres ?? new List<AudibleGenre>())
                    .Select(genre => genre.Name)
                    .Where(genre => !string.IsNullOrWhiteSpace(genre))
                    .Cast<string>()
                    .ToList(),
                Series = primarySeries?.Name,
                SeriesNumber = primarySeries?.Position,
                PublishedDate = book.ReleaseDate,
                Isbn = book.Isbn,
                Link = book.Link,
                MetadataSource = "Audible"
            };
        }

        private static List<AudibleSearchResult> FilterCatalogByLanguage(
            IEnumerable<AudibleSearchResult> books,
            string? preferredLanguage)
        {
            var materialized = books.ToList();
            if (string.IsNullOrWhiteSpace(preferredLanguage))
            {
                return materialized;
            }

            return materialized
                .Where(book => string.Equals(
                    NormalizeLanguage(book.Language),
                    preferredLanguage,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private static string NormalizeAuthorCacheKey(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var cleaned = new string(value
                .Where(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character))
                .ToArray());
            var parts = cleaned.Split(
                new[] { ' ', '\t', '\n', '\r' },
                StringSplitOptions.RemoveEmptyEntries);

            return string.Join(' ', parts).ToLowerInvariant();
        }

        private static string NormalizeRegion(string? region)
        {
            return AudiobookIdentifierNormalizer.NormalizeRegion(region) ?? "us";
        }

        private static string? NormalizeLanguage(string? language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return null;
            }

            var normalized = language.Trim().ToLowerInvariant();
            if (normalized == "all")
            {
                return null;
            }

            return LanguageAliases.TryGetValue(normalized, out var alias)
                ? alias
                : normalized;
        }
    }
}
