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

namespace Listenarr.Api.Features.Search
{
    public partial class SearchController
    {
        /// <summary>
        /// Search for audiobook series by name using the Audible catalog provider.
        /// </summary>
        /// <param name="name">Series name to search for.</param>
        /// <param name="region">Audible marketplace region (default: us).</param>
        [HttpGet("audible/series")]
        [ProducesResponseType(typeof(List<AudibleSeriesSearchItem>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<List<AudibleSeriesSearchItem>>> SearchAudibleSeries([FromQuery] string name, [FromQuery] string region = "us")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name)) return BadRequest("name query parameter is required");
                var res = await _audibleService.SearchSeriesByNameAsync(name, region);
                if (res == null) return NotFound();

                // SearchSeriesByNameAsync is declared as object? for historical reasons but always
                // yields SeriesLookupItem values; project them so callers get a stable contract
                // rather than having to reverse-engineer the provider's shape.
                if (res is not IEnumerable<SeriesLookupItem> items)
                {
                    _logger.LogWarning(
                        "Audible series search returned an unexpected shape ({Type}) for name {Name}",
                        res.GetType().Name,
                        LogRedaction.SanitizeText(name));
                    return Ok(new List<AudibleSeriesSearchItem>());
                }

                // Shape is unchanged (a bare array); only the element type is now declared.
                return Ok(items
                    .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                    .Select(item => new AudibleSeriesSearchItem
                    {
                        Asin = item.Asin,
                        Name = item.Name!,
                        Region = item.Region,
                        Description = item.Description,
                        Image = item.Image
                    })
                    .ToList());
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error proxying Audible series search for name {Name}", name);
                return StatusCode(500, "Internal server error");
            }
        }
    }
}
