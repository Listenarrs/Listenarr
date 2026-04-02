/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2025 Robbie Davis
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

using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Services.Search.Providers
{
    /// <summary>
    /// Defines a contract for indexer-specific search implementations.
    /// Each indexer type (Torznab, MyAnonamouse, Internet Archive, etc.) implements this interface.
    /// </summary>
    public interface IIndexerSearchProvider
    {
        List<Implementation> Implements { get; }

        /// <summary>
        /// Check if the given implementation is supported by this provider
        /// </summary>
        bool IsImplementing(Implementation type)
        {
            return Implements.Contains(type);
        }

        /// <summary>
        /// Performs a search on the specific indexer.
        /// </summary>
        /// <param name="indexer">The indexer configuration.</param>
        /// <param name="query">The search query.</param>
        /// <param name="category">Optional category filter.</param>
        /// <param name="request">Optional additional request context.</param>
        /// <returns>List of search results from the indexer.</returns>
        Task<List<IndexerSearchResult>> SearchAsync(
            Indexer indexer, 
            string query, 
            string? category = null, 
            Listenarr.Api.Models.SearchRequest? request = null);
    }
}
