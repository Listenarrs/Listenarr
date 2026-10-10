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
using System.Globalization;

namespace Listenarr.Application.Search.Scoring
{
    public partial class SearchResultScorer
    {
        /// <summary>
        /// Parses an indexer's published date into a UTC instant.
        /// </summary>
        /// <remarks>
        /// A bare <see cref="DateTime.TryParse(string, out DateTime)"/> converts an offset-bearing
        /// string to the host's local time and hands back <see cref="DateTimeKind.Local"/>. The
        /// scorer then subtracts that from <see cref="DateTime.UtcNow"/>, so every age came out
        /// wrong by the server's UTC offset: results looked older west of UTC and newer east of
        /// it. At day granularity that only shows at a boundary; at the minute granularity the
        /// indexer's minimum age needs, it decides the answer.
        ///
        /// AdjustToUniversal converts an offset to UTC rather than to local time, and
        /// AssumeUniversal covers indexer dates that carry no offset at all, which is the safer
        /// reading of a feed whose timestamps are conventionally UTC.
        ///
        /// This is a separate method so it can be asserted on directly. A test that drives the
        /// scorer end to end cannot see the difference on a host whose local time is UTC, which
        /// is every hosted CI runner: there the old code and the new code return the same
        /// instant. The Kind does differ on every host, so that is what a test should pin.
        /// </remarks>
        /// <param name="publishedDate">The raw value from the indexer, which may be null or empty.</param>
        /// <param name="publishedUtc">The parsed instant, with Kind set to Utc.</param>
        /// <returns>True if the value parsed; false if it was absent or unrecognised.</returns>
        internal static bool TryParsePublishedDateUtc(string? publishedDate, out DateTime publishedUtc)
        {
            publishedUtc = default;
            if (string.IsNullOrEmpty(publishedDate))
            {
                return false;
            }

            return DateTime.TryParse(
                publishedDate,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out publishedUtc);
        }
    }
}
