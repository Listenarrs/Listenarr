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
using System.Security.Claims;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Security;

namespace Listenarr.Api.Middleware
{
    /// <summary>
    /// Middleware to accept an API key via header X-Api-Key and treat the request as authenticated when it matches the configured startup ApiKey.
    /// This is intentionally simple: a valid key results in a lightweight ClaimsPrincipal so downstream authorization and the AuthenticationEnforcerMiddleware
    /// will treat the request as authenticated. The key itself is stored in the startup config (config.json).
    /// </summary>
    public class ApiKeyMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IStartupConfigService _startupConfigService;
        private readonly ILogger<ApiKeyMiddleware> _logger;

        public ApiKeyMiddleware(RequestDelegate next, IStartupConfigService startupConfigService, ILogger<ApiKeyMiddleware> logger)
        {
            _next = next;
            _startupConfigService = startupConfigService;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                var cfg = _startupConfigService.GetConfig();
                var configuredKey = cfg?.ApiKey;
                if (!string.IsNullOrWhiteSpace(configuredKey))
                {
                    // Accept header X-Api-Key or Authorization: ApiKey <key>
                    string? provided = null;
                    if (context.Request.Headers.TryGetValue("X-Api-Key", out var h))
                        provided = h.ToString();

                    if (string.IsNullOrWhiteSpace(provided) && context.Request.Headers.TryGetValue("Authorization", out var auth))
                    {
                        var s = auth.ToString();
                        if (s.StartsWith("ApiKey ", StringComparison.OrdinalIgnoreCase))
                            provided = s.Substring("ApiKey ".Length).Trim();
                    }

                    // If headers didn't supply the key, only accept query-string token for SignalR hub connections.
                    // Avoiding query-string auth for normal API routes prevents credential leakage via logs/referrers.
                    if (string.IsNullOrWhiteSpace(provided))
                    {
                        try
                        {
                            var path = context.Request.Path.Value ?? string.Empty;
                            if (path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase))
                            {
                                var qs = context.Request.Query;
                                if (qs.TryGetValue("access_token", out var accessTokenValues)) provided = accessTokenValues.FirstOrDefault();
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger?.LogDebug(ex, "ApiKeyMiddleware: failed reading hub access_token from query string");
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(provided))
                    {
                        // Log a stable non-reversible fingerprint for troubleshooting without exposing key material.
                        try
                        {
                            var keyHash = SecurityRequestUtils.HashSecretForLog(provided);
                            _logger?.LogDebug("ApiKeyMiddleware: API key provided ({KeyHash})", keyHash);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            System.Diagnostics.Debug.WriteLine($"ApiKeyMiddleware key fingerprint logging failed: {ex.Message}");
                        }

                        if (provided == configuredKey)
                        {
                            // Create a minimal authenticated principal for downstream checks
                            var claims = new List<Claim>
                            {
                                new Claim(ClaimTypes.Name, "ApiKey"),
                                new Claim("AuthMethod", "ApiKey")
                            };

                            var identity = new ClaimsIdentity(claims, "ApiKey");
                            context.User = new ClaimsPrincipal(identity);

                            try
                            {
                                _logger?.LogInformation("ApiKeyMiddleware: API key accepted, principal set. PrincipalNameMask={NameMask}, ClaimsCount={ClaimsCount}", "ApiKey", context.User?.Claims?.Count() ?? 0);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                System.Diagnostics.Debug.WriteLine($"ApiKeyMiddleware accepted-key logging failed: {ex.Message}");
                            }
                        }
                        else
                        {
                            try { _logger?.LogDebug("ApiKeyMiddleware: API key provided but did not match configured key"); }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                System.Diagnostics.Debug.WriteLine($"ApiKeyMiddleware invalid-key logging failed: {ex.Message}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger?.LogWarning(ex, "ApiKeyMiddleware: failed to read startup config for API key authentication");
                // Do not fail the request if config cannot be read - just continue without API key auth
            }

            await _next(context);
        }
    }
}

