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

namespace Listenarr.Api.Features.Configuration
{
    [ApiController]
    [Route("api/v{version:apiVersion}/configuration")]
    [RequireAdminOrApiKey]
    public class ApiSourcesController : ControllerBase
    {
        private readonly IConfigurationService _configurationService;
        private readonly ILogger<ApiSourcesController> _logger;

        public ApiSourcesController(
            IConfigurationService configurationService,
            ILogger<ApiSourcesController> logger)
        {
            _configurationService = configurationService;
            _logger = logger;
        }

        /// <summary>
        /// Get all API configurations.
        /// </summary>
        [Tags("API Sources")]
        [HttpGet("apis")]
        [ProducesResponseType(typeof(List<ApiConfiguration>), 200)]
        [ProducesResponseType(500)]
        public async Task<ActionResult<List<ApiConfiguration>>> GetApiConfigurations()
        {
            try
            {
                var configs = await _configurationService.GetApiConfigurationsAsync();
                if (HttpSecurityRequestUtils.ShouldRedactSecretsForCaller(HttpContext))
                {
                    configs = configs.Select(ApiResponseRedactor.RedactApiConfiguration).ToList();
                }
                return Ok(configs);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error retrieving API configurations");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Get a specific API configuration by ID.
        /// </summary>
        /// <param name="id">API configuration ID</param>
        [Tags("API Sources")]
        [HttpGet("apis/{id}")]
        [ProducesResponseType(typeof(ApiConfiguration), 200)]
        [ProducesResponseType(404)]
        [ProducesResponseType(500)]
        public async Task<ActionResult<ApiConfiguration>> GetApiConfiguration(string id)
        {
            try
            {
                var config = await _configurationService.GetApiConfigurationAsync(id);
                if (config == null)
                {
                    return NotFound();
                }
                if (HttpSecurityRequestUtils.ShouldRedactSecretsForCaller(HttpContext))
                {
                    return Ok(ApiResponseRedactor.RedactApiConfiguration(config));
                }

                return Ok(config);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error retrieving API configuration {Id}", id);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Save an API configuration.
        /// </summary>
        /// <param name="config">API configuration to save</param>
        [Tags("API Sources")]
        [HttpPost("apis")]
        [ProducesResponseType(typeof(object), 200)]
        [ProducesResponseType(500)]
        public async Task<ActionResult<object>> SaveApiConfiguration([FromBody] ApiConfiguration config)
        {
            try
            {
                var id = await _configurationService.SaveApiConfigurationAsync(config);
                return Ok(new { id });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error saving API configuration");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Delete an API configuration by ID.
        /// </summary>
        /// <param name="id">API configuration ID</param>
        [Tags("API Sources")]
        [HttpDelete("apis/{id}")]
        [ProducesResponseType(typeof(bool), 200)]
        [ProducesResponseType(500)]
        public async Task<ActionResult<bool>> DeleteApiConfiguration(string id)
        {
            try
            {
                var deleted = await _configurationService.DeleteApiConfigurationAsync(id);
                return Ok(deleted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error deleting API configuration {Id}", id);
                return StatusCode(500, "Internal server error");
            }
        }
    }
}
