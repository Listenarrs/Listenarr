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
    internal static class AudibleLookupJsonParser
    {
        private static readonly JsonSerializerOptions s_options = new() { PropertyNameCaseInsensitive = true };

        public static AuthorLookupItem? ParseSingleAuthorLookupItem(string lookupJson)
        {
            var items = ParseAuthorLookupItems(lookupJson);
            return items.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Asin)) ?? items.FirstOrDefault();
        }

        public static SeriesLookupItem? ParseSeriesLookupItem(string lookupJson)
        {
            var items = ParseSeriesLookupItems(lookupJson);
            return items.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Asin)) ?? items.FirstOrDefault();
        }

        public static List<AuthorLookupItem> ParseAuthorLookupItems(string lookupJson)
        {
            if (string.IsNullOrWhiteSpace(lookupJson)) return new List<AuthorLookupItem>();

            var trimmed = lookupJson.TrimStart();
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                return JsonSerializer.Deserialize<List<AuthorLookupItem>>(lookupJson, s_options) ?? new List<AuthorLookupItem>();
            }

            var single = JsonSerializer.Deserialize<AuthorLookupItem>(lookupJson, s_options);
            if (single != null && (!string.IsNullOrWhiteSpace(single.Asin) || !string.IsNullOrWhiteSpace(single.Name)))
            {
                return new List<AuthorLookupItem> { single };
            }

            var doc = JsonSerializer.Deserialize<AuthorLookupEnvelope>(lookupJson, s_options);
            if (doc == null) return new List<AuthorLookupItem>();
            if (doc.Results?.Any() == true) return doc.Results;
            if (!string.IsNullOrWhiteSpace(doc.Asin))
            {
                return new List<AuthorLookupItem>
                {
                    new AuthorLookupItem
                    {
                        Asin = doc.Asin,
                        Name = doc.Name,
                        Image = doc.Image,
                        Region = doc.Region,
                        Description = doc.Description
                    }
                };
            }

            return new List<AuthorLookupItem>();
        }

        public static List<SeriesLookupItem> ParseSeriesLookupItems(string lookupJson)
        {
            if (string.IsNullOrWhiteSpace(lookupJson)) return new List<SeriesLookupItem>();

            var trimmed = lookupJson.TrimStart();
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                return JsonSerializer.Deserialize<List<SeriesLookupItem>>(lookupJson, s_options) ?? new List<SeriesLookupItem>();
            }

            var single = JsonSerializer.Deserialize<SeriesLookupItem>(lookupJson, s_options);
            if (single != null && (!string.IsNullOrWhiteSpace(single.Asin) || !string.IsNullOrWhiteSpace(single.Name)))
            {
                return new List<SeriesLookupItem> { single };
            }

            var doc = JsonSerializer.Deserialize<SeriesLookupEnvelope>(lookupJson, s_options);
            if (doc == null) return new List<SeriesLookupItem>();
            if (doc.Results?.Any() == true) return doc.Results;
            if (!string.IsNullOrWhiteSpace(doc.Asin))
            {
                return new List<SeriesLookupItem>
                {
                    new SeriesLookupItem
                    {
                        Asin = doc.Asin,
                        Name = doc.Name,
                        Region = doc.Region,
                        Description = doc.Description,
                        Position = doc.Position
                    }
                };
            }

            return new List<SeriesLookupItem>();
        }
    }
}
