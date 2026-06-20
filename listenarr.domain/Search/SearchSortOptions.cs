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
    /// Sort criteria for search results
    /// </summary>
    public enum SearchSortBy
    {
        /// <summary>
        /// Sort by number of seeders (torrents only)
        /// </summary>
        Seeders,

        /// <summary>
        /// Sort by file size
        /// </summary>
        Size,

        /// <summary>
        /// Sort by publication date
        /// </summary>
        PublishedDate,

        /// <summary>
        /// Sort by title (alphabetical)
        /// </summary>
        Title,

        /// <summary>
        /// Sort by source/indexer name
        /// </summary>
        Source,

        /// <summary>
        /// Sort by quality score (when available)
        /// </summary>
        Quality,

        /// <summary>
        /// Smart ranking inspired by Prowlarr (multi-tiered scoring)
        /// </summary>
        Smart,

        /// <summary>
        /// Sort by language token (e.g., English, Spanish)
        /// </summary>
        Language,

        /// <summary>
        /// Sort by grabs/snatches (Usenet / DDL popularity)
        /// </summary>
        Grabs
    }

    /// <summary>
    /// Sort direction for search results
    /// </summary>
    public enum SearchSortDirection
    {
        /// <summary>
        /// Sort in ascending order
        /// </summary>
        Ascending,

        /// <summary>
        /// Sort in descending order
        /// </summary>
        Descending
    }
}
