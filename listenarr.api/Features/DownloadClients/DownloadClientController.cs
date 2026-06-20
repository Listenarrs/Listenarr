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
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.DownloadClients
{
    [ApiController]
    [Route("api/v{version:apiVersion}/download-clients")]
    [RequireAdminOrApiKey]
    [Tags("Download Clients")]
    public class DownloadClientController : ControllerBase
    {
        private readonly IConfigurationService _configurationService;
        private readonly IDownloadClientGateway _downloadClientGateway;
        private readonly ILogger<DownloadClientController> _logger;

        public DownloadClientController(
            IConfigurationService configurationService,
            IDownloadClientGateway downloadClientGateway,
            ILogger<DownloadClientController> logger)
        {
            _configurationService = configurationService;
            _downloadClientGateway = downloadClientGateway;
            _logger = logger;
        }

        /// <summary>
        /// Get all download client configurations.
        /// </summary>
        [HttpGet]
        [ProducesResponseType(typeof(List<DownloadClientConfiguration>), 200)]
        [ProducesResponseType(500)]
        public async Task<ActionResult<List<DownloadClientConfiguration>>> GetDownloadClientConfigurations()
        {
            try
            {
                var configs = await _configurationService.GetDownloadClientConfigurationsAsync();
                var redactSecrets = HttpSecurityRequestUtils.ShouldRedactSecretsForCaller(HttpContext);
                var response = configs
                    .Select(c => redactSecrets ? ApiResponseRedactor.RedactDownloadClientConfiguration(c) : c)
                    .Select(ApiResponseRedactor.ToDownloadClientSummaryResponse)
                    .ToList();

                return Ok(response);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error retrieving download client configurations");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Get a specific download client configuration by ID.
        /// </summary>
        /// <param name="id">Download client configuration ID</param>
        [HttpGet("{id}")]
        [ProducesResponseType(typeof(DownloadClientConfiguration), 200)]
        [ProducesResponseType(404)]
        [ProducesResponseType(500)]
        public async Task<ActionResult<DownloadClientConfiguration>> GetDownloadClientConfiguration(string id)
        {
            try
            {
                var config = await _configurationService.GetDownloadClientConfigurationAsync(id);
                if (config == null)
                {
                    return NotFound();
                }

                var responseConfig = HttpSecurityRequestUtils.ShouldRedactSecretsForCaller(HttpContext)
                    ? ApiResponseRedactor.RedactDownloadClientConfiguration(config)
                    : config;
                var response = ApiResponseRedactor.ToDownloadClientDetailResponse(responseConfig);

                return Ok(response);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error retrieving download client configuration {Id}", id);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Save or update a download client configuration. Preserves existing credentials when incoming values are blank.
        /// </summary>
        /// <param name="config">Download client configuration to save.</param>
        [HttpPost]
        public async Task<ActionResult<object>> SaveDownloadClientConfiguration([FromBody] DownloadClientConfiguration config)
        {
            try
            {
                if (config == null)
                {
                    return BadRequest("Missing download client configuration");
                }

                if (!string.IsNullOrWhiteSpace(config.Id))
                {
                    var existing = await _configurationService.GetDownloadClientConfigurationAsync(config.Id);
                    if (existing != null)
                    {
                        if (string.IsNullOrWhiteSpace(config.Username) && !string.IsNullOrWhiteSpace(existing.Username))
                        {
                            config.Username = existing.Username;
                        }

                        if (string.IsNullOrWhiteSpace(config.Password) && !string.IsNullOrWhiteSpace(existing.Password))
                        {
                            config.Password = existing.Password;
                        }

                        try
                        {
                            if (existing.Settings != null)
                            {
                                if (!config.Settings?.ContainsKey("apiKey") ?? true)
                                {
                                    if (existing.Settings.TryGetValue("apiKey", out var existingApiKeyObj))
                                    {
                                        var existingApiKey = existingApiKeyObj?.ToString();
                                        if (!string.IsNullOrWhiteSpace(existingApiKey))
                                        {
                                            config.Settings ??= new Dictionary<string, object>();
                                            config.Settings["apiKey"] = existingApiKey;
                                        }
                                    }
                                }
                                else if (config.Settings != null && config.Settings.TryGetValue("apiKey", out var incomingApiKeyObj))
                                {
                                    var incomingApiKey = incomingApiKeyObj?.ToString();
                                    if (string.IsNullOrWhiteSpace(incomingApiKey) && existing.Settings.TryGetValue("apiKey", out var exKey))
                                    {
                                        var existingApiKey = exKey?.ToString();
                                        if (!string.IsNullOrWhiteSpace(existingApiKey))
                                        {
                                            config.Settings["apiKey"] = existingApiKey;
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
                        {
                            _logger.LogDebug(caughtEx, "Unable to preserve download client API key settings while saving configuration {Id}", config.Id);
                        }
                    }
                }

                var id = await _configurationService.SaveDownloadClientConfigurationAsync(config);
                return Ok(new { id });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error saving download client configuration");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Delete a download client configuration by ID.
        /// </summary>
        /// <param name="id">Download client configuration ID.</param>
        [HttpDelete("{id}")]
        public async Task<ActionResult<bool>> DeleteDownloadClientConfiguration(string id)
        {
            try
            {
                var deleted = await _configurationService.DeleteDownloadClientConfigurationAsync(id);
                return Ok(deleted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error deleting download client configuration {Id}", id);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Test connectivity to a download client. Send the full configuration so credentials can be included in the test.
        /// </summary>
        /// <param name="config">Download client configuration to test.</param>
        /// <returns>Success flag, message, and the (optionally redacted) client configuration.</returns>
        [HttpPost("test")]
        public async Task<ActionResult<object>> TestDownloadClientConfiguration([FromBody] DownloadClientConfiguration config)
        {
            try
            {
                if (config == null)
                {
                    return BadRequest(new { success = false, message = "Missing download client configuration" });
                }

                if (!string.IsNullOrWhiteSpace(config.Id))
                {
                    var existing = await _configurationService.GetDownloadClientConfigurationAsync(config.Id);
                    if (existing != null)
                    {
                        if (string.IsNullOrWhiteSpace(config.Username) && !string.IsNullOrWhiteSpace(existing.Username))
                        {
                            config.Username = existing.Username;
                        }

                        if (string.IsNullOrWhiteSpace(config.Password) && !string.IsNullOrWhiteSpace(existing.Password))
                        {
                            config.Password = existing.Password;
                        }

                        try
                        {
                            if (existing.Settings != null)
                            {
                                config.Settings ??= new Dictionary<string, object>();

                                foreach (var kvp in existing.Settings.Where(kvp => !config.Settings.ContainsKey(kvp.Key)))
                                {
                                    config.Settings[kvp.Key] = kvp.Value;
                                }
                            }
                        }
                        catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
                        {
                            _logger.LogDebug(caughtEx, "Unable to merge existing download client settings while testing configuration {Id}", config.Id);
                        }
                    }
                }

                var (success, message) = await _downloadClientGateway.TestConnectionAsync(config);
                var clientResponse = config;
                if (HttpSecurityRequestUtils.ShouldRedactSecretsForCaller(HttpContext))
                {
                    clientResponse = ApiResponseRedactor.RedactDownloadClientConfiguration(clientResponse);
                }
                return Ok(new { success, message, client = clientResponse });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error testing download client configuration");
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }
    }
}
