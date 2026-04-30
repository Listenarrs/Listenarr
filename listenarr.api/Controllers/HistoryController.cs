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

namespace Listenarr.Api.Controllers
{
    [ApiController]
    [Route("api/v{version:apiVersion}/history")]
    [Tags("History")]
    public class HistoryController : ControllerBase
    {
        private readonly IHistoryRepository _history;
        private readonly ILogger<HistoryController> _logger;

        public HistoryController(IHistoryRepository history, ILogger<HistoryController> logger)
        {
            _history = history;
            _logger = logger;
        }

        /// <summary>
        /// Get all history entries, ordered by most recent first.
        /// </summary>
        /// <param name="limit">Maximum number of entries to return.</param>
        /// <param name="offset">Number of entries to skip for pagination.</param>
        [HttpGet]
        public async Task<IActionResult> GetAll([FromQuery] int? limit = null, [FromQuery] int? offset = null)
        {
            var total = await _history.CountAsync();
            var pageLimit = limit ?? total;
            var history = await _history.GetPagedAsync(pageLimit, offset ?? 0);

            return Ok(new
            {
                history,
                total,
                limit = pageLimit,
                offset = offset ?? 0
            });
        }

        /// <summary>
        /// Get history entries for a specific audiobook.
        /// </summary>
        /// <param name="audiobookId">Audiobook ID to filter by.</param>
        [HttpGet("audiobook/{audiobookId}")]
        public async Task<IActionResult> GetByAudiobookId(int audiobookId)
        {
            var history = await _history.GetByAudiobookIdAsync(audiobookId);

            _logger.LogInformation("Retrieved {Count} history entries for audiobook ID {AudiobookId}",
                history.Count, audiobookId);

            return Ok(history);
        }

        /// <summary>
        /// Get history entries filtered by event type (e.g., "Downloaded", "Imported", "Deleted").
        /// </summary>
        /// <param name="eventType">Event type string to filter by.</param>
        /// <param name="limit">Maximum number of entries to return.</param>
        [HttpGet("type/{eventType}")]
        public async Task<IActionResult> GetByEventType(string eventType, [FromQuery] int? limit = null)
        {
            var history = await _history.GetByEventTypeAsync(eventType, limit);
            return Ok(history);
        }

        /// <summary>
        /// Get history entries filtered by source (e.g., indexer name).
        /// </summary>
        /// <param name="source">Source string to filter by.</param>
        /// <param name="limit">Maximum number of entries to return.</param>
        [HttpGet("source/{source}")]
        public async Task<IActionResult> GetBySource(string source, [FromQuery] int? limit = null)
        {
            var history = await _history.GetBySourceAsync(source, limit);
            return Ok(history);
        }

        /// <summary>
        /// Get the most recent history entries.
        /// </summary>
        /// <param name="limit">Number of recent entries to return (default 50).</param>
        [HttpGet("recent")]
        public async Task<IActionResult> GetRecent([FromQuery] int limit = 50)
        {
            var history = await _history.GetRecentAsync(limit);
            return Ok(history);
        }

        /// <summary>
        /// Delete a single history entry.
        /// </summary>
        /// <param name="id">History entry ID.</param>
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            var deleted = await _history.DeleteAsync(id);
            if (!deleted)
            {
                return NotFound(new { message = "History entry not found" });
            }

            _logger.LogInformation("Deleted history entry ID {Id}", id);

            return Ok(new { message = "History entry deleted successfully", id });
        }

        /// <summary>
        /// Delete all history entries.
        /// </summary>
        [HttpDelete("clear")]
        public async Task<IActionResult> ClearAll()
        {
            var count = await _history.CountAsync();
            await _history.DeleteAllAsync();

            _logger.LogInformation("Cleared all {Count} history entries", count);

            return Ok(new { message = "All history entries cleared", deletedCount = count });
        }

        /// <summary>
        /// Delete history entries older than a specified number of days.
        /// </summary>
        /// <param name="days">Age threshold in days (default 90). Entries older than this are deleted.</param>
        [HttpDelete("cleanup")]
        public async Task<IActionResult> CleanupOld([FromQuery] int days = 90)
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-days);
            var deletedCount = await _history.DeleteOlderThanAsync(cutoffDate);

            _logger.LogInformation("Cleaned up {Count} history entries older than {Days} days",
                deletedCount, days);

            return Ok(new
            {
                message = $"Cleaned up history entries older than {days} days",
                deletedCount
            });
        }
    }
}
