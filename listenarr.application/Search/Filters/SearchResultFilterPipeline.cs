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
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Search.Filters;

/// <summary>
/// Applies a pipeline of filters to search results.
/// </summary>
public class SearchResultFilterPipeline
{
    private readonly IEnumerable<ISearchResultFilter> _filters;
    private readonly ILogger<SearchResultFilterPipeline> _logger;

    public SearchResultFilterPipeline(IEnumerable<ISearchResultFilter> filters, ILogger<SearchResultFilterPipeline> logger)
    {
        _filters = filters;
        _logger = logger;
    }

    /// <summary>
    /// Filters a list of search results through all registered filters.
    /// </summary>
    /// <param name="results">Results to filter</param>
    /// <param name="logFilteredResults">Whether to log filtered results</param>
    /// <returns>Filtered list with unwanted results removed</returns>
    public List<SearchResult> ApplyFilters(List<SearchResult> results, bool logFilteredResults = true)
    {
        var filtered = new List<SearchResult>();

        foreach (var result in results)
        {
            var matchingFilter = _filters.FirstOrDefault(f => f.ShouldFilter(result));
            bool shouldFilter = matchingFilter != null;
            string? filterReason = matchingFilter?.FilterReason;

            if (shouldFilter)
            {
                if (logFilteredResults)
                {
                    _logger.LogInformation("Filtered out result: {Title} (ASIN: {Asin}) - Reason: {Reason}",
                        result.Title, result.Asin, filterReason);
                }
            }
            else
            {
                filtered.Add(result);
            }
        }

        return filtered;
    }

    /// <summary>
    /// Checks if a single result would be filtered.
    /// </summary>
    public bool WouldFilter(SearchResult result, out string? filterReason)
    {
        foreach (var filter in _filters.Where(f => f.ShouldFilter(result)))
        {
            filterReason = filter.FilterReason;
            return true;
        }

        filterReason = null;
        return false;
    }
}
