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

namespace Listenarr.Application.Metadata.Audible
{
    internal static class AudibleSearchResultFilter
    {
        public static bool IndicatesPodcast(AudibleSearchResult? result)
        {
            if (result == null) return false;

            var contentType = result.ContentType?.Trim();
            var deliveryType = result.ContentDeliveryType?.Trim();
            var contentTypeIsBookOrProduct = !string.IsNullOrWhiteSpace(contentType) &&
                                             (string.Equals(contentType, "Book", StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(contentType, "Product", StringComparison.OrdinalIgnoreCase));
            var allowedBookDelivery = new[] { "SinglePartBook", "MultiPartBook", "BookSeries" };
            var deliveryTypeIsBook = !string.IsNullOrWhiteSpace(deliveryType) &&
                                     allowedBookDelivery.Any(allowed => string.Equals(allowed, deliveryType, StringComparison.OrdinalIgnoreCase));
            if (contentTypeIsBookOrProduct || deliveryTypeIsBook) return false;

            if (!string.IsNullOrWhiteSpace(result.ContentType) && result.ContentType.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (!string.IsNullOrWhiteSpace(result.ContentDeliveryType) && result.ContentDeliveryType.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (!string.IsNullOrWhiteSpace(result.EpisodeType)) return true;
            if (!string.IsNullOrWhiteSpace(result.Sku) && result.Sku.StartsWith("PC_", StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrWhiteSpace(result.BookFormat) && result.BookFormat.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (result.Genres?.Any(genre =>
                    (!string.IsNullOrWhiteSpace(genre?.Name) && genre.Name.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrWhiteSpace(genre?.Type) && genre.Type.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0)) == true) return true;
            return false;
        }

        public static string? GetPodcastFilterReason(AudibleSearchResult? result)
        {
            if (result == null) return null;
            if (!string.IsNullOrWhiteSpace(result.ContentType) && result.ContentType.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0) return "ContentType contains 'podcast'";
            if (!string.IsNullOrWhiteSpace(result.ContentDeliveryType) && result.ContentDeliveryType.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0) return "ContentDeliveryType contains 'podcast'";
            if (!string.IsNullOrWhiteSpace(result.EpisodeType)) return "EpisodeType present";
            if (!string.IsNullOrWhiteSpace(result.Sku) && result.Sku.StartsWith("PC_", StringComparison.OrdinalIgnoreCase)) return "SKU starts with PC_";
            if (!string.IsNullOrWhiteSpace(result.BookFormat) && result.BookFormat.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0) return "BookFormat contains 'podcast'";
            if (result.Genres?.Any(genre =>
                    (!string.IsNullOrWhiteSpace(genre?.Name) && genre.Name.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrWhiteSpace(genre?.Type) && genre.Type.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0)) == true) return "Genre contains 'podcast'";
            return null;
        }
    }
}
