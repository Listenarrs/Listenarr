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
using System.Text.Json;
using Listenarr.Api.Extensions;
using Microsoft.OpenApi;
using Xunit;

namespace Listenarr.Tests.Features.Api.Extensions
{
    public sealed class SwaggerSecurityRequirementDocumentFilterTests
    {
        [Fact]
        public void Apply_AddsResolvedSecurityRequirements_WhenOperationRequiresAuthentication()
        {
            var swaggerDoc = BuildDocument();
            var pathItem = Assert.IsType<OpenApiPathItem>(swaggerDoc.Paths["/probe"]);
            var operation = pathItem.Operations[HttpMethod.Get];
            operation.Metadata ??= new Dictionary<string, object>();
            operation.Metadata[SwaggerSecurityRequirementDocumentFilter.AuthenticationRequiredMetadataKey] = true;

            var filter = new SwaggerSecurityRequirementDocumentFilter();
            filter.Apply(swaggerDoc, null!);

            var security = GetOperationSecurity(Serialize(swaggerDoc));
            var schemeNames = security
                .EnumerateArray()
                .Select(requirement => Assert.Single(requirement.EnumerateObject()).Name)
                .ToArray();

            Assert.Equal(
                ["SessionBearer", "SessionTokenHeader", "ApiKeyHeader", "ApiKeyAuthorization"],
                schemeNames);
        }

        private static OpenApiDocument BuildDocument()
        {
            var responses = new OpenApiResponses
            {
                ["200"] = new OpenApiResponse { Description = "OK" }
            };

            var pathItem = new OpenApiPathItem
            {
                Operations = new Dictionary<HttpMethod, OpenApiOperation>
                {
                    [HttpMethod.Get] = new()
                    {
                        Responses = responses
                    }
                }
            };

            var paths = new OpenApiPaths();
            paths.Add("/probe", pathItem);

            return new OpenApiDocument
            {
                Info = new OpenApiInfo
                {
                    Title = "Probe",
                    Version = "v1"
                },
                Components = new OpenApiComponents
                {
                    SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
                    {
                        ["SessionBearer"] = new OpenApiSecurityScheme
                        {
                            Type = SecuritySchemeType.Http,
                            Scheme = "bearer"
                        },
                        ["SessionTokenHeader"] = new OpenApiSecurityScheme
                        {
                            Type = SecuritySchemeType.ApiKey,
                            In = ParameterLocation.Header,
                            Name = "X-Session-Token"
                        },
                        ["ApiKeyHeader"] = new OpenApiSecurityScheme
                        {
                            Type = SecuritySchemeType.ApiKey,
                            In = ParameterLocation.Header,
                            Name = "X-Api-Key"
                        },
                        ["ApiKeyAuthorization"] = new OpenApiSecurityScheme
                        {
                            Type = SecuritySchemeType.ApiKey,
                            In = ParameterLocation.Header,
                            Name = "Authorization"
                        }
                    }
                },
                Paths = paths
            };
        }

        private static string Serialize(OpenApiDocument swaggerDoc)
        {
            using var textWriter = new StringWriter();
            var writer = new OpenApiJsonWriter(textWriter);
            swaggerDoc.SerializeAs(OpenApiSpecVersion.OpenApi3_0, writer);
            return textWriter.ToString();
        }

        private static JsonElement GetOperationSecurity(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement
                .GetProperty("paths")
                .GetProperty("/probe")
                .GetProperty("get")
                .GetProperty("security")
                .Clone();
        }
    }
}
