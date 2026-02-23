using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using System.Linq;
using System.Threading.Tasks;

namespace Listenarr.Api.Middleware
{
    public class AntiforgeryValidationMiddleware
    {
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

                // Allow some public endpoints without antiforgery (startup config reads, token request itself, login/register)
                if (path.StartsWith("/api/antiforgery") || path.StartsWith("/api/account/login") || path.StartsWith("/api/account/register") || path.StartsWith("/api/account/logout") || path.StartsWith("/api/startupconfig") || path.StartsWith("/api/configuration/startupconfig") || path.StartsWith("/hubs/")
                    // Also allow Prowlarr-compatible indexer endpoints and system status
                    || path.StartsWith("/api/v1/indexer") || path.StartsWith("/api/v1/system"))
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
                        catch { /* ignore cookie read errors */ }

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
                        string? sessionToken = context.Request.Headers["Authorization"].FirstOrDefault();
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
                        catch { }

                        _logger?.LogWarning(ex, "Antiforgery validation failed. Method={Method}, Path={Path}, HeaderLength={HeaderLength}, CookieNames={CookieNames}, HeaderPrefix={HeaderPrefix}, CookiePrefix={CookiePrefix}, PrefixesEqual={PrefixesEqual}, PrincipalAuthenticated={PrincipalAuthenticated}, PrincipalNameMask={PrincipalNameMask}, PrincipalClaims={PrincipalClaims}, SessionToken={SessionToken}", method, path, hdrLen, cookieNames, headerPrefix, cookiePrefix, equalPrefixes, principalAuthenticated, principalNameMask, principalClaims, sessionToken);
                    }
                    catch { /* ignore logging errors */ }

                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsJsonAsync(new { message = "Invalid or missing CSRF token" });
                    return;
                }
            }

            await _next(context);
        }
    }
}
