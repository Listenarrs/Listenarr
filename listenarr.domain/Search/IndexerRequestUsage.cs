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

namespace Listenarr.Domain.Search
{
    /// <summary>
    /// The number of search requests sent to one indexer during one UTC hour. The rows are the
    /// persisted copy of the rolling daily request budget, so a restart does not hand the sweep a
    /// fresh allowance part-way through an indexer's day.
    /// </summary>
    public class IndexerRequestUsage
    {
        public int IndexerId { get; set; }

        /// <summary>
        /// Start of the UTC hour this row counts.
        /// </summary>
        public DateTime HourStartUtc { get; set; }

        public int Count { get; set; }
    }
}
