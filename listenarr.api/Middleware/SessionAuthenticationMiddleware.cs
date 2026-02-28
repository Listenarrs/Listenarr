/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2025 Robbie Davis
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

using Listenarr.Api.Services;
using System.Security.Claims;

namespace Listenarr.Api.Middleware
{
    public class SessionAuthenticationMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<SessionAuthenticationMiddleware> _logger;

        public SessionAuthenticationMiddleware(RequestDelegate next, ILogger<SessionAuthenticationMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context, ISessionService sessionService)
        {
            // Only process session authentication if no user is already authenticated
            var isAlreadyAuthenticated = context.User.Identity?.IsAuthenticated ?? false;
            var path = context.Request.Path.Value ?? string.Empty;
            if (!isAlreadyAuthenticated)
            {
                var sessionToken = ExtractSessionToken(context);
                if (!string.IsNullOrEmpty(sessionToken))
                {
                    var tokenHash = SecurityRequestUtils.HashSecretForLog(sessionToken);
                    _logger.LogDebug("[SessionAuth] Incoming session token for {Path} ({TokenHash})", path, tokenHash);
                    try
                    {
                        var principal = await sessionService.GetSessionUserAsync(sessionToken);
                        if (principal != null)
                        {
                            context.User = principal;
                            _logger.LogDebug("[SessionAuth] Session authentication successful for {Path} ({TokenHash})", path, tokenHash);
                        }
                        else
                        {
                            _logger.LogDebug("[SessionAuth] Session token invalid or expired for {Path} ({TokenHash})", path, tokenHash);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                        _logger.LogError(ex, "[SessionAuth] Error during session authentication for {Path}", path);
                    }
                }
                else if (path.Contains("startupconfig"))
                {
                    _logger.LogDebug("[SessionAuth] No session token found for {Path}. HeaderCount={HeaderCount}", path, context.Request.Headers.Count);
                }
            }
            else if (path.Contains("startupconfig"))
            {
                _logger.LogDebug("[SessionAuth] User already authenticated for {Path}. Identity: {Identity}, Name: {Name}", path, context.User.Identity?.AuthenticationType, context.User.Identity?.Name);
            }

            await _next(context);
        }

        private static string? ExtractSessionToken(HttpContext context)
        {
            // Try Authorization header first (Bearer token)
            var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return authHeader[7..]; // Remove "Bearer " prefix
            }

            // Try X-Session-Token header
            var sessionHeader = context.Request.Headers["X-Session-Token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(sessionHeader))
            {
                return sessionHeader;
            }

            // For WebSocket (SignalR) connections browsers can't send custom headers on the
            // initial upgrade request. Accept query token only for hub endpoints.
            try
            {
                var path = context.Request.Path.Value ?? string.Empty;
                if (path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase))
                {
                    var qs = context.Request.Query;
                    if (qs.TryGetValue("access_token", out var accessTokenValues))
                    {
                        var provided = accessTokenValues.FirstOrDefault();
                        if (!string.IsNullOrEmpty(provided)) return provided;
                    }
                }
            }
            catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) {
                // ignore any query-parsing issues and fall through to null
                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }

            return null;
        }
    }
}

