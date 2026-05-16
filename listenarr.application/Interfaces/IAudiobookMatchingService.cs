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

namespace Listenarr.Application.Interfaces
{
    /// <summary>
    /// Service for matching downloads with audiobooks using multiple identifying criteria
    /// </summary>
    public interface IAudiobookMatchingService
    {
        /// <summary>
        /// Find the most likely audiobook match for a download using multiple criteria
        /// </summary>
        Task<Audiobook?> FindBestAudiobookMatchAsync(Download download, double minimumConfidence = 0.8);

        /// <summary>
        /// Find the most likely audiobook match for a search result using multiple criteria
        /// </summary>
        Task<Audiobook?> FindBestAudiobookMatchAsync(SearchResult searchResult, double minimumConfidence = 0.8);

        /// <summary>
        /// Calculate confidence score between a download and audiobook (0.0 to 1.0)
        /// </summary>
        double CalculateMatchConfidence(Download download, Audiobook audiobook);

        /// <summary>
        /// Calculate confidence score between a search result and audiobook (0.0 to 1.0)
        /// </summary>
        double CalculateMatchConfidence(SearchResult searchResult, Audiobook audiobook);

        /// <summary>
        /// Populate download metadata from search result for better matching
        /// </summary>
        void PopulateDownloadFromSearchResult(Download download, SearchResult searchResult);
    }
}
