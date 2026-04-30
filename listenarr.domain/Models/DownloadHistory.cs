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

namespace Listenarr.Domain.Models
{
    /// <summary>
    /// Event-sourced history of download state transitions.
    /// Each record represents a state change event for a download.
    /// This prevents duplicate downloads and provides audit trail.
    /// </summary>
    public class DownloadHistory
    {
        /// <summary>
        /// Auto-incrementing primary key for the history entry
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// The unique download identifier (torrent hash or NZB ID) - UPPERCASE
        /// This is the natural key that links to DownloadClientItem.DownloadId
        /// </summary>
        public string DownloadId { get; set; } = string.Empty;

        /// <summary>
        /// Type of event that occurred
        /// </summary>
        public DownloadHistoryEventType EventType { get; set; }

        /// <summary>
        /// Download status at the time of this event
        /// </summary>
        public DownloadItemStatus Status { get; set; }

        /// <summary>
        /// When this event occurred (UTC)
        /// </summary>
        public DateTime EventDate { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// The audiobook this download was intended for (nullable if not yet matched)
        /// </summary>
        public Guid? AudiobookId { get; set; }

        /// <summary>
        /// Which download client this came from
        /// </summary>
        public string DownloadClient { get; set; } = string.Empty;

        /// <summary>
        /// Download client configuration ID
        /// </summary>
        public string DownloadClientId { get; set; } = string.Empty;

        /// <summary>
        /// Protocol used (Torrent or Usenet)
        /// </summary>
        public DownloadProtocol Protocol { get; set; }

        /// <summary>
        /// Title/name of the download
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Output path where files were downloaded (if applicable)
        /// </summary>
        public string? OutputPath { get; set; }

        /// <summary>
        /// Additional metadata about the event (JSON serialized)
        /// </summary>
        public Dictionary<string, object>? Data { get; set; }

        /// <summary>
        /// Error message if the event represents a failure
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// Whether this download was successfully imported
        /// </summary>
        public bool WasImported { get; set; }

        /// <summary>
        /// When the download was imported (if applicable)
        /// </summary>
        public DateTime? ImportedAt { get; set; }
    }

    /// <summary>
    /// Types of events that can occur in the download lifecycle
    /// Following an event-sourced pattern
    /// </summary>
    public enum DownloadHistoryEventType
    {
        /// <summary>
        /// Download was grabbed/initiated
        /// </summary>
        Grabbed = 1,

        /// <summary>
        /// Download is actively downloading
        /// </summary>
        Downloading = 2,

        /// <summary>
        /// Download finished downloading
        /// </summary>
        DownloadCompleted = 3,

        /// <summary>
        /// Download failed
        /// </summary>
        DownloadFailed = 4,

        /// <summary>
        /// Download was imported successfully
        /// </summary>
        Imported = 5,

        /// <summary>
        /// Import failed
        /// </summary>
        ImportFailed = 6,

        /// <summary>
        /// Download was paused
        /// </summary>
        Paused = 7,

        /// <summary>
        /// Download was resumed
        /// </summary>
        Resumed = 8,

        /// <summary>
        /// Download was removed/deleted
        /// </summary>
        Removed = 9,

        /// <summary>
        /// Download is being checked/verified
        /// </summary>
        Checking = 10,

        /// <summary>
        /// Warning occurred (non-fatal)
        /// </summary>
        Warning = 11
    }
}
