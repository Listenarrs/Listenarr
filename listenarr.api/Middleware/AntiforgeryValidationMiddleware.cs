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
using Microsoft.AspNetCore.Antiforgery;
using System.Text.RegularExpressions;

namespace Listenarr.Api.Middleware
{
    public class AntiforgeryValidationMiddleware
    {
        private static readonly Regex VersionedIndexerOrSystemPathRegex = new(
            @"^/api/v\d+(?:\.\d+)?/(indexer|system)(?:/|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly RequestDelegate _next;
        private readonly IAntiforgery _antiforgery;
        private readonly Microsoft.Extensions.Logging.ILogger<AntiforgeryValidationMiddleware> _logger;

        public AntiforgeryValidationMiddleware(RequestDelegate next, IAntiforgery antiforgery, Microsoft.Extensions.Logging.ILogger<AntiforgeryValidationMiddleware> logger)
        {
            _next = next;
            _antiforgery = antiforgery;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var method = context.Request.Method;
            var path = context.Request.Path.Value ?? string.Empty;
            var normalizedApiPath = NormalizeApiVersionedPath(path);
            _logger?.LogDebug("AntiforgeryMiddleware: incoming request {Method} {Path}", method, path);

            // Only validate for unsafe HTTP methods
            if (HttpMethods.IsPost(context.Request.Method)
                || HttpMethods.IsPut(context.Request.Method)
                || HttpMethods.IsDelete(context.Request.Method)
                || HttpMethods.IsPatch(context.Request.Method))
            {
                _logger?.LogDebug("AntiforgeryMiddleware: request method is considered unsafe; checking endpoint metadata and path whitelist");
                var endpoint = context.GetEndpoint();
                // Skip if endpoint allows anonymous access
                if (endpoint?.Metadata?.GetMetadata<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>() != null)
                {
                    _logger?.LogDebug("AntiforgeryMiddleware: endpoint allows anonymous, skipping antiforgery validation");
                    await _next(context);
                    return;
                }

                // Skip if endpoint explicitly opts out of antiforgery via IgnoreAntiforgeryToken
                // Accept either the MVC attribute or the antiforgery attribute type if present.
                if (endpoint?.Metadata?.GetMetadata<Microsoft.AspNetCore.Mvc.IgnoreAntiforgeryTokenAttribute>() != null)
                {
                    _logger?.LogDebug("AntiforgeryMiddleware: endpoint requests antiforgery be ignored, skipping antiforgery validation");
                    await _next(context);
                    return;
                }

                // Skip antiforgery for requests authenticated via API key — these are programmatic
                // (machine-to-machine) requests that cannot carry a browser cookie/token pair.
                if (context.User?.Identity?.IsAuthenticated == true
                    && context.User.FindFirst("AuthMethod")?.Value == "ApiKey")
                {
                    _logger?.LogDebug("AntiforgeryMiddleware: request authenticated via API key, skipping antiforgery validation");
                    await _next(context);
                    return;
                }

                // Allow public token issuance and explicit machine-compat routes to bypass CSRF.
                if (normalizedApiPath.StartsWith("/api/antiforgery", StringComparison.OrdinalIgnoreCase)
                    || normalizedApiPath.StartsWith("/api/account/login", StringComparison.OrdinalIgnoreCase)
                    || normalizedApiPath.StartsWith("/api/account/logout", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase)
                    // Also allow Prowlarr-compatible indexer endpoints and system status
                    || IsVersionedIndexerOrSystemPath(path))
                {
                    _logger?.LogDebug("AntiforgeryMiddleware: path is whitelisted, skipping antiforgery validation");
                    await _next(context);
                    return;
                }

                try
                {
                    await _antiforgery.ValidateRequestAsync(context);
                }
                catch (AntiforgeryValidationException ex)
                {
                    // Log safe debug info to help diagnose antiforgery mismatches during development.
                    try
                    {
                        var hdr = context.Request.Headers["X-XSRF-TOKEN"].FirstOrDefault();
                        var hdrLen = hdr?.Length ?? 0;
                        var cookieNames = string.Join(',', context.Request.Cookies.Keys);
                        // Try to capture the antiforgery cookie value prefix (masked) for diagnosis.
                        string? cookiePrefix = null;
                        try
                        {
                            var afCookie = context.Request.Cookies.FirstOrDefault(kv => kv.Key?.StartsWith(".AspNetCore.Antiforgery") == true);
                            var cookieVal = afCookie.Value;
                            if (!string.IsNullOrEmpty(cookieVal))
                            {
                                cookiePrefix = cookieVal.Length <= 8 ? cookieVal : cookieVal.Substring(0, 8);
                            }
                        }
                        catch (Exception cookieEx) when (cookieEx is not OperationCanceledException && cookieEx is not OutOfMemoryException && cookieEx is not StackOverflowException)
                        {
                            _logger?.LogDebug(cookieEx, "Failed reading antiforgery cookie prefix for diagnostics");
                        }

                        var headerPrefix = string.Empty;
                        if (!string.IsNullOrEmpty(hdr))
                        {
                            headerPrefix = hdr.Length <= 8 ? hdr : hdr.Substring(0, 8);
                        }

                        var equalPrefixes = string.Equals(headerPrefix, cookiePrefix, System.StringComparison.Ordinal);

                        // Capture a masked view of the current principal for diagnostics
                        bool principalAuthenticated = false;
                        string? principalNameMask = null;
                        int principalClaims = 0;
                        var hasAuthorizationHeader = !string.IsNullOrWhiteSpace(context.Request.Headers["Authorization"].FirstOrDefault());
                        try
                        {
                            var user = context.User;
                            principalAuthenticated = user?.Identity?.IsAuthenticated ?? false;
                            principalClaims = user?.Claims?.Count() ?? 0;
                            if (principalAuthenticated)
                            {
                                var pname = user?.Identity?.Name ?? user?.FindFirst("sub")?.Value ?? user?.FindFirst("name")?.Value;
                                if (!string.IsNullOrEmpty(pname)) principalNameMask = pname.Length <= 8 ? pname : pname.Substring(0, 8);
                            }
                        }
                        catch (Exception ex2) when (ex2 is not OperationCanceledException && ex2 is not OutOfMemoryException && ex2 is not StackOverflowException)
                        {
                            _logger?.LogDebug(ex2, "Failed capturing principal diagnostics during antiforgery validation failure");
                        }

                        _logger?.LogWarning(ex, "Antiforgery validation failed. Method={Method}, Path={Path}, HeaderLength={HeaderLength}, CookieNames={CookieNames}, HeaderPrefix={HeaderPrefix}, CookiePrefix={CookiePrefix}, PrefixesEqual={PrefixesEqual}, PrincipalAuthenticated={PrincipalAuthenticated}, PrincipalNameMask={PrincipalNameMask}, PrincipalClaims={PrincipalClaims}, HasAuthorizationHeader={HasAuthorizationHeader}", method, path, hdrLen, cookieNames, headerPrefix, cookiePrefix, equalPrefixes, principalAuthenticated, principalNameMask, principalClaims, hasAuthorizationHeader);
                    }
                    catch (Exception logEx) when (logEx is not OperationCanceledException && logEx is not OutOfMemoryException && logEx is not StackOverflowException)
                    {
                        System.Diagnostics.Debug.WriteLine($"AntiforgeryValidationMiddleware logging failed: {logEx.Message}");
                    }

                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsJsonAsync(new { message = "Invalid or missing CSRF token" });
                    return;
                }
            }

            await _next(context);
        }

        private static string NormalizeApiVersionedPath(string path)
        {
            // Convert /api/v1/... (or /api/v1.0/...) -> /api/... for legacy path checks.
            if (!path.StartsWith("/api/v", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            var versionStart = "/api/v".Length;
            var slashAfterVersion = path.IndexOf('/', versionStart);
            if (slashAfterVersion <= 0)
            {
                return path;
            }

            return "/api" + path[slashAfterVersion..];
        }

        private static bool IsVersionedIndexerOrSystemPath(string path)
            => !string.IsNullOrWhiteSpace(path) && VersionedIndexerOrSystemPathRegex.IsMatch(path);
    }
}
