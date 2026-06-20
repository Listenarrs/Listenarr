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

namespace Listenarr.Api.Features.Configuration
{
    [ApiController]
    [Route("api/v{version:apiVersion}/configuration")]
    [RequireAdminOrApiKey]
    public class ApiKeyController : ControllerBase
    {
        private readonly IConfigurationService _configurationService;
        private readonly IUserService _userService;
        private readonly ILogger<ApiKeyController> _logger;

        public ApiKeyController(
            IConfigurationService configurationService,
            IUserService userService,
            ILogger<ApiKeyController> logger)
        {
            _configurationService = configurationService;
            _userService = userService;
            _logger = logger;
        }

        /// <summary>
        /// Get the current server API key.
        /// </summary>
        /// <returns>The configured API key.</returns>
        [Tags("Security")]
        [HttpGet("apikey")]
        [RequireApiKeyManagementAccess]
        [ProducesResponseType(typeof(object), 200)]
        [ProducesResponseType(401)]
        [ProducesResponseType(403)]
        [ProducesResponseType(500)]
        public async Task<ActionResult<object>> GetApiKey()
        {
            var cfg = await _configurationService.GetStartupConfigAsync() ?? new StartupConfig();
            return Ok(new { apiKey = cfg.ApiKey ?? string.Empty });
        }

        /// <summary>
        /// Regenerate the API key.
        /// </summary>
        /// <returns>The newly generated API key.</returns>
        [Tags("Security")]
        [HttpPost("apikey/regenerate")]
        [RequireApiKeyManagementAccess]
        public async Task<ActionResult<object>> RegenerateApiKey()
        {
            var cfg = await _configurationService.GetStartupConfigAsync();
            var current = cfg ?? new StartupConfig();
            var bytes = new byte[32];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            var newKey = Convert.ToBase64String(bytes).TrimEnd('=');
            current.ApiKey = newKey;
            await _configurationService.SaveStartupConfigAsync(current);
            return Ok(new { apiKey = newKey });
        }

        /// <summary>
        /// Generate an API key during initial setup. Only available from localhost when no users or API key exist.
        /// </summary>
        /// <returns>The newly generated API key.</returns>
        /// <response code="200">API key generated successfully.</response>
        /// <response code="403">Request is not from localhost.</response>
        /// <response code="409">Users or an API key already exist.</response>
        [Tags("Security")]
        [HttpPost("apikey/generate-initial")]
        [AllowAnonymous]
        public async Task<ActionResult<object>> GenerateInitialApiKey()
        {
            try
            {
                var remoteIp = HttpContext?.Connection?.RemoteIpAddress;
                if (remoteIp == null || !System.Net.IPAddress.IsLoopback(remoteIp))
                {
                    return StatusCode(403, new { message = "Initial API key generation is only allowed from localhost" });
                }

                var current = await _configurationService.GetStartupConfigAsync();
                if (current == null)
                {
                    return StatusCode(500, "Unable to load startup configuration");
                }

                var hasUsers = await _userService.GetUsersCountAsync() > 0;
                if (hasUsers || !string.IsNullOrWhiteSpace(current.ApiKey))
                {
                    return StatusCode(409, new { message = "Initial API key generation is only allowed before users and API key are configured" });
                }

                var bytes = new byte[32];
                using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                {
                    rng.GetBytes(bytes);
                }

                var newKey = Convert.ToBase64String(bytes).TrimEnd('=');
                current.ApiKey = newKey;
                await _configurationService.SaveStartupConfigAsync(current);
                return Ok(new { apiKey = newKey });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error generating initial API key");
                return StatusCode(500, "Internal server error");
            }
        }
    }
}
