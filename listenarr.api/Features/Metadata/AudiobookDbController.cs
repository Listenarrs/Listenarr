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

namespace Listenarr.Api.Features.Metadata
{
    /// <summary>
    /// Surfaces alternate narrated Releases of a book from AudiobookDB, keyed off an Audible
    /// ASIN the book has already been matched to. Used by the Add/Edit screens to let the user
    /// pick a specific narrator's edition when more than one exists - e.g. a book's original
    /// self-published narration vs. a later official re-recording that Audible's own catalog
    /// no longer carries.
    /// </summary>
    [ApiController]
    [Route("api/v{version:apiVersion}/audiobookdb")]
    [Tags("Metadata")]
    public class AudiobookDbController : ControllerBase
    {
        private readonly IAudiobookDbService _audiobookDbService;

        public AudiobookDbController(IAudiobookDbService audiobookDbService)
        {
            _audiobookDbService = audiobookDbService;
        }

        /// <summary>
        /// Get every known Release (distinct narrated recording) of the book matching the given
        /// Audible ASIN, as cataloged by AudiobookDB.
        /// </summary>
        /// <param name="asin">Audible ASIN already associated with the book.</param>
        [HttpGet("releases/{asin}")]
        [ProducesResponseType(typeof(AudiobookDbBook), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<AudiobookDbBook>> GetReleases(string asin)
        {
            // AudiobookDbService itself catches and logs failures, returning null rather than
            // throwing, so there is no exception path left to handle at this layer.
            var book = await _audiobookDbService.GetBookByAudibleAsinAsync(asin);
            if (book == null)
            {
                return NotFound(new { message = "No AudiobookDB entry found for this ASIN" });
            }

            return Ok(book);
        }
    }
}
