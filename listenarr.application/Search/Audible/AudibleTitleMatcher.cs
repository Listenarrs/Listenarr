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

namespace Listenarr.Application.Search.Audible
{
    /// <summary>
    /// Decides whether an Audible catalogue entry matches a searched title. Folder and tag titles
    /// often carry the subtitle or series in brackets ("Dragons et serpents (Les cités des Anciens 1)")
    /// while Audible splits it into title and subtitle, so the comparison also runs on
    /// title, subtitle and series together, with punctuation, case, accents and word order ignored. That
    /// comparison works on whole words, so "Arsène Lupin 1" does not match a book subtitled "Arsène Lupin 10".
    /// </summary>
    internal static class AudibleTitleMatcher
    {
        public static bool Matches(AudibleSearchResult book, string title)
        {
            if (Contains(book.Title, title) || Contains(book.Subtitle, title))
            {
                return true;
            }

            var query = Normalize(title);
            if (query.Length == 0)
            {
                return false;
            }

            // Every word of the search, in any order, as a whole word of the title, subtitle or series
            // (name and position): "Primal Hunter 4" matches "Primal Hunter" / "Primal Hunter, Tome 4".
            // Whole words keep "Arsène Lupin 1" from matching a book subtitled "Arsène Lupin 10".
            var series = string.Join(' ', (book.Series ?? []).Select(s => $"{s.Name} {s.Position}"));
            var words = Normalize($"{book.Title} {book.Subtitle} {series}").Split(' ').ToHashSet(StringComparer.Ordinal);
            return query.Split(' ').All(words.Contains);
        }

        private static bool Contains(string? value, string title) =>
            !string.IsNullOrWhiteSpace(value) && value.IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0;

        internal static string Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length);
            foreach (var c in AudibleService.RemoveDiacritics(value).ToLowerInvariant())
            {
                builder.Append(char.IsLetterOrDigit(c) ? c : ' ');
            }

            return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
