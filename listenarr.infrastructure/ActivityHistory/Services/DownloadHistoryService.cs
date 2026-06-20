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

using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.ActivityHistory.Services
{
    /// <summary>
    /// Implementation of download history service.
    /// Records all download events to provide idempotency and audit trail.
    /// </summary>
    public class DownloadHistoryService : IDownloadHistoryService
    {
        private readonly ListenArrDbContext _context;
        private readonly ILogger<DownloadHistoryService> _logger;

        public DownloadHistoryService(
            ListenArrDbContext context,
            ILogger<DownloadHistoryService> logger)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Check if a download was already imported successfully.
        /// Core idempotency check: if we have an Imported event, don't import again.
        /// </summary>
        public async Task<bool> IsAlreadyImportedAsync(string downloadId, string clientId)
        {
            if (string.IsNullOrWhiteSpace(downloadId) || string.IsNullOrWhiteSpace(clientId))
                return false;

            var normalizedId = downloadId.ToUpperInvariant();

            var imported = await _context.History
                .Where(h => h.DownloadId == normalizedId &&
                           h.DownloadClientId == clientId &&
                           h.EventType == HistoryEvents.Imported &&
                           h.Outcome == HistoryOutcome.Succeeded)
                .AnyAsync();

            if (imported)
                _logger.LogInformation(
                    "Download {DownloadId} from client {ClientId} was already imported (idempotency check)",
                    downloadId, clientId);

            return imported;
        }

        /// <summary>
        /// Check if a download was recently grabbed from a source.
        /// Validates that we're not re-importing an old download that reappeared.
        /// </summary>
        public async Task<bool> HasRecentGrabbedAsync(string downloadId, string clientId, int withinSeconds = 604800)
        {
            if (string.IsNullOrWhiteSpace(downloadId) || string.IsNullOrWhiteSpace(clientId))
                return false;

            var normalizedId = downloadId.ToUpperInvariant();
            var cutoffTime = DateTime.UtcNow.AddSeconds(-withinSeconds);

            var recentGrab = await _context.History
                .Where(h => h.DownloadId == normalizedId &&
                           h.DownloadClientId == clientId &&
                           h.EventType == HistoryEvents.Grabbed &&
                           h.Timestamp >= cutoffTime)
                .AnyAsync();

            return recentGrab;
        }

        public async Task RecordGrabbedAsync(string downloadId, string clientId, string title,
            DownloadProtocol protocol, Guid? audiobookId = null)
        {
            var history = new DownloadHistory
            {
                DownloadId = downloadId.ToUpperInvariant(),
                EventType = DownloadHistoryEventType.Grabbed,
                Status = DownloadItemStatus.Queued,
                EventDate = DateTime.UtcNow,
                AudiobookId = audiobookId,
                DownloadClient = "Unknown",
                DownloadClientId = clientId,
                Protocol = protocol,
                Title = title,
                WasImported = false
            };

            await AddUnifiedAsync(history);

            _logger.LogInformation(
                "Recorded Grabbed event for {DownloadId} ({Title}) from client {ClientId}",
                downloadId, title, clientId);
        }

        public async Task RecordDownloadCompleteAsync(string downloadId, string clientId, string title,
            string? outputPath = null)
        {
            var history = new DownloadHistory
            {
                DownloadId = downloadId.ToUpperInvariant(),
                EventType = DownloadHistoryEventType.DownloadCompleted,
                Status = DownloadItemStatus.Completed,
                EventDate = DateTime.UtcNow,
                DownloadClient = "Unknown",
                DownloadClientId = clientId,
                Protocol = DownloadProtocol.Torrent,
                Title = title,
                OutputPath = outputPath,
                WasImported = false
            };

            await AddUnifiedAsync(history);

            _logger.LogInformation(
                "Recorded DownloadCompleted event for {DownloadId} ({Title})",
                downloadId, title);
        }

        public async Task RecordDownloadFailedAsync(string downloadId, string clientId, string title,
            string? errorMessage = null)
        {
            var history = new DownloadHistory
            {
                DownloadId = downloadId.ToUpperInvariant(),
                EventType = DownloadHistoryEventType.DownloadFailed,
                Status = DownloadItemStatus.Failed,
                EventDate = DateTime.UtcNow,
                DownloadClient = "Unknown",
                DownloadClientId = clientId,
                Protocol = DownloadProtocol.Torrent,
                Title = title,
                ErrorMessage = errorMessage,
                WasImported = false
            };

            await AddUnifiedAsync(history);

            _logger.LogWarning(
                "Recorded DownloadFailed event for {DownloadId} ({Title}): {Error}",
                downloadId, title, errorMessage ?? "No error message");
        }

        public async Task RecordImportedAsync(string downloadId, string clientId, string title,
            Guid? audiobookId = null)
        {
            var history = new DownloadHistory
            {
                DownloadId = downloadId.ToUpperInvariant(),
                EventType = DownloadHistoryEventType.Imported,
                Status = DownloadItemStatus.Imported,
                EventDate = DateTime.UtcNow,
                AudiobookId = audiobookId,
                DownloadClient = "Unknown",
                DownloadClientId = clientId,
                Protocol = DownloadProtocol.Torrent,
                Title = title,
                WasImported = true,
                ImportedAt = DateTime.UtcNow
            };

            await AddUnifiedAsync(history);

            _logger.LogInformation(
                "Recorded Imported event for {DownloadId} ({Title}) audiobook {AudiobookId}",
                downloadId, title, audiobookId ?? Guid.Empty);
        }

        public async Task RecordImportFailedAsync(string downloadId, string clientId, string title,
            string? errorMessage = null)
        {
            var history = new DownloadHistory
            {
                DownloadId = downloadId.ToUpperInvariant(),
                EventType = DownloadHistoryEventType.ImportFailed,
                Status = DownloadItemStatus.ImportFailed,
                EventDate = DateTime.UtcNow,
                DownloadClient = "Unknown",
                DownloadClientId = clientId,
                Protocol = DownloadProtocol.Torrent,
                Title = title,
                ErrorMessage = errorMessage,
                WasImported = false
            };

            await AddUnifiedAsync(history);

            _logger.LogWarning(
                "Recorded ImportFailed event for {DownloadId} ({Title}): {Error}",
                downloadId, title, errorMessage ?? "No error message");
        }

        public async Task RecordPausedAsync(string downloadId, string clientId, string title)
        {
            var history = new DownloadHistory
            {
                DownloadId = downloadId.ToUpperInvariant(),
                EventType = DownloadHistoryEventType.Paused,
                Status = DownloadItemStatus.Paused,
                EventDate = DateTime.UtcNow,
                DownloadClient = "Unknown",
                DownloadClientId = clientId,
                Protocol = DownloadProtocol.Torrent,
                Title = title,
                WasImported = false
            };

            await AddUnifiedAsync(history);

            _logger.LogInformation("Recorded Paused event for {DownloadId} ({Title})", downloadId, title);
        }

        public async Task RecordResumedAsync(string downloadId, string clientId, string title)
        {
            var history = new DownloadHistory
            {
                DownloadId = downloadId.ToUpperInvariant(),
                EventType = DownloadHistoryEventType.Resumed,
                Status = DownloadItemStatus.Downloading,
                EventDate = DateTime.UtcNow,
                DownloadClient = "Unknown",
                DownloadClientId = clientId,
                Protocol = DownloadProtocol.Torrent,
                Title = title,
                WasImported = false
            };

            await AddUnifiedAsync(history);

            _logger.LogInformation("Recorded Resumed event for {DownloadId} ({Title})", downloadId, title);
        }

        public async Task RecordRemovedAsync(string downloadId, string clientId, string title)
        {
            var history = new DownloadHistory
            {
                DownloadId = downloadId.ToUpperInvariant(),
                EventType = DownloadHistoryEventType.Removed,
                Status = DownloadItemStatus.Removed,
                EventDate = DateTime.UtcNow,
                DownloadClient = "Unknown",
                DownloadClientId = clientId,
                Protocol = DownloadProtocol.Torrent,
                Title = title,
                WasImported = false
            };

            await AddUnifiedAsync(history);

            _logger.LogInformation("Recorded Removed event for {DownloadId} ({Title})", downloadId, title);
        }

        public async Task<List<DownloadHistory>> GetHistoryAsync(string downloadId, string clientId)
        {
            if (string.IsNullOrWhiteSpace(downloadId) || string.IsNullOrWhiteSpace(clientId))
                return new();

            var normalizedId = downloadId.ToUpperInvariant();

            return (await _context.History
                    .AsNoTracking()
                    .Where(h => h.DownloadId == normalizedId && h.DownloadClientId == clientId)
                    .OrderBy(h => h.Timestamp)
                    .ToListAsync())
                .Select(ToLegacy)
                .ToList();
        }

        public async Task<DownloadHistory?> GetLatestEventAsync(string downloadId, string clientId)
        {
            if (string.IsNullOrWhiteSpace(downloadId) || string.IsNullOrWhiteSpace(clientId))
                return null;

            var normalizedId = downloadId.ToUpperInvariant();

            var entry = await _context.History
                .AsNoTracking()
                .Where(h => h.DownloadId == normalizedId && h.DownloadClientId == clientId)
                .OrderByDescending(h => h.Timestamp)
                .FirstOrDefaultAsync();
            return entry == null ? null : ToLegacy(entry);
        }

        public async Task<int> CleanupOldEntriesAsync(int retentionDays = 0)
        {
            if (retentionDays < 0)
                throw new ArgumentOutOfRangeException(nameof(retentionDays), "Retention days cannot be negative.");
            if (retentionDays == 0)
            {
                _logger.LogInformation("Download history retention is unlimited; no entries were deleted");
                return 0;
            }

            var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);

            // Use ToList() first for in-memory DB compatibility, then delete
            var oldEntries = await _context.History
                .Where(h => h.DownloadId != null && h.Timestamp < cutoffDate)
                .ToListAsync();

            _context.History.RemoveRange(oldEntries);
            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "Cleaned up {Count} old download history entries (older than {Days} days)",
                oldEntries.Count, retentionDays);

            return oldEntries.Count;
        }

        private async Task AddUnifiedAsync(DownloadHistory history)
        {
            var normalizedId = history.DownloadId.ToUpperInvariant();
            _context.History.Add(new History
            {
                DownloadId = normalizedId,
                DownloadClientId = history.DownloadClientId,
                AudiobookExternalId = history.AudiobookId?.ToString(),
                SourceTitle = history.Title,
                AudiobookTitle = history.Title,
                EventType = HistoryEvents.FromDownloadEvent(history.EventType),
                Outcome = history.EventType is DownloadHistoryEventType.DownloadFailed or DownloadHistoryEventType.ImportFailed
                    ? HistoryOutcome.Failed
                    : HistoryOutcome.Succeeded,
                Timestamp = history.EventDate,
                Source = string.IsNullOrWhiteSpace(history.DownloadClient) ? "Download" : history.DownloadClient,
                Message = history.ErrorMessage,
                Error = history.ErrorMessage,
                Data = history.Data == null ? null : System.Text.Json.JsonSerializer.Serialize(history.Data),
                CorrelationId = normalizedId
            });
            await _context.SaveChangesAsync();
        }

        private static DownloadHistory ToLegacy(History history) => new()
        {
            Id = history.Id,
            DownloadId = history.DownloadId ?? string.Empty,
            DownloadClientId = history.DownloadClientId ?? string.Empty,
            DownloadClient = history.Source ?? "Unknown",
            EventType = HistoryEvents.ToDownloadEvent(history.EventType),
            Status = history.EventType == HistoryEvents.Imported
                ? DownloadItemStatus.Imported
                : history.Outcome == HistoryOutcome.Failed
                    ? DownloadItemStatus.Failed
                    : DownloadItemStatus.Unknown,
            EventDate = history.Timestamp,
            Title = history.SourceTitle ?? history.AudiobookTitle ?? string.Empty,
            ErrorMessage = history.Error,
            WasImported = history.EventType == HistoryEvents.Imported && history.Outcome == HistoryOutcome.Succeeded,
            ImportedAt = history.EventType == HistoryEvents.Imported && history.Outcome == HistoryOutcome.Succeeded
                ? history.Timestamp
                : null
        };
    }
}
