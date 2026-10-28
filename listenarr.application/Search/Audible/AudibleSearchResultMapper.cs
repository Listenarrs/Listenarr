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

namespace Listenarr.Application.Search.Audible
{
    internal static class AudibleSearchResultMapper
    {
        public static async Task<List<SearchResult>> ConvertToSearchResultsAsync(
            IEnumerable<AudibleSearchResult> books,
            MetadataConverters metadataConverters,
            string region,
            IReadOnlyDictionary<string, AudibleBookResponse>? detailedMetadataByAsin = null,
            ILogger? logger = null,
            bool continueOnConversionError = false)
        {
            var converted = new List<SearchResult>();

            foreach (var book in books.Where(book => !string.IsNullOrWhiteSpace(book.Asin)))
            {
                try
                {
                    var bookResponse = detailedMetadataByAsin != null &&
                                       detailedMetadataByAsin.TryGetValue(book.Asin!, out var detailed)
                        ? detailed
                        : ToBookResponse(book, region);

                    var metadata = metadataConverters.ConvertAudibleToMetadata(bookResponse, book.Asin!, "Audible");
                    var result = await metadataConverters.ConvertMetadataToSearchResultAsync(metadata, book.Asin!);
                    result.IsEnriched = true;
                    result.MetadataSource = "Audible";
                    converted.Add(result);
                }
                catch (Exception ex) when (
                    continueOnConversionError &&
                    ex is not OperationCanceledException &&
                    ex is not OutOfMemoryException &&
                    ex is not StackOverflowException)
                {
                    logger?.LogDebug(ex, "Failed converting audible data for ASIN {Asin}", book.Asin);
                }
            }

            return converted;
        }

        private static AudibleBookResponse ToBookResponse(AudibleSearchResult book, string region)
        {
            return new AudibleBookResponse
            {
                Asin = book.Asin,
                Title = book.Title,
                Subtitle = book.Subtitle,
                Authors = book.Authors,
                ImageUrl = book.ImageUrl,
                Language = book.Language,
                BookFormat = book.BookFormat,
                Genres = book.Genres,
                Series = book.Series,
                Publisher = book.Publisher,
                Narrators = book.Narrators,
                ReleaseDate = book.ReleaseDate,
                Region = region
            };
        }
    }
}
