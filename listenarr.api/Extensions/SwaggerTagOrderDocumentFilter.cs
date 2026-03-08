using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Listenarr.Api.Extensions;

/// <summary>
/// Swagger document filter that defines a logical ordering and descriptions for API tags.
/// Tags are displayed in the order specified here; any unlisted tags appear at the end.
/// </summary>
public sealed class SwaggerTagOrderDocumentFilter : IDocumentFilter
{
    private static readonly (string Name, string Description)[] OrderedTags =
    [
        ("Library",                  "Audiobook CRUD, scanning, file moves, bulk operations, and manual import"),
        ("Search",                   "Multi-API audiobook search, intelligent search, and Audimeta lookups"),
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
            swaggerDoc.Tags?.Select(t => t.Name) ?? [],
            StringComparer.OrdinalIgnoreCase);

        // Also collect any tags referenced by operations that don't yet have a top-level entry
        foreach (var pathItem in swaggerDoc.Paths.Values)
        {
            foreach (var operation in pathItem.Operations.Values)
            {
                foreach (var tag in operation.Tags)
                {
                    existingTagNames.Add(tag.Name);
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

        swaggerDoc.Tags = result;
    }
}
