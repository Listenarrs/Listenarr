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
    internal static class MetadataCacheKeys
    {
        public static string BuildAuthorLookupCacheKey(string region, string name, string? asin = null)
        {
            var normalizedRegion = AudiobookIdentifierNormalizer.NormalizeRegion(region) ?? "us";
            var normalizedName = NormalizeAuthorCacheKey(name);
            var normalizedAsin = string.IsNullOrWhiteSpace(asin) ? null : asin.Trim().ToUpperInvariant();

            return string.IsNullOrWhiteSpace(normalizedAsin)
                ? $"author-lookup:{normalizedRegion}:{normalizedName}"
                : $"author-lookup:{normalizedRegion}:{normalizedName}:{normalizedAsin}";
        }

        public static string NormalizeAuthorCacheKey(string? value)
        {
            return NormalizeLookupKey(value);
        }

        public static string NormalizeSeriesCacheKey(string? value)
        {
            return NormalizeLookupKey(value);
        }

        public static string NormalizeCatalogToken(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return new string(value.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        }

        private static string NormalizeLookupKey(string? value)
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
