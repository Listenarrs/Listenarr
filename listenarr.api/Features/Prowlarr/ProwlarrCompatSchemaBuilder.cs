/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Api.Features.Prowlarr
{
    internal static class ProwlarrCompatSchemaBuilder
    {
        public static object BuildInfo()
        {
            return new
            {
                implementations = new[] { "Newznab", "Torznab" },
                schema = "/api/v1/indexer/schema"
            };
        }

        public static object BuildSchema()
        {
            var fields = new[]
            {
                new IndexerFieldDto { Name = "name", Type = "string", Required = true, Description = "Indexer name" },
                new IndexerFieldDto { Name = "baseUrl", Type = "string", Required = true, Description = "Base URL of indexer" },
                new IndexerFieldDto { Name = "apiPath", Type = "string", Required = true, Description = "API path (e.g. /api or /torznab)" },
                new IndexerFieldDto { Name = "apiKey", Type = "string", Required = false, Description = "API key or token" },
                new IndexerFieldDto { Name = "categories", Type = "array", Required = false, Description = "Optional categories filter (array of integers or strings)" }
            };

            return new[]
            {
                new { fields = fields, implementation = "Newznab" },
                new { fields = fields, implementation = "Torznab" }
            };
        }

        private record IndexerFieldDto
        {
            public string Name { get; init; } = string.Empty;
            public string Type { get; init; } = string.Empty;
            public bool Required { get; init; }
            public string Description { get; init; } = string.Empty;
        }
    }
}
