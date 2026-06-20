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

namespace Listenarr.Api.Features.ActivityHistory
{
    [ApiController]
    [Route("api/v{version:apiVersion}/history")]
    [Tags("History")]
    public class HistoryController : ControllerBase
    {
        private readonly IHistoryRepository _history;
        private readonly ILogger<HistoryController> _logger;
        private readonly IConfigurationService _configuration;

        public HistoryController(
            IHistoryRepository history,
            IConfigurationService configuration,
            ILogger<HistoryController> logger)
        {
            _history = history;
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// Get all history entries, ordered by most recent first.
        /// </summary>
        /// <param name="limit">Maximum number of entries to return.</param>
        /// <param name="offset">Number of entries to skip for pagination.</param>
        /// <param name="sortBy">Sortable field: timestamp, eventType, outcome, or source.</param>
        /// <param name="sortDirection">Sort direction: asc or desc.</param>
        /// <param name="eventType">Optional event-type filter.</param>
        /// <param name="outcome">Optional outcome filter.</param>
        /// <param name="from">Optional inclusive UTC start date.</param>
        /// <param name="to">Optional inclusive UTC end date.</param>
        /// <param name="audiobookId">Optional audiobook filter.</param>
        /// <param name="downloadId">Optional download filter.</param>
        /// <param name="downloadClientId">Optional download-client filter.</param>
        /// <param name="correlationId">Optional workflow-correlation filter.</param>
        [HttpGet]
        public async Task<IActionResult> GetAll(
            [FromQuery] int limit = 50,
            [FromQuery] int offset = 0,
            [FromQuery] string sortBy = "timestamp",
            [FromQuery] string sortDirection = "desc",
            [FromQuery] string? eventType = null,
            [FromQuery] HistoryOutcome? outcome = null,
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null,
            [FromQuery] int? audiobookId = null,
            [FromQuery] string? downloadId = null,
            [FromQuery] string? downloadClientId = null,
            [FromQuery] string? correlationId = null)
        {
            var page = await _history.QueryAsync(new HistoryQuery
            {
                Limit = limit,
                Offset = offset,
                SortBy = sortBy,
                SortDirection = sortDirection,
                EventType = eventType,
                Outcome = outcome,
                From = from,
                To = to,
                AudiobookId = audiobookId,
                DownloadId = downloadId,
                DownloadClientId = downloadClientId,
                CorrelationId = correlationId
            });

            return Ok(new
            {
                history = page.Records,
                total = page.Total,
                limit = page.Limit,
                offset = page.Offset
            });
        }

        /// <summary>
        /// Get one history event and every attempt in the same workflow.
        /// </summary>
        [HttpGet("{id:int}/details")]
        public async Task<IActionResult> GetDetails(int id)
        {
            var entry = await _history.GetByIdAsync(id);
            if (entry == null) return NotFound(new { message = "History entry not found" });

            var related = await _history.GetByCorrelationIdAsync(entry.CorrelationId);
            return Ok(new { entry, related });
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
        [RequireAdministratorSession]
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
        [RequireAdministratorSession]
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
        /// <param name="days">Optional age threshold. When omitted, the configured retention is used; zero means unlimited.</param>
        [HttpDelete("cleanup")]
        [RequireAdministratorSession]
        public async Task<IActionResult> CleanupOld([FromQuery] int? days = null)
        {
            var retentionDays = days ?? (await _configuration.GetApplicationSettingsAsync()).HistoryRetentionDays;
            if (retentionDays == 0)
            {
                return Ok(new { message = "History retention is unlimited; no entries were deleted", deletedCount = 0 });
            }
            if (retentionDays < 0)
            {
                return BadRequest(new { message = "days cannot be negative; retention value 0 means unlimited" });
            }
            var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);
            var deletedCount = await _history.DeleteOlderThanAsync(cutoffDate);

            _logger.LogInformation("Cleaned up {Count} history entries older than {Days} days",
                deletedCount, retentionDays);

            return Ok(new
            {
                message = $"Cleaned up history entries older than {retentionDays} days",
                deletedCount
            });
        }
    }
}
