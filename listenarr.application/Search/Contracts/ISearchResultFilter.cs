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


namespace Listenarr.Application.Search.Contracts;

/// <summary>
/// Interface for filtering search results based on specific criteria.
/// </summary>
public interface ISearchResultFilter
{
    /// <summary>
    /// Determines if the result should be filtered out (excluded).
    /// </summary>
    /// <param name="result">The search result to evaluate</param>
    /// <returns>True if the result should be filtered out, false to keep it</returns>
    bool ShouldFilter(SearchResult result);

    /// <summary>
    /// Reason why the result was filtered (for logging/debugging).
    /// </summary>
    string FilterReason { get; }
}
