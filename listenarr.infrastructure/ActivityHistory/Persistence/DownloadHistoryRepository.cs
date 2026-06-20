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

namespace Listenarr.Infrastructure.ActivityHistory.Persistence
{
    /// <summary>
    /// Repository for event-sourced download history.
    /// Provides audit trail and prevents duplicate downloads.
    /// </summary>
    public class DownloadHistoryRepository : IDownloadHistoryRepository
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

            var entry = ToUnifiedHistory(history);
            _context.History.Add(entry);
            await _context.SaveChangesAsync(ct);
            history.Id = entry.Id;
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
            return (await _context.History
                    .AsNoTracking()
                    .Where(h => h.DownloadId != null && h.DownloadId.ToUpper() == normalizedId)
                    .OrderByDescending(h => h.Timestamp)
                    .ToListAsync(ct))
                .Select(ToLegacyDownloadHistory)
                .ToList();
        }

        /// <summary>
        /// Get all history events for a specific audiobook
        /// Ordered by EventDate descending (most recent first)
        /// </summary>
        public async Task<List<DownloadHistory>> GetByAudiobookIdAsync(Guid audiobookId, CancellationToken ct = default)
        {
            var externalId = audiobookId.ToString();
            return (await _context.History
                    .AsNoTracking()
                    .Where(h => h.AudiobookExternalId == externalId)
                    .OrderByDescending(h => h.Timestamp)
                    .ToListAsync(ct))
                .Select(ToLegacyDownloadHistory)
                .ToList();
        }

        /// <summary>
        /// Get the most recent event for a specific download ID
        /// </summary>
        public async Task<DownloadHistory?> GetLatestEventAsync(string downloadId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(downloadId)) return null;

            var normalizedId = downloadId.ToUpperInvariant();
            var entry = await _context.History
                .AsNoTracking()
                .Where(h => h.DownloadId != null && h.DownloadId.ToUpper() == normalizedId)
                .OrderByDescending(h => h.Timestamp)
                .FirstOrDefaultAsync(ct);
            return entry == null ? null : ToLegacyDownloadHistory(entry);
        }

        /// <summary>
        /// Check if a download has already been imported (prevents duplicates)
        /// This is a key pattern - check history before grabbing
        /// </summary>
        public async Task<bool> WasImportedAsync(string downloadId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(downloadId)) return false;

            return await _context.History
                .AnyAsync(h => h.DownloadId != null &&
                               h.DownloadId.ToUpper() == downloadId.ToUpperInvariant() &&
                               h.EventType == HistoryEvents.Imported &&
                               h.Outcome == HistoryOutcome.Succeeded, ct);
        }

        /// <summary>
        /// Get all downloads that have been grabbed but not yet imported
        /// </summary>
        public async Task<List<DownloadHistory>> GetPendingImportsAsync(CancellationToken ct = default)
        {
            var importedIds = _context.History
                .Where(h => h.EventType == HistoryEvents.Imported && h.Outcome == HistoryOutcome.Succeeded)
                .Select(h => h.DownloadId);
            return (await _context.History
                    .AsNoTracking()
                    .Where(h => h.EventType == HistoryEvents.Grabbed && !importedIds.Contains(h.DownloadId))
                    .OrderBy(h => h.Timestamp)
                    .ToListAsync(ct))
                .Select(ToLegacyDownloadHistory)
                .ToList();
        }

        /// <summary>
        /// Get recent history (last N events)
        /// </summary>
        public async Task<List<DownloadHistory>> GetRecentAsync(int count = 100, CancellationToken ct = default)
        {
            return (await _context.History
                    .AsNoTracking()
                    .Where(h => h.DownloadId != null)
                    .OrderByDescending(h => h.Timestamp)
                    .Take(count)
                    .ToListAsync(ct))
                .Select(ToLegacyDownloadHistory)
                .ToList();
        }

        /// <summary>
        /// Get failed downloads within a time window
        /// </summary>
        public async Task<List<DownloadHistory>> GetFailedDownloadsAsync(DateTime since, CancellationToken ct = default)
        {
            return (await _context.History
                    .AsNoTracking()
                    .Where(h => h.EventType == HistoryEvents.DownloadFailed && h.Timestamp >= since)
                    .OrderByDescending(h => h.Timestamp)
                    .ToListAsync(ct))
                .Select(ToLegacyDownloadHistory)
                .ToList();
        }

        /// <summary>
        /// Mark a download as imported
        /// </summary>
        public async Task MarkAsImportedAsync(string downloadId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(downloadId)) return;

            var normalizedId = downloadId.ToUpperInvariant();
            var latest = await _context.History
                .AsNoTracking()
                .Where(h => h.DownloadId != null && h.DownloadId.ToUpper() == normalizedId)
                .OrderByDescending(h => h.Timestamp)
                .FirstOrDefaultAsync(ct);
            _context.History.Add(new History
            {
                DownloadId = normalizedId,
                DownloadClientId = latest?.DownloadClientId,
                AudiobookId = latest?.AudiobookId,
                AudiobookExternalId = latest?.AudiobookExternalId,
                AudiobookTitle = latest?.AudiobookTitle,
                SourceTitle = latest?.SourceTitle,
                EventType = HistoryEvents.Imported,
                Outcome = HistoryOutcome.Succeeded,
                Source = "DownloadHistoryCompatibility",
                Message = "Download imported",
                Timestamp = DateTime.UtcNow,
                CorrelationId = latest?.CorrelationId ?? Guid.NewGuid().ToString("N")
            });
            await _context.SaveChangesAsync(ct);
        }

        /// <summary>
        /// Delete old history entries (cleanup task)
        /// </summary>
        public async Task<int> DeleteOlderThanAsync(DateTime cutoffDate, CancellationToken ct = default)
        {
            var oldEntries = await _context.History
                .Where(h => h.DownloadId != null && h.Timestamp < cutoffDate)
                .ToListAsync(ct);

            _context.History.RemoveRange(oldEntries);
            await _context.SaveChangesAsync(ct);

            return oldEntries.Count;
        }

        /// <summary>
        /// Get count of history entries
        /// </summary>
        public async Task<int> GetCountAsync(CancellationToken ct = default)
        {
            return await _context.History.CountAsync(h => h.DownloadId != null, ct);
        }

        private static History ToUnifiedHistory(DownloadHistory history)
        {
            var normalizedId = history.DownloadId.ToUpperInvariant();
            return new History
            {
                DownloadId = history.DownloadId,
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
                CorrelationId = GetCorrelationId(history.Data, normalizedId)
            };
        }

        private static DownloadHistory ToLegacyDownloadHistory(History history)
        {
            Dictionary<string, object>? data = null;
            if (!string.IsNullOrWhiteSpace(history.Data))
            {
                try
                {
                    data = NormalizeData(System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(history.Data));
                }
                catch (System.Text.Json.JsonException)
                {
                    data = new Dictionary<string, object> { ["RawData"] = history.Data };
                }
            }

            return new DownloadHistory
            {
                Id = history.Id,
                DownloadId = history.DownloadId ?? string.Empty,
                EventType = HistoryEvents.ToDownloadEvent(history.EventType),
                Status = MapStatus(history.EventType, history.Outcome),
                EventDate = history.Timestamp,
                DownloadClient = history.Source ?? "Unknown",
                DownloadClientId = history.DownloadClientId ?? string.Empty,
                AudiobookId = Guid.TryParse(history.AudiobookExternalId, out var audiobookId) ? audiobookId : null,
                Title = history.SourceTitle ?? history.AudiobookTitle ?? string.Empty,
                Data = data,
                ErrorMessage = history.Error,
                WasImported = history.EventType == HistoryEvents.Imported && history.Outcome == HistoryOutcome.Succeeded,
                ImportedAt = history.EventType == HistoryEvents.Imported && history.Outcome == HistoryOutcome.Succeeded
                    ? history.Timestamp
                    : null
            };
        }

        private static string GetCorrelationId(Dictionary<string, object>? data, string fallback)
        {
            if (data != null &&
                data.TryGetValue("CorrelationId", out var value) &&
                !string.IsNullOrWhiteSpace(value?.ToString()))
            {
                return value!.ToString()!;
            }
            return fallback;
        }

        private static Dictionary<string, object>? NormalizeData(Dictionary<string, object>? data)
        {
            if (data == null) return null;
            return data.ToDictionary(pair => pair.Key, pair => NormalizeJsonValue(pair.Value));
        }

        private static object NormalizeJsonValue(object value)
        {
            if (value is not System.Text.Json.JsonElement element) return value;
            return element.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => element.GetString() ?? string.Empty,
                System.Text.Json.JsonValueKind.Number when element.TryGetInt32(out var intValue) => intValue,
                System.Text.Json.JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
                System.Text.Json.JsonValueKind.Number => element.GetDouble(),
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                System.Text.Json.JsonValueKind.Null => string.Empty,
                _ => element.ToString()
            };
        }

        private static DownloadItemStatus MapStatus(string eventType, HistoryOutcome outcome)
        {
            if (outcome == HistoryOutcome.Failed)
                return eventType == HistoryEvents.ImportFailed ? DownloadItemStatus.ImportFailed : DownloadItemStatus.Failed;
            return eventType switch
            {
                HistoryEvents.Grabbed => DownloadItemStatus.Queued,
                HistoryEvents.Downloading => DownloadItemStatus.Downloading,
                HistoryEvents.DownloadCompleted => DownloadItemStatus.Completed,
                HistoryEvents.Imported => DownloadItemStatus.Imported,
                HistoryEvents.Paused => DownloadItemStatus.Paused,
                HistoryEvents.Removed => DownloadItemStatus.Removed,
                HistoryEvents.Checking => DownloadItemStatus.Checking,
                _ => DownloadItemStatus.Unknown
            };
        }
    }
}
