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
using Listenarr.Api.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace Listenarr.Api.Features.Identity
{
    [ApiController]
    [Route("api/v{version:apiVersion}/account")]
    [Tags("Account")]
    public class AccountController : ControllerBase
    {
        private readonly IStartupConfigService _startupConfigService;
        private readonly ILogger<AccountController> _logger;
        private readonly IUserService _userService;
        private readonly ILoginRateLimiter _rateLimiter;
        private readonly ISessionService _sessionService;

        public AccountController(
            IStartupConfigService startupConfigService,
            ILogger<AccountController> logger,
            IUserService userService,
            ILoginRateLimiter rateLimiter,
            ISessionService sessionService)
        {
            _startupConfigService = startupConfigService;
            _logger = logger;
            _userService = userService;
            _rateLimiter = rateLimiter;
            _sessionService = sessionService;
        }

        /// <summary>
        /// Authenticate a user and establish a browser session cookie.
        /// </summary>
        /// <param name="req">Login credentials and optional remember-me flag.</param>
        /// <returns>Authentication mode on success, or an error message.</returns>
        /// <response code="200">Login succeeded. Returns auth type and sets a session cookie when authentication is enabled.</response>
        /// <response code="400">Username or password missing.</response>
        /// <response code="401">Invalid credentials.</response>
        /// <response code="429">Too many failed attempts. Retry after the indicated number of seconds.</response>
        [HttpPost("login")]
        [AllowAnonymous]
        public async Task<IActionResult> Login([FromBody] LoginRequest req)
        {
            // NOTE: This is a minimal demo implementation. Replace with a proper user store.
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            {
                return BadRequest(new { message = "Username and password required" });
            }

            // Rate limiter key: IP + username
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var key = $"{ip}:{req.Username}";
            if (_rateLimiter.IsBlocked(key))
            {
                var seconds = _rateLimiter.GetSecondsUntilUnblock(key);
                // Add Retry-After header (in seconds) and return 429 with remaining seconds
                Response.Headers["Retry-After"] = seconds.ToString();
                return StatusCode(429, new { message = "Too many failed login attempts, try again later", retryAfterSeconds = seconds });
            }

            // Validate against user store
            var valid = await _userService.ValidateCredentialsAsync(req.Username, req.Password);
            if (!valid)
            {
                _rateLimiter.RecordFailure(key);
                var seconds = _rateLimiter.GetSecondsUntilUnblock(key);
                if (seconds > 0)
                {
                    Response.Headers["Retry-After"] = seconds.ToString();
                    return StatusCode(429, new { message = "Too many failed login attempts, try again later", retryAfterSeconds = seconds });
                }

                return Unauthorized(new { message = "Invalid credentials" });
            }

            var user = await _userService.GetByUsernameAsync(req.Username);
            _rateLimiter.RecordSuccess(key);

            if (!_startupConfigService.IsAuthenticationRequired())
            {
                return Ok(new { message = "Logged in", authType = "none" });
            }

            var sessionToken = await _sessionService.CreateSessionAsync(req.Username, user?.IsAdmin == true, req.RememberMe);

            // Set HttpOnly session cookie so browsers can authenticate resource
            // requests (images, etc.) without JavaScript intervention.
            Response.Cookies.Append("listenarr_session", sessionToken, new CookieOptions
            {
                HttpOnly = true,
                Secure = HttpContext.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                MaxAge = req.RememberMe ? SessionService.RememberMeExpiration : SessionService.DefaultExpiration,
            });

            return Ok(new
            {
                message = "Logged in",
                authType = "session",
            });
        }

        /// <summary>
        /// End the current session and invalidate the session token.
        /// </summary>
        /// <returns>Confirmation message and the configured auth type.</returns>
        /// <response code="200">Logout succeeded.</response>
        [HttpPost("logout")]
        [AllowAnonymous]
        public async Task<IActionResult> Logout()
        {
            var username = User?.Identity?.Name ?? "Anonymous";
            var authType = User?.Identity?.AuthenticationType ?? "Unknown";

            _logger.LogInformation("Logout request received for user: {Username} (AuthType: {AuthType})", username, authType);

            try
            {
                // Extract the current session token from the request.
                var sessionToken = ExtractSessionToken(HttpContext);

                // Handle session-based authentication logout
                if (!string.IsNullOrEmpty(sessionToken))
                {
                    await _sessionService.InvalidateSessionAsync(sessionToken);
                    _logger.LogInformation("Session invalidated for token {TokenHash}", SecurityRequestUtils.HashSecretForLog(sessionToken));
                }

                // Clear the session cookie regardless of auth type
                Response.Cookies.Delete("listenarr_session", new CookieOptions
                {
                    HttpOnly = true,
                    Secure = HttpContext.Request.IsHttps,
                    SameSite = SameSiteMode.Strict,
                    Path = "/",
                });

                if (string.IsNullOrEmpty(sessionToken))
                {
                    if (User?.Identity?.AuthenticationType == "ApiKey" || username == "ApiKey")
                    {
                        // API key authentication doesn't have a server-side session to clear
                        _logger.LogInformation("API key authenticated user logged out (client should stop sending API key)");
                    }
                    else
                    {
                        _logger.LogInformation("No session token found in logout request");
                    }
                }

                var responseAuthType = _startupConfigService.IsAuthenticationRequired() ? "session" : "none";
                return Ok(new { message = "Logged out successfully", authType = responseAuthType });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error during logout for user: {Username} (AuthType: {AuthType})", username, authType);
                return StatusCode(500, new { message = "Error during logout", error = ex.Message });
            }
        }

        private static string? ExtractSessionToken(HttpContext context)
        {
            // Fall back to session cookie (set on login for browser resource requests)
            var cookieToken = context.Request.Cookies["listenarr_session"];
            if (!string.IsNullOrEmpty(cookieToken))
            {
                return cookieToken;
            }

            return null;
        }

        /// <summary>
        /// Get the current user's authentication status and identity.
        /// </summary>
        /// <returns>An object with <c>authenticated</c> flag and the user's display name.</returns>
        [HttpGet("me")]
        [AllowAnonymous]
        public ActionResult<object> Me()
        {
            if (!(User?.Identity?.IsAuthenticated ?? false))
                return Ok(new { authenticated = false });

            return Ok(new { authenticated = true, name = User?.Identity?.Name ?? string.Empty });
        }

        /// <summary>
        /// List all administrator accounts.
        /// </summary>
        /// <returns>A collection of admin user summaries (id, username, email, creation date).</returns>
        [HttpGet("admins")]
        [RequireAdminOrApiKey]
        public async Task<IActionResult> GetAdminUsers()
        {
            var admins = await _userService.GetAdminUsersAsync();
            var result = admins.Select(u => new
            {
                u.Id,
                u.Username,
                u.Email,
                u.IsAdmin,
                u.CreatedAt
            }).ToList();

            return Ok(result);
        }
    }

    /// <summary>
    /// Request payload used to authenticate a user.
    /// </summary>
    public class LoginRequest
    {
        /// <summary>
        /// Account username.
        /// </summary>
        [Required]
        [MinLength(1)]
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// Account password.
        /// </summary>
        [Required]
        [MinLength(1)]
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// Whether to issue a long-lived session token.
        /// </summary>
        public bool RememberMe { get; set; }
    }

}
