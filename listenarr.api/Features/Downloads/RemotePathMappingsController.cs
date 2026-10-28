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

using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Downloads;

/// <summary>
/// API endpoints for managing remote path mappings between download clients and Listenarr.
/// Used to translate file paths when download clients are in different containers/systems.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/remotepathmappings")]
[Tags("Remote Path Mappings")]
public class RemotePathMappingsController(
    IRemotePathMappingService remotePathMappingService,
    IDownloadClientConfigurationRepository downloadClientConfigurationRepository,
    ILogger<RemotePathMappingsController> logger) : ControllerBase
{
    /// <summary>
    /// Get all remote path mappings.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<RemotePathMapping>>> GetAll()
    {
        try
        {
            var mappings = await remotePathMappingService.GetAllAsync();
            return Ok(mappings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            logger.LogError(ex, "Failed to retrieve remote path mappings");
            return StatusCode(500, new { error = "Failed to retrieve remote path mappings" });
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
            var mapping = await remotePathMappingService.GetByIdAsync(id);
            if (mapping == null)
            {
                return NotFound(new { error = $"Remote path mapping with ID {id} not found" });
            }

            return Ok(mapping);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            logger.LogError(ex, "Failed to retrieve remote path mapping {MappingId}", id);
            return StatusCode(500, new { error = "Failed to retrieve remote path mapping" });
        }
    }

    /// <summary>
    /// Get all remote path mappings for a specific download client.
    /// </summary>
    /// <param name="downloadClientId">Download client ID.</param>
    [HttpGet("client/{downloadClientId}")]
    public async Task<ActionResult<List<RemotePathMapping>>> GetByClientId(string downloadClientId)
    {
        var client = await downloadClientConfigurationRepository.GetByIdAsync(downloadClientId);
        if (client == null)
        {
            return NotFound(new { error = $"Client {downloadClientId} not found" });
        }

        try
        {
            var mappings = await remotePathMappingService.GetPathMappingByClientAsync(client);
            return Ok(mappings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            logger.LogError(ex, "Failed to retrieve remote path mappings for client {ClientId}", downloadClientId);
            return StatusCode(500, new { error = "Failed to retrieve remote path mappings" });
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

            var created = await remotePathMappingService.CreateAsync(mapping);
            return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Rejected invalid remote path mapping");
            return BadRequest(new { error = "Remote path mapping is invalid for this host." });
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            logger.LogError(ex, "Failed to create remote path mapping");
            return StatusCode(500, new { error = "Failed to create remote path mapping" });
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

            var updated = await remotePathMappingService.UpdateAsync(mapping);
            return Ok(updated);
        }
        catch (KeyNotFoundException ex)
        {
            logger.LogInformation(ex, "Remote path mapping {MappingId} was not found during update", id);
            return NotFound(new { error = $"Remote path mapping with ID {id} not found" });
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Rejected invalid remote path mapping {MappingId}", id);
            return BadRequest(new { error = "Remote path mapping is invalid for this host." });
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            logger.LogError(ex, "Failed to update remote path mapping {MappingId}", id);
            return StatusCode(500, new { error = "Failed to update remote path mapping" });
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
            var deleted = await remotePathMappingService.DeleteAsync(id);
            if (!deleted)
            {
                return NotFound(new { error = $"Remote path mapping with ID {id} not found" });
            }

            return NoContent();
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            logger.LogError(ex, "Failed to delete remote path mapping {MappingId}", id);
            return StatusCode(500, new { error = "Failed to delete remote path mapping" });
        }
    }

    /// <summary>
    /// Translate a remote path to a local path for a specific download client using configured mappings.
    /// </summary>
    [HttpPost("translate")]
    public async Task<ActionResult<object>> TranslatePath([FromBody] TranslatePathRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RemotePath))
        {
            return BadRequest(new { error = "RemotePath is required" });
        }

        if (string.IsNullOrWhiteSpace(request.DownloadClientId))
        {
            return BadRequest(new { error = "DownloadClientId is required" });
        }

        var client = await downloadClientConfigurationRepository.GetByIdAsync(request.DownloadClientId);
        if (client == null)
        {
            return NotFound(new { error = $"Client {request.DownloadClientId} not found" });
        }

        try
        {
            var localPath = await remotePathMappingService.TranslatePathAsync(client, request.RemotePath);

            return Ok(new
            {
                downloadClientId = request.DownloadClientId,
                remotePath = request.RemotePath,
                localPath,
                translated = string.Compare(localPath, request.RemotePath) != 0
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            logger.LogError(ex, "Failed to translate path");
            return StatusCode(500, new { error = "Failed to translate path" });
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
