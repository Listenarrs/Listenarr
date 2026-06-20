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

using System.Text.Json;

namespace Listenarr.Application.Metadata.Audible
{
    internal static class AudibleProductMapper
    {
        public static AudibleBookResponse? MapProductToBookResponse(JsonElement product, string region)
        {
            if (product.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var asin = GetString(product, "asin");
            if (string.IsNullOrWhiteSpace(asin))
            {
                return null;
            }

            return new AudibleBookResponse
            {
                Asin = asin,
                Title = GetString(product, "title"),
                Subtitle = GetString(product, "subtitle"),
                Authors = GetArray(product, "authors")
                    .Select(author => new AudibleAuthor
                    {
                        Asin = GetString(author, "asin"),
                        Name = GetString(author, "name"),
                        Region = AudibleRequestHelper.NormalizeRegion(region)
                    })
                    .Where(author => !string.IsNullOrWhiteSpace(author.Name))
                    .ToList(),
                Narrators = GetArray(product, "narrators")
                    .Select(narrator => new AudibleNarrator
                    {
                        Name = GetString(narrator, "name")
                    })
                    .Where(narrator => !string.IsNullOrWhiteSpace(narrator.Name))
                    .ToList(),
                Publisher = GetString(product, "publisher_name"),
                PublishDate = GetString(product, "publication_datetime"),
                Description = GetString(product, "publisher_summary")
                    ?? GetString(product, "merchandising_summary")
                    ?? GetString(product, "extended_product_description")
                    ?? GetString(product, "merchandising_description"),
                ImageUrl = GetHighestResolutionImage(product),
                LengthMinutes = GetInt32(product, "runtime_length_min"),
                Language = GetString(product, "language"),
                Genres = MapGenres(product),
                Series = GetArray(product, "series")
                    .Select(series => new AudibleSeries
                    {
                        Asin = GetString(series, "asin"),
                        Name = GetString(series, "title"),
                        Position = GetString(series, "sequence")
                    })
                    .Where(series => !string.IsNullOrWhiteSpace(series.Name))
                    .ToList(),
                Explicit = GetBoolean(product, "is_adult_product"),
                ReleaseDate = GetString(product, "release_date"),
                Isbn = GetString(product, "isbn"),
                Region = AudibleRequestHelper.NormalizeRegion(region),
                BookFormat = GetString(product, "format_type"),
                ContentType = GetString(product, "content_type"),
                ContentDeliveryType = GetString(product, "content_delivery_type"),
                EpisodeType = GetString(product, "episode_type"),
                Sku = GetString(product, "sku")
            };
        }

        public static AudibleSearchResult? MapBookResponseToSearchResult(AudibleBookResponse book)
        {
            if (string.IsNullOrWhiteSpace(book.Asin))
            {
                return null;
            }

            return new AudibleSearchResult
            {
                Asin = book.Asin,
                Title = book.Title,
                Subtitle = book.Subtitle,
                Authors = book.Authors,
                ImageUrl = book.ImageUrl,
                RuntimeLengthMin = book.LengthMinutes,
                LengthMinutes = book.LengthMinutes,
                RuntimeMinutes = book.LengthMinutes,
                Language = book.Language,
                ContentType = book.ContentType,
                ContentDeliveryType = book.ContentDeliveryType,
                EpisodeType = book.EpisodeType,
                Sku = book.Sku,
                BookFormat = book.BookFormat,
                Genres = book.Genres,
                Series = book.Series,
                Publisher = book.Publisher,
                Narrators = book.Narrators,
                ReleaseDate = book.ReleaseDate,
                Link = string.IsNullOrWhiteSpace(book.Asin) ? null : $"{AudibleRequestHelper.GetBaseUrl(book.Region ?? "us")}/pd/{book.Asin}",
                Isbn = book.Isbn
            };
        }

        public static List<AudibleSearchResult> ApplyLanguageFilter(List<AudibleSearchResult> results, string? language)
        {
            if (string.IsNullOrWhiteSpace(language) ||
                string.Equals(language, "all", StringComparison.OrdinalIgnoreCase))
            {
                return results;
            }

            return results
                .Where(result => string.IsNullOrWhiteSpace(result.Language) ||
                                 string.Equals(result.Language, language, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private static IEnumerable<JsonElement> GetArray(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray()
                : Enumerable.Empty<JsonElement>();
        }

        private static string? GetString(JsonElement element, params string[] path)
        {
            var current = element;
            foreach (var segment in path)
            {
                if (current.ValueKind != JsonValueKind.Object ||
                    !current.TryGetProperty(segment, out current))
                {
                    return null;
                }
            }

            return current.ValueKind switch
            {
                JsonValueKind.String => current.GetString(),
                JsonValueKind.Number => current.ToString(),
                JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
                JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
                _ => null
            };
        }

        private static int? GetInt32(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number;
            }

            return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
        }

        private static bool? GetBoolean(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
                _ => null
            };
        }

        private static string? GetHighestResolutionImage(JsonElement product)
        {
            if (product.TryGetProperty("product_images", out var images) && images.ValueKind == JsonValueKind.Object)
            {
                var bestKey = images.EnumerateObject()
                    .Select(property => new { property.Name, Numeric = int.TryParse(property.Name, out var size) ? size : 0 })
                    .OrderByDescending(property => property.Numeric)
                    .FirstOrDefault();
                if (bestKey != null && images.TryGetProperty(bestKey.Name, out var imageValue))
                {
                    return imageValue.GetString();
                }
            }

            return GetString(product, "cover_art_url");
        }

        private static List<AudibleGenre> MapGenres(JsonElement product)
        {
            var genres = new List<AudibleGenre>();
            foreach (var ladderEntry in GetArray(product, "category_ladders"))
            {
                if (!ladderEntry.TryGetProperty("ladder", out var ladder) || ladder.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var index = 0;
                foreach (var genre in ladder.EnumerateArray())
                {
                    var name = GetString(genre, "name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        index++;
                        continue;
                    }

                    genres.Add(new AudibleGenre
                    {
                        Asin = GetString(genre, "id"),
                        Name = name,
                        Type = index == 0 ? "Genres" : "Tags"
                    });
                    index++;
                }
            }

            return genres
                .GroupBy(genre => $"{genre.Asin}|{genre.Name}|{genre.Type}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }
    }
}
