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
using Listenarr.Application.Common.Exceptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;

namespace Listenarr.Api.Features.Configuration
{
    [ApiController]
    [Route("api/v{version:apiVersion}/configuration")]
    [RequireAdminOrApiKey]
    public class SettingsController : ControllerBase
    {
        private readonly IConfigurationService _configurationService;
        private readonly ILogger<SettingsController> _logger;
        private readonly IHubBroadcaster _hubBroadcaster;
        private readonly IMemoryCache? _cache;

        public SettingsController(
            IConfigurationService configurationService,
            ILogger<SettingsController> logger,
            IHubBroadcaster hubBroadcaster,
            IMemoryCache? cache = null)
        {
            _configurationService = configurationService;
            _logger = logger;
            _hubBroadcaster = hubBroadcaster;
            _cache = cache;
        }

        /// <summary>
        /// Get the current application settings (output paths, naming patterns, webhook URLs, etc.).
        /// </summary>
        [Tags("Settings")]
        [HttpGet("settings")]
        public async Task<ActionResult<ApplicationSettings>> GetApplicationSettings()
        {
            try
            {
                var settings = PrepareApplicationSettingsResponse(await _configurationService.GetApplicationSettingsAsync());
                if (HttpSecurityRequestUtils.ShouldRedactSecretsForCaller(HttpContext))
                {
                    return Ok(ApiResponseRedactor.RedactApplicationSettings(settings));
                }

                return Ok(settings);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error retrieving application settings");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Save application settings. Broadcasts the update to all connected realtime clients.
        /// </summary>
        /// <param name="settings">Updated application settings.</param>
        [Tags("Settings")]
        [HttpPost("settings")]
        public async Task<ActionResult<ApplicationSettings>> SaveApplicationSettings([FromBody] ApplicationSettings settings)
        {
            try
            {
                _logger.LogDebug("Saving application settings");
                await _configurationService.SaveApplicationSettingsAsync(settings);
                _cache?.Remove("default-search-region");

                var savedSettings = PrepareApplicationSettingsResponse(settings);
                savedSettings.AdminUsername = null;
                savedSettings.AdminPassword = null;

                await _hubBroadcaster.BroadcastAsync(
                    RealtimeHubTarget.Settings,
                    "SettingsUpdated",
                    ApiResponseRedactor.RedactApplicationSettings(savedSettings));

                _logger.LogDebug("Application settings saved successfully and broadcasted to realtime clients");
                if (HttpSecurityRequestUtils.ShouldRedactSecretsForCaller(HttpContext))
                {
                    return Ok(ApiResponseRedactor.RedactApplicationSettings(savedSettings));
                }

                return Ok(savedSettings);
            }
            catch (ApplicationConflictException ex)
            {
                _logger.LogInformation(
                    ex,
                    "Application settings save rejected because the client version is stale or missing");
                return Conflict(new { code = ex.Code, message = ex.SafeDetail });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error saving application settings");
                return StatusCode(500, new { error = "Failed to save application settings" });
            }
        }

        private static ApplicationSettings PrepareApplicationSettingsResponse(ApplicationSettings settings)
        {
            var clone = JsonSerializer.Deserialize<ApplicationSettings>(JsonSerializer.Serialize(settings))
                ?? new ApplicationSettings();
            clone.AdminUsername = null;
            clone.AdminPassword = null;
            clone.ProwlarrApiKeyEncrypted = null;
            return clone;
        }

        /// <summary>
        /// Get the saved Prowlarr import connection metadata used by the Indexers tab.
        /// The API key itself is never returned; callers only receive whether a saved key exists.
        /// </summary>
        [Tags("Settings")]
        [HttpGet("prowlarr-import")]
        public async Task<ActionResult<ProwlarrImportConnectionSettings>> GetProwlarrImportSettings()
        {
            try
            {
                var settings = await _configurationService.GetProwlarrImportSettingsAsync();
                settings.ApiKey = null;
                return Ok(settings);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error retrieving saved Prowlarr import settings");
                return StatusCode(500, "Internal server error");
            }
        }
    }
}
