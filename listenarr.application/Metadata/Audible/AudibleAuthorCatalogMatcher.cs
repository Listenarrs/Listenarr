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
    internal static class AudibleAuthorCatalogMatcher
    {
        public static bool MatchesTarget(AudibleSearchResult result, string author, string? authorAsin)
        {
            if (result.Authors == null || result.Authors.Count == 0)
            {
                return false;
            }

            var normalizedTargetName = NormalizeComparableText(author);
            if (string.IsNullOrWhiteSpace(normalizedTargetName))
            {
                return false;
            }

            foreach (var candidate in result.Authors)
            {
                if (!string.IsNullOrWhiteSpace(authorAsin) &&
                    string.Equals(candidate.Asin, authorAsin, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (string.Equals(NormalizeComparableText(candidate.Name), normalizedTargetName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public static string BuildSearchResultKey(AudibleSearchResult result)
        {
            return string.IsNullOrWhiteSpace(result.Asin)
                ? $"{result.Title}|{result.Link}"
                : result.Asin;
        }

        private static string NormalizeComparableText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var joined = string.Join(
                ' ',
                value.Trim()
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .ToLowerInvariant();
            return AudibleService.RemoveDiacritics(joined);
        }
    }
}
