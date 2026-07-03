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
    }
}
