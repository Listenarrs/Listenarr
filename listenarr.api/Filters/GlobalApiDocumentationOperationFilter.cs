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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Listenarr.Api.Filters
{
    /// <summary>
    /// Adds fallback OpenAPI documentation so every endpoint has useful summaries,
    /// parameter descriptions, and response descriptions even when XML comments are missing.
    /// </summary>
    public sealed class GlobalApiDocumentationOperationFilter : IOperationFilter
    {
        private static readonly IReadOnlyDictionary<string, string> ResponseDescriptionMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["200"] = "Request completed successfully.",
                ["201"] = "Resource created successfully.",
                ["202"] = "Request accepted for asynchronous processing.",
                ["204"] = "Request completed successfully with no content returned.",
                ["400"] = "Request validation failed or was malformed.",
                ["401"] = "Authentication is required.",
                ["403"] = "Request is authenticated but not authorized.",
                ["404"] = "Requested resource was not found.",
                ["409"] = "Request conflicts with the current resource state.",
                ["422"] = "Request could not be processed due to semantic validation errors.",
                ["429"] = "Too many requests were sent in a short period of time.",
                ["500"] = "An unexpected server error occurred."
            };

        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ArgumentNullException.ThrowIfNull(context);

            var apiDescription = context.ApiDescription;
            var httpMethod = (apiDescription.HttpMethod ?? "GET").ToUpperInvariant();
            var controllerName = ResolveControllerName(apiDescription, context);
            var actionName = ResolveActionName(apiDescription, context);
            var route = "/" + (apiDescription.RelativePath ?? string.Empty).TrimStart('/');

            if (string.IsNullOrWhiteSpace(operation.Summary))
            {
                operation.Summary = BuildSummary(httpMethod, controllerName, actionName);
            }

            if (string.IsNullOrWhiteSpace(operation.Description))
            {
                operation.Description = $"Endpoint `{httpMethod} {route}`.";
            }

            if (string.IsNullOrWhiteSpace(operation.OperationId))
            {
                operation.OperationId = BuildOperationId(httpMethod, controllerName, actionName, apiDescription.RelativePath);
            }

            if (operation.Tags == null || operation.Tags.Count == 0)
            {
                operation.Tags = new HashSet<OpenApiTagReference>
                {
                    new(controllerName, null!, null)
                };
            }

            ApplyParameterDescriptions(operation, apiDescription);
            ApplyRequestBodyDescription(operation, httpMethod, route);
            ApplyResponseDescriptions(operation, httpMethod);
            ApplySecurityDocumentation(operation, context, route);
        }

        private static void ApplyParameterDescriptions(OpenApiOperation operation, ApiDescription apiDescription)
        {
            if (operation.Parameters == null || operation.Parameters.Count == 0)
            {
                return;
            }

            var apiParameters = apiDescription.ParameterDescriptions;
            foreach (var parameter in operation.Parameters)
            {
                if (!string.IsNullOrWhiteSpace(parameter.Description))
                {
                    continue;
                }

                var apiParameter = apiParameters.FirstOrDefault(p =>
                    string.Equals(p.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));

                var source = apiParameter?.Source?.DisplayName;
                var sourcePrefix = string.IsNullOrWhiteSpace(source)
                    ? "Parameter"
                    : $"{UppercaseFirst(source)} parameter";
                var requiredText = apiParameter?.IsRequired == true ? "Required." : "Optional.";
                parameter.Description = $"{sourcePrefix} `{parameter.Name}`. {requiredText}";
            }
        }

        private static void ApplyRequestBodyDescription(OpenApiOperation operation, string httpMethod, string route)
        {
            if (operation.RequestBody == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(operation.RequestBody.Description))
            {
                operation.RequestBody.Description = $"Request payload for `{httpMethod} {route}`.";
            }

            // For mutating methods, make request body requirement explicit in Swagger UI.
            if (httpMethod is "POST" or "PUT" or "PATCH")
            {
                if (operation.RequestBody is OpenApiRequestBody requestBody)
                {
                    requestBody.Required = true;
                }
            }
        }

        private static void ApplyResponseDescriptions(OpenApiOperation operation, string httpMethod)
        {
            operation.Responses ??= new OpenApiResponses();

            if (operation.Responses.Count == 0)
            {
                var defaultCode = httpMethod switch
                {
                    "POST" => "201",
                    "DELETE" => "204",
                    _ => "200"
                };
                operation.Responses[defaultCode] = new OpenApiResponse
                {
                    Description = ResolveResponseDescription(defaultCode)
                };
            }

            foreach (var response in operation.Responses.Where(r => string.IsNullOrWhiteSpace(r.Value.Description)))
            {
                response.Value.Description = ResolveResponseDescription(response.Key);
            }

            if (!operation.Responses.ContainsKey("500"))
            {
                operation.Responses["500"] = new OpenApiResponse
                {
                    Description = ResolveResponseDescription("500")
                };
            }
        }

        private static void ApplySecurityDocumentation(OpenApiOperation operation, OperationFilterContext context, string route)
        {
            if (!RequiresAuthentication(context, route))
            {
                return;
            }

            operation.Responses ??= new OpenApiResponses();
            operation.Metadata ??= new Dictionary<string, object>();
            operation.Metadata[SwaggerSecurityRequirementDocumentFilter.AuthenticationRequiredMetadataKey] = true;

            if (!operation.Responses.ContainsKey("401"))
            {
                operation.Responses["401"] = new OpenApiResponse
                {
                    Description = ResolveResponseDescription("401")
                };
            }

            if (!operation.Responses.ContainsKey("403"))
            {
                operation.Responses["403"] = new OpenApiResponse
                {
                    Description = ResolveResponseDescription("403")
                };
            }

            if (!string.IsNullOrWhiteSpace(operation.Description) &&
                !operation.Description.Contains("Authentication", StringComparison.OrdinalIgnoreCase))
            {
                operation.Description += " Authentication is required when enabled in startup configuration.";
            }
        }

        private static bool RequiresAuthentication(OperationFilterContext context, string route)
        {
            var normalizedRoute = route.Split('?', 2)[0].ToLowerInvariant();
            var normalizedApiRoute = NormalizeApiVersionedRoute(normalizedRoute);

            if (normalizedRoute.StartsWith("/swagger", StringComparison.Ordinal) ||
                normalizedApiRoute.StartsWith("/api/antiforgery", StringComparison.Ordinal) ||
                normalizedApiRoute.StartsWith("/api/account/login", StringComparison.Ordinal))
            {
                return false;
            }

            if (HasAllowAnonymous(context))
            {
                return false;
            }

            // Authentication is enforced by middleware for API/hub routes when enabled.
            return normalizedRoute.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
                   normalizedRoute.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeApiVersionedRoute(string route)
        {
            // Convert /api/v1/... (or /api/v1.0/...) -> /api/... for auth-doc checks.
            if (!route.StartsWith("/api/v", StringComparison.OrdinalIgnoreCase))
            {
                return route;
            }

            var versionStart = "/api/v".Length;
            var slashAfterVersion = route.IndexOf('/', versionStart);
            if (slashAfterVersion <= 0)
            {
                return route;
            }

            return "/api" + route[slashAfterVersion..];
        }

        private static bool HasAllowAnonymous(OperationFilterContext context) =>
            context.MethodInfo.GetCustomAttributes(true).OfType<AllowAnonymousAttribute>().Any() ||
            (context.MethodInfo.DeclaringType?.GetCustomAttributes(true).OfType<AllowAnonymousAttribute>().Any() ?? false);

        private static string ResolveResponseDescription(string statusCode) =>
            ResponseDescriptionMap.TryGetValue(statusCode, out var description)
                ? description
                : $"HTTP {statusCode} response.";

        private static string ResolveControllerName(ApiDescription apiDescription, OperationFilterContext context)
        {
            if (apiDescription.ActionDescriptor.RouteValues.TryGetValue("controller", out var controller) &&
                !string.IsNullOrWhiteSpace(controller))
            {
                return controller;
            }

            var declaringTypeName = context.MethodInfo.DeclaringType?.Name ?? "Api";
            return declaringTypeName.EndsWith("Controller", StringComparison.OrdinalIgnoreCase)
                ? declaringTypeName[..^"Controller".Length]
                : declaringTypeName;
        }

        private static string ResolveActionName(ApiDescription apiDescription, OperationFilterContext context)
        {
            if (apiDescription.ActionDescriptor.RouteValues.TryGetValue("action", out var action) &&
                !string.IsNullOrWhiteSpace(action))
            {
                return action;
            }

            return context.MethodInfo.Name;
        }

        private static string BuildSummary(string httpMethod, string controllerName, string actionName) =>
            $"{httpMethod} {controllerName} - {SplitWords(actionName)}";

        private static string BuildOperationId(string httpMethod, string controllerName, string actionName, string? relativePath)
        {
            var suffix = string.IsNullOrWhiteSpace(relativePath)
                ? string.Empty
                : "_" + NormalizeToken(relativePath);
            return $"{httpMethod}_{NormalizeToken(controllerName)}_{NormalizeToken(actionName)}{suffix}";
        }

        private static string SplitWords(string value) =>
            string.Concat(value.Select((ch, index) =>
                index > 0 && char.IsUpper(ch) && !char.IsUpper(value[index - 1])
                    ? $" {ch}"
                    : ch.ToString()));

        private static string NormalizeToken(string value)
        {
            var chars = value
                .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
                .ToArray();
            return string.Join(string.Empty, chars)
                .Trim('_')
                .Replace("__", "_", StringComparison.Ordinal);
        }

        private static string UppercaseFirst(string text) =>
            text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }
}
