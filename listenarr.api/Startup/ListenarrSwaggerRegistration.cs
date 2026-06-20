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

using Listenarr.Api.Filters;
using Microsoft.OpenApi;
using Serilog;

namespace Listenarr.Api.Startup;

public static class ListenarrSwaggerRegistration
{
    public static IServiceCollection AddListenarrSwagger(
        this IServiceCollection services,
        IFileSystem fileSystem)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            var swaggerDescription = string.Join(Environment.NewLine, new[]
            {
                "REST API for Listenarr audiobook management and automation.",
                "Versioning: URL segment format `/api/v{version}/...` (default version: v1).",
                "",
                "Authentication quick start:",
                "1. Click `Authorize` and enter one credential (you do not need all schemes).",
                "2. Browser session flow:",
                "   - Call `POST /api/v{version}/account/login` with `{ \"username\": \"...\", \"password\": \"...\", \"rememberMe\": false }`.",
                "   - The browser stores the `listenarr_session` HttpOnly cookie automatically when `authType` is `session`.",
                "   - Subsequent browser requests authenticate with that cookie.",
                "3. API key flow:",
                "   - API keys are intended for non-browser clients such as scripts, bots, and integrations.",
                "   - Read the current key from `GET /api/v{version}/configuration/apikey` (Administrator session required when authentication is enabled; local/private-network access required when disabled).",
                "   - Rotate the key with `POST /api/v{version}/configuration/apikey/regenerate` (Administrator session required when authentication is enabled; local/private-network access required when disabled).",
                "   - `POST /api/v{version}/configuration/apikey/generate-initial` is localhost bootstrap only and typically returns 409 after setup.",
                "   - Use `ApiKeyHeader` (`<apiKey>`) or `ApiKeyAuthorization` (`ApiKey <apiKey>`)."
            });

            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Listenarr API",
                Version = "v1",
                Description = swaggerDescription
            });

            options.AddSecurityDefinition("ApiKeyHeader", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = "X-Api-Key",
                Description = string.Join(Environment.NewLine, new[]
                {
                    "Use `X-Api-Key: <apiKey>`.",
                    "API keys are auto-generated on first run.",
                    "Read the current key from `GET /api/v{version}/configuration/apikey` (Administrator session required when authentication is enabled; local/private-network access required when disabled).",
                    "Regenerate with `POST /api/v{version}/configuration/apikey/regenerate` (Administrator session required when authentication is enabled; local/private-network access required when disabled)."
                })
            });

            options.AddSecurityDefinition("ApiKeyAuthorization", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = "Authorization",
                Description = string.Join(Environment.NewLine, new[]
                {
                    "Use `Authorization: ApiKey <apiKey>`.",
                    "API keys are auto-generated on first run.",
                    "Read the current key from `GET /api/v{version}/configuration/apikey` (Administrator session required when authentication is enabled; local/private-network access required when disabled).",
                    "Regenerate with `POST /api/v{version}/configuration/apikey/regenerate` (Administrator session required when authentication is enabled; local/private-network access required when disabled)."
                })
            });

            try
            {
                var xmlFile = System.Reflection.Assembly.GetExecutingAssembly().GetName().Name + ".xml";
                var xmlPath = Path.Join(AppContext.BaseDirectory, xmlFile);
                if (fileSystem.FileExists(xmlPath))
                {
                    options.IncludeXmlComments(xmlPath);
                }
            }
            catch (Exception ex) when (
                ex is IOException
                || ex is UnauthorizedAccessException
                || ex is System.Xml.XmlException
                || ex is InvalidOperationException
                || ex is ArgumentException)
            {
                Log.Logger.Warning("[WARNING] Failed to include XML comments in Swagger: {Message}", ex.Message);
            }

            options.CustomSchemaIds(type => (type.FullName ?? type.Name).Replace('+', '.'));
            options.OperationFilter<GlobalApiDocumentationOperationFilter>();
            options.DocumentFilter<SwaggerSecurityRequirementDocumentFilter>();
            options.DocumentFilter<SwaggerTagOrderDocumentFilter>();
            options.ResolveConflictingActions(apiDescriptions => apiDescriptions.First());
            options.DocInclusionPredicate((docName, apiDescription) =>
            {
                var groupName = apiDescription.GroupName;
                if (string.IsNullOrWhiteSpace(groupName))
                {
                    return string.Equals(docName, "v1", StringComparison.OrdinalIgnoreCase);
                }

                return string.Equals(groupName, docName, StringComparison.OrdinalIgnoreCase);
            });
        });

        return services;
    }
}
