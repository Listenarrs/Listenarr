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

namespace Listenarr.Application.Search.Core
{
    internal sealed record ParsedSearchQuery(
        string? SearchType,
        string ActualQuery,
        string? Asin,
        string? Isbn,
        string? Author,
        string? Title);

    internal static class SearchQueryParser
    {
        private static readonly string[] Prefixes = { "AUTHOR:", "TITLE:", "ISBN:", "ASIN:" };

        public static ParsedSearchQuery Parse(string query)
        {
            var foundRanges = new List<(int Start, int End)>();
            var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var pos = 0;
            while (pos < query.Length)
            {
                var foundAt = -1;
                string? foundPrefix = null;
                foreach (var prefix in Prefixes)
                {
                    var idx = query.IndexOf(prefix, pos, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0 && (foundAt == -1 || idx < foundAt))
                    {
                        foundAt = idx;
                        foundPrefix = prefix;
                    }
                }

                if (foundAt == -1 || foundPrefix == null)
                {
                    break;
                }

                var valueStart = foundAt + foundPrefix.Length;
                var nextAt = -1;
                foreach (var prefix in Prefixes)
                {
                    var idx = query.IndexOf(prefix, valueStart, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0 && (nextAt == -1 || idx < nextAt))
                    {
                        nextAt = idx;
                    }
                }

                var valueEnd = nextAt == -1 ? query.Length : nextAt;
                var value = query.Substring(valueStart, valueEnd - valueStart).Trim();
                if (!string.IsNullOrEmpty(value))
                {
                    parsed[foundPrefix] = value;
                }

                foundRanges.Add((foundAt, valueEnd));
                pos = valueEnd;
            }

            parsed.TryGetValue("ASIN:", out var asin);
            parsed.TryGetValue("ISBN:", out var isbn);
            parsed.TryGetValue("AUTHOR:", out var author);
            parsed.TryGetValue("TITLE:", out var title);

            asin = asin?.Trim();
            isbn = isbn?.Trim();
            author = author?.Trim();
            title = title?.Trim();

            var searchType = DetermineSearchType(asin, isbn, author, title);
            var actualQuery = BuildActualQuery(query, foundRanges);

            return new ParsedSearchQuery(searchType, actualQuery, asin, isbn, author, title);
        }

        private static string? DetermineSearchType(string? asin, string? isbn, string? author, string? title)
        {
            if (!string.IsNullOrEmpty(asin))
            {
                return "ASIN";
            }

            if (!string.IsNullOrEmpty(isbn))
            {
                return "ISBN";
            }

            if (!string.IsNullOrEmpty(author) && !string.IsNullOrEmpty(title))
            {
                return "AUTHOR_TITLE";
            }

            if (!string.IsNullOrEmpty(author))
            {
                return "AUTHOR";
            }

            return !string.IsNullOrEmpty(title) ? "TITLE" : null;
        }

        private static string BuildActualQuery(string query, List<(int Start, int End)> foundRanges)
        {
            if (!foundRanges.Any())
            {
                return query;
            }

            foundRanges.Sort((a, b) => a.Start.CompareTo(b.Start));
            var builder = new System.Text.StringBuilder();
            var idx = 0;
            foreach (var range in foundRanges)
            {
                if (range.Start > idx)
                {
                    builder.Append(query.Substring(idx, range.Start - idx));
                }

                idx = range.End;
            }

            if (idx < query.Length)
            {
                builder.Append(query.Substring(idx));
            }

            var collapsed = builder.ToString();
            while (collapsed.Contains("  "))
            {
                collapsed = collapsed.Replace("  ", " ");
            }

            return collapsed.Trim();
        }
    }
}
