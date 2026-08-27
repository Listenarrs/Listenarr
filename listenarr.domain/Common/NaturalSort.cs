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
        // builder can pad numeric runs to a fixed width before concatenation.
        private static readonly Regex NumericChunkPattern = new(@"\d+|\D+", RegexOptions.Compiled);

        /// <summary>
        /// Build a comparison key for <paramref name="value"/> such that ordinary
        /// ordinal string comparison on the returned key produces a natural sort.
        ///
        /// Numeric runs are zero-padded to 12 digits (large enough for any realistic
        /// chapter / disc / track number) and non-numeric runs are upper-cased for
        /// case-insensitive ordering.
        /// </summary>
        public static string ToKey(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return string.Concat(
                NumericChunkPattern.Matches(value).Select(match =>
                {
                    var chunk = match.Value;
                    return int.TryParse(chunk, out var numeric)
                        ? numeric.ToString("D12")
                        : chunk.ToUpperInvariant();
                }));
        }
    }
}
