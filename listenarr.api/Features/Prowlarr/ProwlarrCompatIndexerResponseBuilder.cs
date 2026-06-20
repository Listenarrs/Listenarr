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

namespace Listenarr.Api.Features.Prowlarr
{
    internal static class ProwlarrCompatIndexerResponseBuilder
    {
        public static object BuildReadIndexer(Indexer indexer, bool authEnabled)
        {
            var categories = SplitCategories(indexer.Categories);
            var apiKey = authEnabled ? indexer.ApiKey : null;

            return new
            {
                id = indexer.Id,
                name = indexer.Name,
                implementation = indexer.Implementation,
                baseUrl = indexer.Url,
                apiKey,
                categories,
                settings = new
                {
                    baseUrl = indexer.Url,
                    apiKey,
                    apiPath = string.Empty,
                    categories
                },
                fields = BuildFields(indexer.Url ?? string.Empty, apiKey, categories),
                tags = Array.Empty<int>()
            };
        }

        public static object BuildSavedIndexer(Indexer indexer)
        {
            var categories = SplitCategories(indexer.Categories);
            var apiKey = indexer.ApiKey ?? string.Empty;

            return new
            {
                id = indexer.Id,
                name = indexer.Name,
                implementation = indexer.Implementation,
                baseUrl = indexer.Url,
                apiKey,
                categories,
                settings = new
                {
                    baseUrl = indexer.Url,
                    apiKey,
                    apiPath = string.Empty,
                    categories
                },
                fields = BuildFields(indexer.Url ?? string.Empty, apiKey, categories),
                tags = Array.Empty<int>()
            };
        }

        public static object BuildFallbackIndexer(int id)
        {
            var categories = Array.Empty<string>();

            return new
            {
                id,
                name = "Prowlarr Indexer",
                implementation = "Newznab",
                baseUrl = string.Empty,
                apiKey = (string?)null,
                categories,
                settings = new
                {
                    baseUrl = string.Empty,
                    apiKey = (string?)null,
                    apiPath = string.Empty,
                    categories
                },
                fields = BuildFields(string.Empty, null, categories),
                tags = Array.Empty<int>()
            };
        }

        private static string[] SplitCategories(string? categories)
        {
            return string.IsNullOrEmpty(categories)
                ? Array.Empty<string>()
                : categories.Split(',').Select(s => s.Trim()).ToArray();
        }

        private static ProwlarrCompatFieldDto[] BuildFields(string baseUrl, object? apiKey, string[] categories)
        {
            return
            [
                new ProwlarrCompatFieldDto("baseUrl", baseUrl),
                new ProwlarrCompatFieldDto("apiKey", apiKey),
                new ProwlarrCompatFieldDto("apiPath", string.Empty),
                new ProwlarrCompatFieldDto("categories", categories)
            ];
        }

        private record ProwlarrCompatFieldDto(string Name, object? Value);
    }
}
