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
    public class SeriesCatalogService : ISeriesCatalogService
    {
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
        private readonly IAudiobookRepository _audiobookRepository;
        private readonly ILogger<SeriesCatalogService> _logger;

        public SeriesCatalogService(
            AudibleService audibleService,
            IAudiobookRepository audiobookRepository,
            ILogger<SeriesCatalogService> logger)
        {
            _audibleService = audibleService;
            _audiobookRepository = audiobookRepository;
            _logger = logger;
        }

        public async Task<SeriesCatalogFetchResult?> GetCatalogAsync(
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
                return new SeriesCatalogFetchResult
                {
                    Series = MapCachedSeries(cachedEntry, normalizedName, normalizedRegion),
                    Books = FilterCatalogByLanguage(
                        cachedEntry.CatalogBooks.Select(MapCachedCatalogBook),
                        normalizedLanguage)
                };
            }

            var series = await ResolveSeriesAsync(normalizedName, normalizedRegion, cachedEntry);
            if (series == null || string.IsNullOrWhiteSpace(series.Asin))
            {
                return null;
            }

            var books = await _audibleService.GetTypedBooksBySeriesAsinAsync(series.Asin, normalizedRegion)
                ?? new List<AudibleSearchResult>();

            var limitedBooks = books
                .Where(book => book != null)
                .DistinctBy(BuildSeriesCatalogBookKey)
                .Take(Math.Clamp(limit, 1, 500))
                .ToList();

            if (limitedBooks.Count == 0 &&
                cachedEntry?.CatalogBooks != null &&
                cachedEntry.CatalogBooks.Count > 0)
            {
                _logger.LogWarning(
                    "Series catalog refresh produced no books for {Series}; keeping persisted catalog cache",
                    normalizedName);

                return new SeriesCatalogFetchResult
                {
                    Series = MapCachedSeries(cachedEntry, normalizedName, normalizedRegion),
                    Books = FilterCatalogByLanguage(
                        cachedEntry.CatalogBooks.Select(MapCachedCatalogBook),
                        normalizedLanguage)
                };
            }

            if (string.IsNullOrWhiteSpace(series.Image))
            {
                series.Image = limitedBooks.FirstOrDefault(book => !string.IsNullOrWhiteSpace(book.ImageUrl))?.ImageUrl;
            }

            await PersistCatalogAsync(
                cachedEntry,
                normalizedName,
                normalizedRegion,
                series,
                limitedBooks,
                cancellationToken);

            return new SeriesCatalogFetchResult
            {
                Series = series,
                Books = FilterCatalogByLanguage(limitedBooks, normalizedLanguage)
            };
        }

        private async Task<SeriesLookupItem?> ResolveSeriesAsync(
            string normalizedName,
            string region,
            SeriesCacheEntry? cachedEntry)
        {
            if (cachedEntry != null && !string.IsNullOrWhiteSpace(cachedEntry.SeriesAsin))
            {
                return MapCachedSeries(cachedEntry, normalizedName, region);
            }

            var series = await _audibleService.LookupSeriesAsync(normalizedName, region);
            if (!string.IsNullOrWhiteSpace(series?.Asin))
            {
                return series;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(series?.Asin))
                {
                    var cachedByAsin = await _audiobookRepository.GetCachedSeriesByAsinAsync(series.Asin, region);
                    if (cachedByAsin != null)
                    {
                        return MapCachedSeries(cachedByAsin, normalizedName, region);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to resolve cached series ASIN for {Series}", normalizedName);
            }

            return series;
        }

        private async Task<SeriesCacheEntry?> ResolvePersistedCacheAsync(string normalizedName, string region)
        {
            try
            {
                return await _audiobookRepository.GetCachedSeriesByNameAsync(normalizedName, region);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to resolve persisted series catalog cache for {Series}", normalizedName);
            }

            return null;
        }

        private async Task PersistCatalogAsync(
            SeriesCacheEntry? cachedEntry,
            string seriesName,
            string region,
            SeriesLookupItem series,
            List<AudibleSearchResult> books,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var entry = cachedEntry ?? new SeriesCacheEntry();
                entry.SeriesName = string.IsNullOrWhiteSpace(series.Name) ? seriesName : series.Name;
                entry.SeriesNameNormalized = NormalizeSeriesCacheKey(seriesName);
                entry.SeriesAsin = series.Asin;
                entry.Region = region;
                entry.ImageUrl = series.Image ?? books.FirstOrDefault(book => !string.IsNullOrWhiteSpace(book.ImageUrl))?.ImageUrl ?? entry.ImageUrl;
                entry.Description = series.Description ?? entry.Description;
                entry.CatalogBooks = books.Select(MapCachedCatalogBook).ToList();
                entry.LastFetchedAt = DateTime.UtcNow;

                await _audiobookRepository.UpsertCachedSeriesAsync(entry);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to persist series catalog cache for {Series}", seriesName);
            }
        }

        private static string BuildSeriesCatalogBookKey(AudibleSearchResult book)
        {
            if (!string.IsNullOrWhiteSpace(book.Asin))
            {
                return $"asin:{NormalizeCatalogToken(book.Asin)}";
            }

            var title = NormalizeCatalogToken(book.Title);
            var authors = string.Join("|", (book.Authors ?? new List<AudibleAuthor>())
                .Select(author => NormalizeCatalogToken(author.Name))
                .Where(author => !string.IsNullOrWhiteSpace(author)));

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

        private static string NormalizeRegion(string? region)
        {
            var normalized = AudiobookIdentifierNormalizer.NormalizeRegion(region);
            return string.IsNullOrWhiteSpace(normalized) ? "us" : normalized;
        }

        private static string? NormalizeLanguage(string? language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return null;
            }

            var trimmed = language.Trim();
            return LanguageAliases.TryGetValue(trimmed, out var normalized)
                ? normalized
                : trimmed.ToLowerInvariant();
        }

        private static List<AudibleSearchResult> FilterCatalogByLanguage(
            IEnumerable<AudibleSearchResult> books,
            string? normalizedLanguage)
        {
            var bookList = books.ToList();
            if (string.IsNullOrWhiteSpace(normalizedLanguage) ||
                string.Equals(normalizedLanguage, "all", StringComparison.OrdinalIgnoreCase))
            {
                return bookList;
            }

            return bookList
                .Where(book =>
                {
                    var bookLanguage = NormalizeLanguage(book.Language);
                    return !string.IsNullOrWhiteSpace(bookLanguage) &&
                        string.Equals(bookLanguage, normalizedLanguage, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
        }

        private static SeriesLookupItem MapCachedSeries(SeriesCacheEntry entry, string fallbackName, string region)
        {
            return new SeriesLookupItem
            {
                Asin = entry.SeriesAsin,
                Name = string.IsNullOrWhiteSpace(entry.SeriesName) ? fallbackName : entry.SeriesName,
                Image = entry.ImageUrl,
                Region = region,
                Description = entry.Description
            };
        }

        private static CachedSeriesCatalogBook MapCachedCatalogBook(AudibleSearchResult book)
        {
            var primarySeries = book.Series?.FirstOrDefault();
            var runtime = book.LengthMinutes ?? book.RuntimeLengthMin ?? book.RuntimeMinutes;

            return new CachedSeriesCatalogBook
            {
                Asin = book.Asin,
                Title = book.Title ?? "Unknown Title",
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

        private static AudibleSearchResult MapCachedCatalogBook(CachedSeriesCatalogBook book)
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

        private static string NormalizeSeriesCacheKey(string? value)
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
    }
}
