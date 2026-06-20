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

namespace Listenarr.Application.Audiobooks.Catalog
{
    internal static class AuthorCatalogMapping
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

        public static string BuildAuthorCatalogBookKey(AudibleSearchResult book)
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

        public static bool ShouldSupplementWithSearchFallback(int currentCount, int totalLimit)
        {
            if (currentCount == 0)
            {
                return true;
            }

            return currentCount < Math.Min(3, totalLimit);
        }

        public static bool MatchesAuthor(MetadataSearchResult result, string authorName)
        {
            var target = NormalizeAuthorMatchToken(authorName);
            if (string.IsNullOrWhiteSpace(target))
            {
                return false;
            }

            return ExpandAuthorCandidates(result)
                .Any(candidate => NormalizeAuthorMatchToken(candidate) == target);
        }

        public static AudibleSearchResult MapFallbackSearchResult(MetadataSearchResult result)
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

        public static AuthorLookupItem MapCachedAuthor(AuthorCacheEntry entry, string fallbackName, string region)
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

        public static AudibleSearchResult MapCachedCatalogBook(CachedAuthorCatalogBook book)
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

        public static CachedAuthorCatalogBook MapCachedCatalogBook(AudibleSearchResult book)
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

        public static List<AudibleSearchResult> FilterCatalogByLanguage(
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

        public static string NormalizeAuthorCacheKey(string? value)
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

        public static string NormalizeRegion(string? region)
        {
            return AudiobookIdentifierNormalizer.NormalizeRegion(region) ?? "us";
        }

        public static string? NormalizeLanguage(string? language)
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

        private static string NormalizeCatalogToken(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return new string(value.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
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
    }
}
