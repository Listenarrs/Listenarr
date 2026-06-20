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


namespace Listenarr.Application.Downloads.Contracts
{
    /// <summary>
    /// Service for recording and querying download events.
    /// Provides idempotency checking and audit trail functionality.
    /// 
    /// This is the critical service that:
    /// - Prevents duplicate imports (IsAlreadyImportedAsync)
    /// - Records all download events (Record* methods)
    /// - Provides import validation (HasRecentGrabbedAsync)
    /// - Supports download history queries (GetHistoryAsync)
    /// </summary>
    public interface IDownloadHistoryService
    {
        /// <summary>
        /// Check if a download was already imported successfully.
        /// Returns true if we have a recent Imported event for this DownloadId.
        /// Prevents duplicate imports by validating against history table.
        /// </summary>
        /// <param name="downloadId">The download ID (torrent hash or NZB ID) - UPPERCASE</param>
        /// <param name="clientId">Download client configuration ID</param>
        /// <returns>True if already imported, false if safe to import</returns>
        Task<bool> IsAlreadyImportedAsync(string downloadId, string clientId);

        /// <summary>
        /// Check if a download was recently grabbed from a source.
        /// Used to validate that we're not re-importing an old download that reappeared.
        /// </summary>
        /// <param name="downloadId">The download ID</param>
        /// <param name="clientId">Download client configuration ID</param>
        /// <param name="withinSeconds">How far back to check (default: 7 days)</param>
        /// <returns>True if we found a recent Grabbed event</returns>
        Task<bool> HasRecentGrabbedAsync(string downloadId, string clientId, int withinSeconds = 604800);

        /// <summary>
        /// Record that a download was grabbed from a source.
        /// Called when we first detect a download in the client.
        /// </summary>
        Task RecordGrabbedAsync(string downloadId, string clientId, string title,
            DownloadProtocol protocol, Guid? audiobookId = null);

        /// <summary>
        /// Record that a download completed successfully.
        /// </summary>
        Task RecordDownloadCompleteAsync(string downloadId, string clientId, string title,
            string? outputPath = null);

        /// <summary>
        /// Record that a download failed.
        /// </summary>
        Task RecordDownloadFailedAsync(string downloadId, string clientId, string title,
            string? errorMessage = null);

        /// <summary>
        /// Record that a download was imported successfully.
        /// Sets the WasImported flag for idempotency checking.
        /// </summary>
        Task RecordImportedAsync(string downloadId, string clientId, string title,
            Guid? audiobookId = null);

        /// <summary>
        /// Record that an import failed and why.
        /// Used to populate import error details for user visibility.
        /// </summary>
        Task RecordImportFailedAsync(string downloadId, string clientId, string title,
            string? errorMessage = null);

        /// <summary>
        /// Record that a download was paused.
        /// </summary>
        Task RecordPausedAsync(string downloadId, string clientId, string title);

        /// <summary>
        /// Record that a download was resumed.
        /// </summary>
        Task RecordResumedAsync(string downloadId, string clientId, string title);

        /// <summary>
        /// Record that a download was removed from the client.
        /// </summary>
        Task RecordRemovedAsync(string downloadId, string clientId, string title);

        /// <summary>
        /// Get the full event history for a download.
        /// Used for debugging and audit trail visualization.
        /// </summary>
        /// <param name="downloadId">The download ID</param>
        /// <param name="clientId">Download client configuration ID</param>
        /// <returns>List of history events ordered by EventDate ascending</returns>
        Task<List<DownloadHistory>> GetHistoryAsync(string downloadId, string clientId);

        /// <summary>
        /// Get the most recent event for a download.
        /// Helps determine current state when EventType history is unclear.
        /// </summary>
        /// <param name="downloadId">The download ID</param>
        /// <param name="clientId">Download client configuration ID</param>
        /// <returns>Most recent history event, or null if no history</returns>
        Task<DownloadHistory?> GetLatestEventAsync(string downloadId, string clientId);

        /// <summary>
        /// Clean up old history entries (older than the specified days).
        /// This compatibility method must only be invoked by an explicit administrator operation.
        /// </summary>
        /// <param name="retentionDays">Keep events from the last N days. Zero means unlimited retention.</param>
        /// <returns>Number of entries deleted</returns>
        Task<int> CleanupOldEntriesAsync(int retentionDays = 0);
    }
}
