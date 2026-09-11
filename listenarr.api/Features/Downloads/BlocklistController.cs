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
///
/// No try/catch. NewControllerBroadCatches_AreForbiddenOutsideDocumentedLegacyControllers
/// forbids one in a controller added after the rule, and the neighbours that have them are on
/// its grandfathered list. An unexpected failure goes to the pipeline's exception handler,
/// which is also why no 5xx body here can leak an exception message.
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
        return Ok(await blocklistService.GetForAudiobookAsync(audiobookId));
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
        if (!await blocklistService.DeleteAsync(id))
        {
            return NotFound(new { error = $"Blocklist entry with ID {id} not found" });
        }

        logger.LogInformation("Removed blocklist entry {BlocklistEntryId} on request", id);
        return NoContent();
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
        var removed = await blocklistService.ClearForAudiobookAsync(audiobookId);
        logger.LogInformation(
            "Cleared {RemovedCount} blocklist entries for audiobook {AudiobookId} on request",
            removed,
            audiobookId);
        return Ok(new { audiobookId, removed });
    }
}
