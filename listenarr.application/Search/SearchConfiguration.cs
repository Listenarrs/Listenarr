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
using Listenarr.Domain.Models;

namespace Listenarr.Application.Search
{
    /// <summary>
    /// Encapsulates all search configuration parameters.
    /// </summary>
    public class SearchConfiguration
    {
        public string Query { get; set; } = string.Empty;
        public string? Category { get; set; }
        public List<string>? ApiIds { get; set; }
        public SearchSortBy SortBy { get; set; } = SearchSortBy.Seeders;
        public SearchSortDirection SortDirection { get; set; } = SearchSortDirection.Descending;
        public bool IsAutomaticSearch { get; set; }

        public SearchConfiguration() { }

        public SearchConfiguration(
            string query,
            string? category = null,
            List<string>? apiIds = null,
            SearchSortBy sortBy = SearchSortBy.Seeders,
            SearchSortDirection sortDirection = SearchSortDirection.Descending,
            bool isAutomaticSearch = false)
        {
            Query = query;
            Category = category;
            ApiIds = apiIds;
            SortBy = sortBy;
            SortDirection = sortDirection;
            IsAutomaticSearch = isAutomaticSearch;
        }
    }
}
