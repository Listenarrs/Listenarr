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

namespace Listenarr.Application.Metadata.Audnexus
{
    /// <summary>
    /// Audnexus reports series membership as two optional records rather than a list, while the
    /// rest of the code works with the Audible shape. Both the product lookup and the metadata
    /// converter need that translation, so it lives here once.
    /// </summary>
    internal static class AudnexusSeriesMapper
    {
        public static List<AudibleSeries>? ToAudibleSeries(AudnexusBookResponse? book)
        {
            if (book == null)
            {
                return null;
            }

            var series = new List<AudibleSeries>();

            foreach (var entry in new[] { book.SeriesPrimary, book.SeriesSecondary })
            {
                if (entry == null)
                {
                    continue;
                }

                series.Add(new AudibleSeries
                {
                    Asin = entry.Asin,
                    Name = entry.Name,
                    Position = entry.Position
                });
            }

            return series.Count == 0 ? null : series;
        }
    }
}
