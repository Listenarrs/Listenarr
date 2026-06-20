/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using System.Globalization;
using System.Text;

namespace Listenarr.Application.Audiobooks.Monitoring
{
    public partial class AuthorMonitoringService : IAuthorMonitoringService
    {
        private static AudibleBookMetadata MapToMetadata(AudibleSearchResult book)
        {
            var primarySeries = book.Series?.FirstOrDefault();
            var runtime = book.LengthMinutes ?? book.RuntimeLengthMin ?? book.RuntimeMinutes;

            return new AudibleBookMetadata
            {
                Asin = book.Asin,
                Title = book.Title,
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
                PublishYear = TryExtractPublishYear(book.ReleaseDate),
                Isbn = string.IsNullOrWhiteSpace(book.Isbn) ? new List<string>() : new List<string> { book.Isbn },
                Source = "Audible"
            };
        }

        private static Audiobook? FindExistingLibraryMatch(
            AudibleSearchResult book,
            IEnumerable<Audiobook> libraryBooks)
        {
            var asin = NormalizeIdentifier(book.Asin);
            if (!string.IsNullOrWhiteSpace(asin))
            {
                var asinMatch = libraryBooks.FirstOrDefault(candidate =>
                    NormalizeIdentifier(candidate.Asin) == asin);
                if (asinMatch != null)
                {
                    return asinMatch;
                }
            }

            var isbn = NormalizeIdentifier(book.Isbn);
            if (!string.IsNullOrWhiteSpace(isbn))
            {
                var isbnMatch = libraryBooks.FirstOrDefault(candidate =>
                    (candidate.Isbn ?? new List<string>()).Any(value => NormalizeIdentifier(value) == isbn));
                if (isbnMatch != null)
                {
                    return isbnMatch;
                }
            }

            var titleAuthorKey = BuildTitleAuthorKey(
                book.Title,
                (book.Authors ?? new List<AudibleAuthor>())
                    .Select(author => author.Name)
                    .Where(author => !string.IsNullOrWhiteSpace(author))
                    .Cast<string>()
                    .ToList());

            if (string.IsNullOrWhiteSpace(titleAuthorKey))
            {
                return null;
            }

            return libraryBooks.FirstOrDefault(candidate =>
                BuildTitleAuthorKey(candidate.Title, candidate.Authors) == titleAuthorKey);
        }

        private static bool ShouldIncludeBookForLanguage(AudibleSearchResult book, string preferredLanguage)
        {
            if (string.Equals(preferredLanguage, "all", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var normalizedBookLanguage = NormalizeLanguage(book.Language, fallbackToEnglish: false);
            return string.Equals(normalizedBookLanguage, preferredLanguage, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeAuthorName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var decomposed = name.Trim().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (var character in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(char.ToLowerInvariant(character));
                }
                else if (char.IsWhiteSpace(character))
                {
                    builder.Append(' ');
                }
            }

            return string.Join(
                ' ',
                builder.ToString()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        private static string NormalizeRegion(string? region)
        {
            return AudiobookIdentifierNormalizer.NormalizeRegion(region) ?? "us";
        }

        private static string NormalizeLanguage(string? language, bool fallbackToEnglish)
        {
            var normalized = (language ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return fallbackToEnglish ? "english" : string.Empty;
            }

            if (LanguageAliases.TryGetValue(normalized, out var alias))
            {
                return alias;
            }

            if (SupportedLanguages.Contains(normalized))
            {
                return normalized;
            }

            return fallbackToEnglish ? "english" : string.Empty;
        }

        private static string? NormalizeOptionalIdentifier(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : new string(value.Trim().Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        }

        private static string NormalizeIdentifier(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        }

        private static string BuildTitleAuthorKey(string? title, IEnumerable<string>? authors)
        {
            var normalizedTitle = NormalizeAuthorName(title);
            var normalizedAuthors = string.Join(
                "|",
                (authors ?? Enumerable.Empty<string>())
                    .Select(NormalizeAuthorName)
                    .Where(author => !string.IsNullOrWhiteSpace(author))
                    .OrderBy(author => author));

            return string.IsNullOrWhiteSpace(normalizedTitle) && string.IsNullOrWhiteSpace(normalizedAuthors)
                ? string.Empty
                : $"{normalizedTitle}::{normalizedAuthors}";
        }

        private static string? TryExtractPublishYear(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var match = System.Text.RegularExpressions.Regex.Match(value, "\\d{4}");
            return match.Success ? match.Value : null;
        }

        private static string? TruncateError(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return value.Length <= 2048 ? value : value[..2048];
        }
    }
}
