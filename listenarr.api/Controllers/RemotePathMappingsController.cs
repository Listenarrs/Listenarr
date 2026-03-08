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

using Microsoft.AspNetCore.Mvc;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;

namespace Listenarr.Api.Controllers;

/// <summary>
/// API endpoints for managing remote path mappings between download clients and Listenarr.
/// Used to translate file paths when download clients are in different containers/systems.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/remotepathmappings")]
[Tags("Remote Path Mappings")]
public class RemotePathMappingsController : ControllerBase
{
    private readonly IRemotePathMappingService _mappingService;
    private readonly ILogger<RemotePathMappingsController> _logger;

    public RemotePathMappingsController(
        IRemotePathMappingService mappingService,
        ILogger<RemotePathMappingsController> logger)
    {
        _mappingService = mappingService;
        _logger = logger;
    }

    /// <summary>
    /// Get all remote path mappings.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<RemotePathMapping>>> GetAll()
    {
        try
        {
            var mappings = await _mappingService.GetAllAsync();
            return Ok(mappings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
            _logger.LogError(ex, "Failed to retrieve remote path mappings");
            return StatusCode(500, new { error = "Failed to retrieve remote path mappings", details = ex.Message });
        }
    }

    /// <summary>
    /// Get a specific remote path mapping by ID.
    /// </summary>
    /// <param name="id">Mapping ID.</param>
    [HttpGet("{id}")]
    public async Task<ActionResult<RemotePathMapping>> GetById(int id)
    {
        try
        {
            var mapping = await _mappingService.GetByIdAsync(id);
            if (mapping == null)
            {
                return NotFound(new { error = $"Remote path mapping with ID {id} not found" });
            }

            return Ok(mapping);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
            _logger.LogError(ex, "Failed to retrieve remote path mapping {MappingId}", id);
            return StatusCode(500, new { error = "Failed to retrieve remote path mapping", details = ex.Message });
        }
    }

    /// <summary>
    /// Get all remote path mappings for a specific download client.
    /// </summary>
    /// <param name="downloadClientId">Download client ID.</param>
    [HttpGet("client/{downloadClientId}")]
    public async Task<ActionResult<List<RemotePathMapping>>> GetByClientId(string downloadClientId)
    {
        try
        {
            var mappings = await _mappingService.GetByClientIdAsync(downloadClientId);
            return Ok(mappings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
            _logger.LogError(ex, "Failed to retrieve remote path mappings for client {ClientId}", downloadClientId);
            return StatusCode(500, new { error = "Failed to retrieve remote path mappings", details = ex.Message });
        }
    }

    /// <summary>
    /// Create a new remote path mapping.
    /// </summary>
    /// <param name="mapping">Mapping to create with download client ID, remote path, and local path.</param>
    [HttpPost]
    public async Task<ActionResult<RemotePathMapping>> Create([FromBody] RemotePathMapping mapping)
    {
        try
        {
            // Validate required fields
            if (string.IsNullOrWhiteSpace(mapping.DownloadClientId))
            {
                return BadRequest(new { error = "DownloadClientId is required" });
            }

            if (string.IsNullOrWhiteSpace(mapping.RemotePath))
            {
                return BadRequest(new { error = "RemotePath is required" });
            }

            if (string.IsNullOrWhiteSpace(mapping.LocalPath))
            {
                return BadRequest(new { error = "LocalPath is required" });
            }

            var created = await _mappingService.CreateAsync(mapping);
            return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
            _logger.LogError(ex, "Failed to create remote path mapping");
            return StatusCode(500, new { error = "Failed to create remote path mapping", details = ex.Message });
        }
    }

    /// <summary>
    /// Update an existing remote path mapping.
    /// </summary>
    /// <param name="id">Mapping ID.</param>
    /// <param name="mapping">Updated mapping data.</param>
    [HttpPut("{id}")]
    public async Task<ActionResult<RemotePathMapping>> Update(int id, [FromBody] RemotePathMapping mapping)
    {
        try
        {
            if (id != mapping.Id)
            {
                return BadRequest(new { error = "ID in URL does not match ID in body" });
            }

            // Validate required fields
            if (string.IsNullOrWhiteSpace(mapping.DownloadClientId))
            {
                return BadRequest(new { error = "DownloadClientId is required" });
            }

            if (string.IsNullOrWhiteSpace(mapping.RemotePath))
            {
                return BadRequest(new { error = "RemotePath is required" });
            }

            if (string.IsNullOrWhiteSpace(mapping.LocalPath))
            {
                return BadRequest(new { error = "LocalPath is required" });
            }

            var updated = await _mappingService.UpdateAsync(mapping);
            return Ok(updated);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
            _logger.LogError(ex, "Failed to update remote path mapping {MappingId}", id);
            return StatusCode(500, new { error = "Failed to update remote path mapping", details = ex.Message });
        }
    }

    /// <summary>
    /// Delete a remote path mapping.
    /// </summary>
    /// <param name="id">Mapping ID.</param>
    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(int id)
    {
        try
        {
            var deleted = await _mappingService.DeleteAsync(id);
            if (!deleted)
            {
                return NotFound(new { error = $"Remote path mapping with ID {id} not found" });
            }

            return NoContent();
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
            _logger.LogError(ex, "Failed to delete remote path mapping {MappingId}", id);
            return StatusCode(500, new { error = "Failed to delete remote path mapping", details = ex.Message });
        }
    }

    /// <summary>
    /// Translate a remote path to a local path for a specific download client using configured mappings.
    /// </summary>
    [HttpPost("translate")]
    public async Task<ActionResult<object>> TranslatePath([FromBody] TranslatePathRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.DownloadClientId))
            {
                return BadRequest(new { error = "DownloadClientId is required" });
            }

            if (string.IsNullOrWhiteSpace(request.RemotePath))
            {
                return BadRequest(new { error = "RemotePath is required" });
            }

            var localPath = await _mappingService.TranslatePathAsync(request.DownloadClientId, request.RemotePath);
            var requiresTranslation = await _mappingService.RequiresTranslationAsync(request.DownloadClientId, request.RemotePath);

            return Ok(new
            {
                downloadClientId = request.DownloadClientId,
                remotePath = request.RemotePath,
                localPath,
                translated = requiresTranslation
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
            _logger.LogError(ex, "Failed to translate path");
            return StatusCode(500, new { error = "Failed to translate path", details = ex.Message });
        }
    }

    /// <summary>
    /// Request model for path translation
    /// </summary>
    public class TranslatePathRequest
    {
        public string DownloadClientId { get; set; } = string.Empty;
        public string RemotePath { get; set; } = string.Empty;
    }
}


