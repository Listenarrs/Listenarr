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
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Listenarr.Api.Filters;

/// <summary>
/// Swagger document filter that defines a logical ordering and descriptions for API tags.
/// Tags are displayed in the order specified here; any unlisted tags appear at the end.
/// </summary>
public sealed class SwaggerTagOrderDocumentFilter : IDocumentFilter
{
    private static readonly (string Name, string Description)[] OrderedTags =
    [
        ("Library",                  "Audiobook CRUD, scanning, file moves, bulk operations, and manual import"),
        ("Search",                   "Multi-API audiobook search, intelligent search, and direct Audible lookups"),
        ("Metadata",                 "ASIN/ISBN metadata lookup and author search"),
        ("Downloads",                "Download queue, send to client, reprocessing, and download record management"),
        ("History",                  "Event history browsing, filtering, and cleanup"),
        ("Indexers",                 "Indexer CRUD, testing, and Prowlarr import"),
        ("Quality Profiles",         "Quality profile CRUD and result scoring"),
        ("Settings",                 "Application settings and startup configuration"),
        ("Download Clients",         "Download client CRUD and connectivity testing"),
        ("API Sources",              "API source configuration management"),
        ("Notifications",            "Test and manage webhook notifications"),
        ("Security",                 "API key generation and rotation"),
        ("Root Folders",             "Root folder CRUD for audiobook storage paths"),
        ("Remote Path Mappings",     "Path mapping CRUD for cross-system file path translation"),
        ("File System",              "Directory browsing, path validation, and volume checks"),
        ("System",                   "System info, health checks, logs, FFmpeg, and admin tools"),
        ("Images",                   "Cover image retrieval and cache management"),
        ("Account",                  "Authentication, user management, and CSRF tokens"),
        ("Discord",                  "Discord bot management and diagnostics"),
        ("Prowlarr Compatibility",   "Prowlarr-compatible indexer endpoints for external integration"),
    ];

    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        // Build a lookup of existing tags in the document (auto-generated from controller actions)
        var existingTagNames = new HashSet<string>(
            swaggerDoc.Tags?
                .Select(t => t.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>() ?? [],
            StringComparer.OrdinalIgnoreCase);

        // Also collect any tags referenced by operations that don't yet have a top-level entry
        if (swaggerDoc.Paths != null)
        {
            foreach (var pathItem in swaggerDoc.Paths.Values)
            {
                if (pathItem.Operations == null)
                {
                    continue;
                }

                foreach (var operation in pathItem.Operations.Values)
                {
                    if (operation.Tags == null)
                    {
                        continue;
                    }

                    foreach (var tag in operation.Tags)
                    {
                        if (!string.IsNullOrWhiteSpace(tag.Name))
                        {
                            existingTagNames.Add(tag.Name);
                        }
                    }
                }
            }
        }

        // Build the ordered tag list: ordered tags first, then any remaining tags alphabetically
        var result = new List<OpenApiTag>();

        foreach (var (name, description) in OrderedTags)
        {
            if (existingTagNames.Contains(name))
            {
                result.Add(new OpenApiTag { Name = name, Description = description });
                existingTagNames.Remove(name);
            }
        }

        // Append any tags not in our predefined list (future controllers, etc.)
        foreach (var remaining in existingTagNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            result.Add(new OpenApiTag { Name = remaining });
        }

        swaggerDoc.Tags = new HashSet<OpenApiTag>(result);
    }
}
