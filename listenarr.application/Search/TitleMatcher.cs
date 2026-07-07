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
using System.Text;

namespace Listenarr.Application.Search
{
    /// <summary>
    /// Punctuation-tolerant title matching. A naive case-insensitive
    /// comparison fails on common punctuation mismatches between user-entered
    /// or filename-derived titles and canonical catalog titles (hyphen vs.
    /// colon, en/em dashes, smart quotes, doubled whitespace), causing
    /// legitimate matches to be silently dropped.
    /// </summary>
    public static class TitleMatcher
    {
        /// <summary>
        /// Lowercases the input and replaces any character that is not a
        /// letter or digit with a single space, collapsing consecutive
        /// whitespace. Strips characters that commonly differ between
        /// user input and indexer titles: -, –, —, :, ;, ,, ., (), [], etc.
        /// </summary>
        public static string Normalize(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            var sb = new StringBuilder(input.Length);
            bool lastWasSpace = true;
            foreach (var ch in input)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(char.ToLowerInvariant(ch));
                    lastWasSpace = false;
                }
                else if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }
            if (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length--;
            return sb.ToString();
        }

        /// <summary>
        /// True when the candidate's <paramref name="title"/> or
        /// <paramref name="subtitle"/> plausibly matches the user-supplied
        /// <paramref name="query"/>. Primary check is normalized substring;
        /// for multi-word queries a tight token-overlap fallback catches
        /// reordering / extra noise. Single-token queries (e.g. "1634") fall
        /// through to substring only, to avoid admitting every book that
        /// contains the same one word.
        /// </summary>
        public static bool Matches(string? title, string? subtitle, string? query)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            var normalizedQuery = Normalize(query);
            if (normalizedQuery.Length == 0) return true;

            if (ContainsNormalized(title, normalizedQuery)) return true;
            if (ContainsNormalized(subtitle, normalizedQuery)) return true;

            // Token-overlap fallback: only when the query has 2+ significant
            // tokens, and we require every one of them to appear in the
            // candidate. This handles word reordering and extra punctuation
            // / noise around the title without admitting books that merely
            // share a couple of common words.
            var queryTokens = new HashSet<string>(
                Filters.SignificantTokens.From(query),
                StringComparer.OrdinalIgnoreCase);
            if (queryTokens.Count < 2) return false;

            var combined = (title ?? string.Empty) + " " + (subtitle ?? string.Empty);
            var candidateTokens = new HashSet<string>(
                Filters.SignificantTokens.From(combined),
                StringComparer.OrdinalIgnoreCase);
            return queryTokens.IsSubsetOf(candidateTokens);
        }

        private static bool ContainsNormalized(string? field, string normalizedQuery)
        {
            if (string.IsNullOrWhiteSpace(field)) return false;
            var normalizedField = Normalize(field);
            return normalizedField.Length > 0
                && normalizedField.Contains(normalizedQuery, StringComparison.Ordinal);
        }
    }
}
