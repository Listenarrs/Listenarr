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

using System.Text.RegularExpressions;
using Listenarr.Api.Attributes;
using Listenarr.Api.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Configuration
{
    [ApiController]
    [Route("api/v{version:apiVersion}/configuration")]
    [RequireAdminOrApiKey]
    public partial class StartupConfigurationController : ControllerBase
    {
        private readonly IConfigurationService _configurationService;
        private readonly IStartupConfigService _startupConfigService;
        private readonly ILogger<StartupConfigurationController> _logger;

        public StartupConfigurationController(
            IConfigurationService configurationService,
            IStartupConfigService startupConfigService,
            ILogger<StartupConfigurationController> logger)
        {
            _configurationService = configurationService;
            _startupConfigService = startupConfigService;
            _logger = logger;
        }

        /// <summary>
        /// Get the public bootstrap configuration used by the SPA.
        /// </summary>
        /// <returns>Safe startup fields needed before authentication, such as auth mode and API version.</returns>
        [Tags("Settings")]
        [HttpGet("bootstrap")]
        [AllowAnonymous]
        [ProducesResponseType(typeof(StartupConfigDto), 200)]
        [ProducesResponseType(500)]
        public ActionResult<StartupConfigDto> GetBootstrapConfig()
        {
            return Ok(StartupConfigDto.FromStartupConfig(
                _startupConfigService.IsAuthenticationRequired(),
                _startupConfigService.GetEffectiveApiVersion(GetRequestedApiVersion())));
        }

        /// <summary>
        /// Get the full Listenarr startup configuration (API key, authentication, etc).
        /// </summary>
        /// <returns>StartupConfig object</returns>
        [Tags("Settings")]
        [HttpGet("startupconfig")]
        [ProducesResponseType(typeof(StartupConfig), 200)]
        [ProducesResponseType(401)]
        [ProducesResponseType(403)]
        [ProducesResponseType(500)]
        public async Task<ActionResult<StartupConfig>> GetStartupConfig()
        {
            var config = await _configurationService.GetStartupConfigAsync() ?? new StartupConfig();
            config.ApiVersion = NormalizeStartupApiVersion(config.ApiVersion);
            if (HttpSecurityRequestUtils.ShouldRedactSecretsForCaller(HttpContext))
            {
                config = ApiResponseRedactor.RedactStartupConfig(config);
            }

            return Ok(config);
        }

        /// <summary>
        /// Save the Listenarr startup configuration (API key, authentication, etc).
        /// </summary>
        /// <param name="config">StartupConfig object to save</param>
        /// <returns>The saved StartupConfig</returns>
        [Tags("Settings")]
        [HttpPost("startupconfig")]
        [ProducesResponseType(typeof(StartupConfig), 200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(401)]
        [ProducesResponseType(403)]
        [ProducesResponseType(500)]
        public async Task<ActionResult<StartupConfig>> SaveStartupConfig([FromBody] StartupConfig config)
        {
            if (!IsValidUrlBase(config.UrlBase))
            {
                _logger.LogWarning("Rejected a startup config whose UrlBase is a full URL rather than a path.");
                return BadRequest(new { error = InvalidUrlBaseMessage });
            }

            config.ApiVersion = NormalizeStartupApiVersion(config.ApiVersion);
            await _configurationService.SaveStartupConfigAsync(config);
            var savedConfig = await _configurationService.GetStartupConfigAsync();
            if (savedConfig == null)
            {
                return Ok(new StartupConfig());
            }

            savedConfig.ApiVersion = NormalizeStartupApiVersion(savedConfig.ApiVersion);
            if (HttpSecurityRequestUtils.ShouldRedactSecretsForCaller(HttpContext))
            {
                return Ok(ApiResponseRedactor.RedactStartupConfig(savedConfig));
            }

            return Ok(savedConfig);
        }

        internal const string InvalidUrlBaseMessage = "Must be a valid URL path (ie: '/listenarr')";

        /// <summary>
        /// A full URL in <c>UrlBase</c> is stored happily and then ignored at startup, because the
        /// path base is a path. Sonarr, Radarr and Readarr all refuse it at the controller with
        /// <c>ValidUrlBase</c>; this is the same rule and the same message.
        /// </summary>
        internal static bool IsValidUrlBase(string? urlBase)
        {
            if (string.IsNullOrWhiteSpace(urlBase))
            {
                return true;
            }

            return !AbsoluteUrlBase().IsMatch(urlBase.Trim());
        }

        [GeneratedRegex(@"^/?https?://[-_a-z0-9.]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
        private static partial Regex AbsoluteUrlBase();

        private string NormalizeStartupApiVersion(string? configuredApiVersion)
            => _startupConfigService.NormalizeApiVersion(configuredApiVersion, GetRequestedApiVersion());

        private string? GetRequestedApiVersion()
        {
            try
            {
                if (RouteData?.Values?.TryGetValue("version", out var versionObj) is true)
                {
                    var value = versionObj?.ToString();
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Failed to read requested API version from route data.");
            }

            return null;
        }
    }
}
