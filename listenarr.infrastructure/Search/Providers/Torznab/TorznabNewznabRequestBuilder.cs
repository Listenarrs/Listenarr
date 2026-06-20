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


namespace Listenarr.Infrastructure.Search.Providers.Torznab;

internal static class TorznabNewznabRequestBuilder
{
    public static string BuildUrl(Indexer indexer, string query, string? category)
    {
        var url = indexer.Url.TrimEnd('/');

        // Don't append /api if URL already ends with it (e.g., Prowlarr proxy URLs)
        var apiPath = url.EndsWith("/api", StringComparison.OrdinalIgnoreCase)
            ? ""
            : indexer.Implementation.ToLower() switch
            {
                "torznab" => "/api",
                "newznab" => "/api",
                _ => "/api"
            };

        var queryParams = new List<string>
        {
            $"t=search",
            $"q={Uri.EscapeDataString(query)}"
        };

        // Add API key if provided
        if (!string.IsNullOrEmpty(indexer.ApiKey))
        {
            queryParams.Add($"apikey={Uri.EscapeDataString(indexer.ApiKey)}");
        }

        // Add categories if specified
        if (!string.IsNullOrEmpty(category))
        {
            queryParams.Add($"cat={Uri.EscapeDataString(category)}");
        }
        else if (!string.IsNullOrEmpty(indexer.Categories))
        {
            queryParams.Add($"cat={Uri.EscapeDataString(indexer.Categories)}");
        }

        // Add limit
        queryParams.Add("limit=100");

        // Request extended info for Newznab/Torznab indexers to include grabs/snatches and other attributes when available
        if (!string.IsNullOrEmpty(indexer.Implementation) && (indexer.Implementation.Equals("newznab", StringComparison.OrdinalIgnoreCase) || indexer.Implementation.Equals("torznab", StringComparison.OrdinalIgnoreCase)))
        {
            queryParams.Add("extended=1");
        }

        return $"{url}{apiPath}?{string.Join("&", queryParams)}";
    }
}
