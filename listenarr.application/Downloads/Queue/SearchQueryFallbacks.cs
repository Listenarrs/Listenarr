/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Text.RegularExpressions;

namespace Listenarr.Application.Downloads.Queue
{
    public static partial class SearchQueryFallbacks
    {
        // Retries drop the author, so we search only the Newznab audiobook category to keep a
        // bare title such as "Ghosts" from matching music or video releases.
        public const string RetryCategory = "3030";

        [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
        private static partial Regex EditionSuffix();

        public static IReadOnlyList<string> RetryQueries(string primaryQuery, string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return [];
            }

            return new[] { title, EditionSuffix().Replace(title, string.Empty) }
                .Select(retry => retry.Trim())
                .Where(retry => retry.Length > 0 && !retry.Equals(primaryQuery.Trim(), StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static async Task<(string Query, List<SearchResult> Results)> SearchAsync(
            ISearchService searchService,
            string primaryQuery,
            string? title)
        {
            var results = await searchService.SearchAsync(primaryQuery, isAutomaticSearch: true);
            var query = primaryQuery;

            foreach (var retry in RetryQueries(primaryQuery, title))
            {
                if (results.Count > 0)
                {
                    break;
                }

                query = retry;
                results = await searchService.SearchAsync(retry, RetryCategory, isAutomaticSearch: true);
            }

            return (query, results);
        }
    }
}
