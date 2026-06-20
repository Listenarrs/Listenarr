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

using System.Text.RegularExpressions;

namespace Listenarr.Api.Features.Search
{
    internal static class SearchRequestNormalizer
    {
        public static string? NormalizeStructuredAdvancedField(string? value, string prefix)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            var trimmed = value.Trim();
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            var stripped = trimmed.Substring(prefix.Length).Trim();
            return string.IsNullOrWhiteSpace(stripped) ? null : stripped;
        }

        public static string? ConvertIsbn10ToIsbn13(string isbn10)
        {
            if (string.IsNullOrWhiteSpace(isbn10)) return null;
            if (isbn10.Length != 10) return null;

            var first9 = isbn10.Substring(0, 9);
            if (!Regex.IsMatch(first9, "^[0-9]{9}$")) return null;

            var twelve = "978" + first9;
            int sum = 0;
            for (int i = 0; i < 12; i++)
            {
                int d = twelve[i] - '0';
                sum += (i % 2 == 0) ? d : d * 3;
            }

            int mod = sum % 10;
            int check = (10 - mod) % 10;
            return string.Concat(twelve, check);
        }
    }
}
