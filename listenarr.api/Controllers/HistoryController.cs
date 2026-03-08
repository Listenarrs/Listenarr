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
using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;

namespace Listenarr.Api.Controllers
{
    [ApiController]
    [Route("api/v{version:apiVersion}/history")]
    [Tags("History")]
    public class HistoryController : ControllerBase
    {
        private readonly ListenArrDbContext _dbContext;
        private readonly ILogger<HistoryController> _logger;

        public HistoryController(ListenArrDbContext dbContext, ILogger<HistoryController> logger)
        {
            _dbContext = dbContext;
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
            var query = _dbContext.History
                .OrderByDescending(h => h.Timestamp)
                .AsQueryable();

            if (offset.HasValue)
            {
                query = query.Skip(offset.Value);
            }

            if (limit.HasValue)
            {
                query = query.Take(limit.Value);
            }

            var history = await query.ToListAsync();
            var total = await _dbContext.History.CountAsync();

            return Ok(new
            {
                history,
                total,
                limit = limit ?? total,
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
            var history = await _dbContext.History
                .Where(h => h.AudiobookId == audiobookId)
                .OrderByDescending(h => h.Timestamp)
                .ToListAsync();

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
            var query = _dbContext.History
                .Where(h => h.EventType == eventType)
                .OrderByDescending(h => h.Timestamp)
                .AsQueryable();

            if (limit.HasValue)
            {
                query = query.Take(limit.Value);
            }

            var history = await query.ToListAsync();

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
            var query = _dbContext.History
                .Where(h => h.Source == source)
                .OrderByDescending(h => h.Timestamp)
                .AsQueryable();

            if (limit.HasValue)
            {
                query = query.Take(limit.Value);
            }

            var history = await query.ToListAsync();

            return Ok(history);
        }

        /// <summary>
        /// Get the most recent history entries.
        /// </summary>
        /// <param name="limit">Number of recent entries to return (default 50).</param>
        [HttpGet("recent")]
        public async Task<IActionResult> GetRecent([FromQuery] int limit = 50)
        {
            var history = await _dbContext.History
                .OrderByDescending(h => h.Timestamp)
                .Take(limit)
                .ToListAsync();

            return Ok(history);
        }

        /// <summary>
        /// Delete a single history entry.
        /// </summary>
        /// <param name="id">History entry ID.</param>
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            var historyEntry = await _dbContext.History.FindAsync(id);
            if (historyEntry == null)
            {
                return NotFound(new { message = "History entry not found" });
            }

            _dbContext.History.Remove(historyEntry);
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Deleted history entry ID {Id}", id);

            return Ok(new { message = "History entry deleted successfully", id });
        }

        /// <summary>
        /// Delete all history entries.
        /// </summary>
        [HttpDelete("clear")]
        public async Task<IActionResult> ClearAll()
        {
            var count = await _dbContext.History.CountAsync();
            _dbContext.History.RemoveRange(_dbContext.History);
            await _dbContext.SaveChangesAsync();

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
            var oldEntries = await _dbContext.History
                .Where(h => h.Timestamp < cutoffDate)
                .ToListAsync();

            _dbContext.History.RemoveRange(oldEntries);
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Cleaned up {Count} history entries older than {Days} days",
                oldEntries.Count, days);

            return Ok(new
            {
                message = $"Cleaned up history entries older than {days} days",
                deletedCount = oldEntries.Count
            });
        }
    }
}

