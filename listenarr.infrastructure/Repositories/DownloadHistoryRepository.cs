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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Repositories
{
    /// <summary>
    /// Repository for event-sourced download history.
    /// Provides audit trail and prevents duplicate downloads.
    /// </summary>
    public class DownloadHistoryRepository
    {
        private readonly ListenArrDbContext _context;

        public DownloadHistoryRepository(ListenArrDbContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary>
        /// Add a new history event
        /// </summary>
        public async Task<DownloadHistory> AddAsync(DownloadHistory history, CancellationToken ct = default)
        {
            if (history == null) throw new ArgumentNullException(nameof(history));

            _context.DownloadHistories.Add(history);
            await _context.SaveChangesAsync(ct);
            return history;
        }

        /// <summary>
        /// Get all history events for a specific download ID (torrent hash or NZB ID)
        /// Ordered by EventDate descending (most recent first)
        /// </summary>
        public async Task<List<DownloadHistory>> GetByDownloadIdAsync(string downloadId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(downloadId)) return new List<DownloadHistory>();

            var normalizedId = downloadId.ToUpperInvariant();
            return await _context.DownloadHistories
                .Where(dh => dh.DownloadId.ToUpper() == normalizedId)
                .OrderByDescending(dh => dh.EventDate)
                .ToListAsync(ct);
        }

        /// <summary>
        /// Get all history events for a specific audiobook
        /// Ordered by EventDate descending (most recent first)
        /// </summary>
        public async Task<List<DownloadHistory>> GetByAudiobookIdAsync(Guid audiobookId, CancellationToken ct = default)
        {
            return await _context.DownloadHistories
                .Where(dh => dh.AudiobookId == audiobookId)
                .OrderByDescending(dh => dh.EventDate)
                .ToListAsync(ct);
        }

        /// <summary>
        /// Get the most recent event for a specific download ID
        /// </summary>
        public async Task<DownloadHistory?> GetLatestEventAsync(string downloadId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(downloadId)) return null;

            var normalizedId = downloadId.ToUpperInvariant();
            return await _context.DownloadHistories
                .Where(dh => dh.DownloadId.ToUpper() == normalizedId)
                .OrderByDescending(dh => dh.EventDate)
                .FirstOrDefaultAsync(ct);
        }

        /// <summary>
        /// Check if a download has already been imported (prevents duplicates)
        /// This is a key pattern - check history before grabbing
        /// </summary>
        public async Task<bool> WasImportedAsync(string downloadId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(downloadId)) return false;

            return await _context.DownloadHistories
                .AnyAsync(dh => dh.DownloadId == downloadId.ToUpperInvariant() && dh.WasImported, ct);
        }

        /// <summary>
        /// Get all downloads that have been grabbed but not yet imported
        /// </summary>
        public async Task<List<DownloadHistory>> GetPendingImportsAsync(CancellationToken ct = default)
        {
            return await _context.DownloadHistories
                .Where(dh => !dh.WasImported && dh.EventType == DownloadHistoryEventType.Grabbed)
                .OrderBy(dh => dh.EventDate)
                .ToListAsync(ct);
        }

        /// <summary>
        /// Get recent history (last N events)
        /// </summary>
        public async Task<List<DownloadHistory>> GetRecentAsync(int count = 100, CancellationToken ct = default)
        {
            return await _context.DownloadHistories
                .OrderByDescending(dh => dh.EventDate)
                .Take(count)
                .ToListAsync(ct);
        }

        /// <summary>
        /// Get failed downloads within a time window
        /// </summary>
        public async Task<List<DownloadHistory>> GetFailedDownloadsAsync(DateTime since, CancellationToken ct = default)
        {
            return await _context.DownloadHistories
                .Where(dh => dh.EventType == DownloadHistoryEventType.DownloadFailed && dh.EventDate >= since)
                .OrderByDescending(dh => dh.EventDate)
                .ToListAsync(ct);
        }

        /// <summary>
        /// Mark a download as imported
        /// </summary>
        public async Task MarkAsImportedAsync(string downloadId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(downloadId)) return;

            var normalizedId = downloadId.ToUpperInvariant();
            var events = await _context.DownloadHistories
                .Where(dh => dh.DownloadId.ToUpper() == normalizedId)
                .ToListAsync(ct);

            foreach (var evt in events)
            {
                evt.WasImported = true;
                evt.ImportedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync(ct);
        }

        /// <summary>
        /// Delete old history entries (cleanup task)
        /// </summary>
        public async Task<int> DeleteOlderThanAsync(DateTime cutoffDate, CancellationToken ct = default)
        {
            var oldEntries = await _context.DownloadHistories
                .Where(dh => dh.EventDate < cutoffDate)
                .ToListAsync(ct);

            _context.DownloadHistories.RemoveRange(oldEntries);
            await _context.SaveChangesAsync(ct);

            return oldEntries.Count;
        }

        /// <summary>
        /// Get count of history entries
        /// </summary>
        public async Task<int> GetCountAsync(CancellationToken ct = default)
        {
            return await _context.DownloadHistories.CountAsync(ct);
        }
    }
}
