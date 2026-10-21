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

namespace Listenarr.Application.Audiobooks.Catalog
{
    // Why a book is broken, one code per problem (the Issues page uses the same codes). The front end
    // has the same dictionary, with a label per code (fe/src/utils/brokenBookReasons.ts): a test keeps
    // the two in step.
    public static class BrokenBookReasons
    {
        public const string MissingTitle = "missing_title";
        public const string MissingAuthor = "missing_author";
        public const string MissingAsin = "missing_asin";
        public const string InvalidAsin = "invalid_asin";
        public const string MissingRegion = "missing_region";

        public static readonly IReadOnlyList<string> All =
        [
            MissingTitle,
            MissingAuthor,
            MissingAsin,
            InvalidAsin,
            MissingRegion,
        ];
    }

    // A book is broken when Audible, the only metadata source today, can't be asked for it: no title,
    // no author, no ASIN (or one that isn't an ASIN), or no region for its ASIN. The library list
    // gives each book its reasons for its temporary "Broken books" group, until the Issues page;
    // later, any book not matched to a metadata provider will count.
    public static class BrokenBookRules
    {
        // asin and asinRegion are the book's main ASIN identifier when it has one, else its ASIN field.
        // Empty when the book isn't broken.
        public static string[] FindReasons(
            string? title,
            IEnumerable<string?>? authors,
            string? asin,
            string? asinRegion)
        {
            var reasons = new List<string>();
            if (string.IsNullOrWhiteSpace(title))
            {
                reasons.Add(BrokenBookReasons.MissingTitle);
            }

            if (authors == null || !authors.Any(author => !string.IsNullOrWhiteSpace(author)))
            {
                reasons.Add(BrokenBookReasons.MissingAuthor);
            }

            if (string.IsNullOrWhiteSpace(asin))
            {
                reasons.Add(BrokenBookReasons.MissingAsin);
            }
            else if (!IsWellFormedAsin(asin))
            {
                reasons.Add(BrokenBookReasons.InvalidAsin);
            }
            else if (string.IsNullOrWhiteSpace(asinRegion))
            {
                reasons.Add(BrokenBookReasons.MissingRegion);
            }

            return reasons.ToArray();
        }

        private static bool IsWellFormedAsin(string asin)
        {
            var trimmed = asin.Trim();
            return trimmed.Length == 10 && trimmed.All(char.IsAsciiLetterOrDigit);
        }
    }
}
