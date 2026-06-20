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

        public async Task InvokeAsync(
            HttpContext context,
            ISessionService sessionService)
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
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogError(ex, "[SessionAuth] Error during session authentication for {Path}", path);
                    }
                }
                else if (IsStartupConfigurationPath(path))
                {
                    _logger.LogDebug("[SessionAuth] No session token found for {Path}. HeaderCount={HeaderCount}", path, context.Request.Headers.Count);
                }
            }
            else if (IsStartupConfigurationPath(path))
            {
                _logger.LogDebug("[SessionAuth] User already authenticated for {Path}. Identity: {Identity}, Name: {Name}", path, context.User.Identity?.AuthenticationType, context.User.Identity?.Name);
            }

            await _next(context);
        }

        private static string? ExtractSessionToken(HttpContext context)
        {
            // Fall back to session cookie for browser-initiated resource requests
            // (images, stylesheets, etc.) that cannot attach custom headers.
            var cookieToken = context.Request.Cookies["listenarr_session"];
            if (!string.IsNullOrEmpty(cookieToken))
            {
                return cookieToken;
            }

            return null;
        }

        private static bool IsStartupConfigurationPath(string path)
            => path.Contains("/configuration/startupconfig", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/configuration/bootstrap", StringComparison.OrdinalIgnoreCase);
    }
}
