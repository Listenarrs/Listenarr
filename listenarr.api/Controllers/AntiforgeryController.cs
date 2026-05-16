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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Controllers
{
    [ApiController]
    [Route("api/v{version:apiVersion}/antiforgery")]
    [Tags("Account")]
    public class AntiforgeryController : ControllerBase
    {
        private readonly IAntiforgery _antiforgery;
        private readonly ILogger<AntiforgeryController> _logger;

        public AntiforgeryController(IAntiforgery antiforgery, ILogger<AntiforgeryController> logger)
        {
            _antiforgery = antiforgery;
            _logger = logger;
        }

        /// <summary>
        /// Issue a new CSRF antiforgery token for the current session.
        /// </summary>
        /// <remarks>The token must be sent as a request header on subsequent state-changing requests.</remarks>
        /// <returns>An object containing the antiforgery request token.</returns>
        [HttpGet("token")]
        [AllowAnonymous]
        public IActionResult GetToken()
        {
            // Log minimal diagnostics about the principal we see when issuing a token.
            try
            {
                var user = HttpContext.User;
                var isAuthenticated = user?.Identity?.IsAuthenticated ?? false;
                string? nameMask = null;
                if (isAuthenticated)
                {
                    var name = user?.Identity?.Name ?? user?.FindFirst("sub")?.Value ?? user?.FindFirst("name")?.Value;
                    if (!string.IsNullOrEmpty(name))
                    {
                        nameMask = name.Length <= 8 ? name : name.Substring(0, 8);
                    }
                }
                var hasAuthorizationHeader = !string.IsNullOrWhiteSpace(HttpContext.Request.Headers["Authorization"].FirstOrDefault());
                var cookieNames = string.Join(',', HttpContext.Request.Cookies.Keys);
                var afCookie = HttpContext.Request.Cookies.FirstOrDefault(kv => kv.Key?.StartsWith(".AspNetCore.Antiforgery") == true);
                var cookiePrefix = afCookie.Value != null && afCookie.Value.Length > 8 ? afCookie.Value.Substring(0, 8) : afCookie.Value;
                _logger.LogInformation("Issuing antiforgery token. Authenticated={Authenticated}, NameMask={NameMask}, ClaimsCount={ClaimsCount}, HasAuthorizationHeader={HasAuthorizationHeader}, CookieNames={CookieNames}, AntiforgeryCookiePrefix={CookiePrefix}", isAuthenticated, nameMask, user?.Claims?.Count() ?? 0, hasAuthorizationHeader, cookieNames, cookiePrefix);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                // Non-fatal diagnostic logging
                _logger.LogDebug(ex, "Failed to log antiforgery principal details");
            }

            var tokens = _antiforgery.GetAndStoreTokens(HttpContext);
            return Ok(new { token = tokens.RequestToken });
        }
    }
}

