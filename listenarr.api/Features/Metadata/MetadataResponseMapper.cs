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
    internal static class MetadataResponseMapper
    {
        public static MetadataController.AuthorCatalogBookItem MapAuthorCatalogBook(AudibleSearchResult book)
        {
            var primarySeries = book.Series?.FirstOrDefault();
            var runtime = book.LengthMinutes ?? book.RuntimeLengthMin ?? book.RuntimeMinutes;

            return new MetadataController.AuthorCatalogBookItem
            {
                Asin = book.Asin,
                Title = book.Title ?? "Unknown Title",
                Subtitle = book.Subtitle,
                Authors = MapNames(book.Authors, author => author.Name),
                ImageUrl = book.ImageUrl,
                Runtime = runtime,
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

        public static MetadataController.SeriesCatalogBookItem MapSeriesCatalogBook(AudibleSearchResult book)
        {
            var primarySeries = book.Series?.FirstOrDefault();
            var runtime = book.LengthMinutes ?? book.RuntimeLengthMin ?? book.RuntimeMinutes;

            return new MetadataController.SeriesCatalogBookItem
            {
                Asin = book.Asin,
                Title = book.Title ?? "Unknown Title",
                Subtitle = book.Subtitle,
                Authors = MapNames(book.Authors, author => author.Name),
                ImageUrl = book.ImageUrl,
                Runtime = runtime,
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

        public static List<MetadataController.RelatedAuthorItem> MapSimilarAuthors(
            IEnumerable<AudnexusSimilarAuthor>? authors,
            string currentAuthorName)
        {
            if (authors == null)
            {
                return new List<MetadataController.RelatedAuthorItem>();
            }

            return authors
                .Where(author => !string.IsNullOrWhiteSpace(author.Name))
                .Where(author => !string.Equals(author.Name, currentAuthorName, StringComparison.OrdinalIgnoreCase))
                .GroupBy(author => author.Name!, StringComparer.OrdinalIgnoreCase)
                .Select(group => new MetadataController.RelatedAuthorItem
                {
                    Asin = group.First().Asin,
                    Name = group.First().Name ?? string.Empty
                })
                .ToList();
        }

        public static bool HasCompleteAuthorLookupData(
            string? cachedPath,
            string? description,
            IEnumerable<MetadataController.RelatedAuthorItem>? similarAuthors)
        {
            return !string.IsNullOrWhiteSpace(cachedPath) &&
                !string.IsNullOrWhiteSpace(description) &&
                (similarAuthors?.Any(author => !string.IsNullOrWhiteSpace(author.Name)) ?? false);
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
