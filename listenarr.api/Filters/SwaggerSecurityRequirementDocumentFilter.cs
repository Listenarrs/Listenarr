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
/// Adds operation-level security requirements with resolved scheme names.
/// </summary>
public sealed class SwaggerSecurityRequirementDocumentFilter : IDocumentFilter
{
    internal const string AuthenticationRequiredMetadataKey = "Listenarr.AuthenticationRequired";

    private static readonly string[] AuthenticationSecuritySchemeIds =
    [
        "SessionBearer",
        "SessionTokenHeader",
        "ApiKeyHeader",
        "ApiKeyAuthorization"
    ];

    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(swaggerDoc);

        if (swaggerDoc.Paths == null ||
            swaggerDoc.Components?.SecuritySchemes == null ||
            swaggerDoc.Components.SecuritySchemes.Count == 0)
        {
            return;
        }

        swaggerDoc.RegisterComponents();

        foreach (var pathItem in swaggerDoc.Paths.Values)
        {
            if (pathItem.Operations == null)
            {
                continue;
            }

            foreach (var operation in pathItem.Operations.Values)
            {
                if (operation.Metadata?.Remove(AuthenticationRequiredMetadataKey) != true)
                {
                    continue;
                }

                operation.Security ??= new List<OpenApiSecurityRequirement>();

                foreach (var schemeId in AuthenticationSecuritySchemeIds)
                {
                    AddSecurityRequirement(operation.Security, swaggerDoc, schemeId);
                }
            }
        }
    }

    private static void AddSecurityRequirement(
        IList<OpenApiSecurityRequirement> securityRequirements,
        OpenApiDocument swaggerDoc,
        string schemeId)
    {
        if (swaggerDoc.Components?.SecuritySchemes?.ContainsKey(schemeId) != true ||
            securityRequirements.Any(requirement => requirement.Keys.Any(reference =>
                string.Equals(reference.Reference?.Id, schemeId, StringComparison.Ordinal))))
        {
            return;
        }

        securityRequirements.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(schemeId, swaggerDoc, null)] = []
        });
    }
}
