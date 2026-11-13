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

namespace Listenarr.Domain.Common
{
    /// <summary>
    /// Natural-sort helpers that treat embedded digit runs as numeric values so that
    /// strings like "Disc 2" sort before "Disc 10" (rather than the lexicographic
    /// "Disc 10" before "Disc 2" you would get from ordinary string comparison).
    ///
    /// Used wherever a user-visible list of files, chapters, or numbered items needs
    /// to come out in the order a human would expect.
    /// </summary>
    public static class NaturalSort
    {
        // Splits a string into alternating runs of digits and non-digits so the key
        // builder can encode numeric runs before concatenation.
        private static readonly Regex NumericChunkPattern = new(@"\d+|\D+", RegexOptions.Compiled);

        // Separates the natural portion of the key from the tie-break tail. Sorts
        // before every printable character, so a key that is a prefix of another
        // ("Part 1" vs "Part 1a") still orders the shorter natural portion first.
        private const char TieBreakSeparator = '\u0001';

        // Width of the length prefix in front of each numeric run. A single filename
        // component cannot hold more than 255 characters on any mainstream filesystem,
        // so four digits is ample and keeps the key compact.
        private const int LengthPrefixWidth = 4;

        /// <summary>
        /// Build a comparison key for <paramref name="value"/> such that ordinary
        /// ordinal string comparison on the returned key produces a natural sort.
        ///
        /// Each numeric run is written as its digit count (fixed width) followed by
        /// its digits with leading zeros removed, so runs of any length compare by
        /// magnitude without being parsed into a bounded integer. Non-numeric runs
        /// are upper-cased for case-insensitive ordering. Names whose natural
        /// portions tie — typically the same number with different zero padding,
        /// "Part 01" and "Part 1" — are then ordered by ordinal comparison of the
        /// original value, so the result is deterministic and no caller has to
        /// fall back on storage order.
        /// </summary>
        public static string ToKey(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var natural = string.Concat(
                NumericChunkPattern.Matches(value).Select(match =>
                {
                    var chunk = match.Value;
                    return char.IsAsciiDigit(chunk[0])
                        ? EncodeNumericRun(chunk)
                        : chunk.ToUpperInvariant();
                }));

            return string.Concat(natural, TieBreakSeparator, value);
        }

        private static string EncodeNumericRun(string digits)
        {
            var significant = digits.TrimStart('0');
            if (significant.Length == 0)
            {
                significant = "0";
            }

            return string.Concat(
                significant.Length.ToString("D" + LengthPrefixWidth, System.Globalization.CultureInfo.InvariantCulture),
                significant);
        }
    }
}
