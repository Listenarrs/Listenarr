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
/// The way back out of the release blocklist.
///
/// An entry is written whenever a download the client accepted then failed, and a blocked
/// release is never grabbed for that book again. Some of the failures that write one are not
/// the release's fault: a download client that ran out of disk, a tracker that was down, a
/// torrent the user removed by hand. Without a delete, one of those bans a release for good,
/// so these three endpoints exist before the feature ships rather than after somebody asks
/// for them.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/blocklist")]
[Tags("Blocklist")]
public class BlocklistController(
    IBlocklistService blocklistService,
    ILogger<BlocklistController> logger) : ControllerBase
{
    /// <summary>
    /// The releases currently blocked for one audiobook, newest first.
    /// </summary>
    /// <param name="audiobookId">Audiobook ID.</param>
    [HttpGet("audiobook/{audiobookId}")]
    [ProducesResponseType(typeof(IReadOnlyList<BlockedRelease>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<BlockedRelease>>> GetForAudiobook(int audiobookId)
    {
        try
        {
            return Ok(await blocklistService.GetForAudiobookAsync(audiobookId));
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            logger.LogError(ex, "Failed to list blocked releases for audiobook {AudiobookId}", audiobookId);
            return StatusCode(500, new { error = "Failed to list blocked releases" });
        }
    }

    /// <summary>
    /// Remove one blocklist entry, so the release it names can be grabbed again.
    /// </summary>
    /// <param name="id">Blocklist entry ID.</param>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Delete(int id)
    {
        try
        {
            if (!await blocklistService.DeleteAsync(id))
            {
                return NotFound(new { error = $"Blocklist entry with ID {id} not found" });
            }

            return NoContent();
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            logger.LogError(ex, "Failed to remove blocklist entry {BlocklistEntryId}", id);
            return StatusCode(500, new { error = "Failed to remove blocklist entry" });
        }
    }

    /// <summary>
    /// Remove every blocklist entry for one audiobook.
    /// </summary>
    /// <param name="audiobookId">Audiobook ID.</param>
    /// <returns>How many entries were removed. Zero when the book had none, which is not an error.</returns>
    [HttpDelete("audiobook/{audiobookId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> ClearForAudiobook(int audiobookId)
    {
        try
        {
            var removed = await blocklistService.ClearForAudiobookAsync(audiobookId);
            return Ok(new { audiobookId, removed });
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            logger.LogError(ex, "Failed to clear the blocklist for audiobook {AudiobookId}", audiobookId);
            return StatusCode(500, new { error = "Failed to clear the blocklist" });
        }
    }
}
