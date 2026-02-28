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

namespace Listenarr.Domain.Models
{
    /// <summary>
    /// Protocol type for download clients
    /// </summary>
    public enum DownloadProtocol
    {
        Torrent,
        Usenet
    }

    /// <summary>
    /// Normalized status for download items across all clients
    /// </summary>
    public enum DownloadItemStatus
    {
        Queued = 0,
        Paused = 1,
        Downloading = 2,
        Completed = 3,
        Failed = 4,
        Warning = 5,
        Importing = 6,
        Imported = 7,
        ImportFailed = 8,
        Removed = 9,
        Checking = 10,
        Unknown = 11
    }

    /// <summary>
    /// Metadata about which download client a DownloadClientItem originated from
    /// </summary>
    public class DownloadClientItemClientInfo
    {
        public DownloadProtocol Protocol { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool RemoveCompletedDownloads { get; set; }
        public bool HasPostImportCategory { get; set; }

        public static DownloadClientItemClientInfo FromClient(
            string clientId,
            string clientName,
            string clientType,
            DownloadProtocol protocol,
            bool removeCompletedDownloads = false,
            bool hasPostImportCategory = false)
        {
            return new DownloadClientItemClientInfo
            {
                Id = clientId,
                Name = clientName,
                Type = clientType,
                Protocol = protocol,
                RemoveCompletedDownloads = removeCompletedDownloads,
                HasPostImportCategory = hasPostImportCategory
            };
        }
    }

    /// <summary>
    /// Normalized representation of a download queue item from any download client.
    /// This follows the DownloadClientItem pattern for consistency across
    /// qBittorrent, Transmission, SABnzbd, NZBGet, etc.
    /// 
    /// All download clients are expected to map their native queue items to this structure.
    /// </summary>
    public class DownloadClientItem
    {
        /// <summary>
        /// Unique identifier for this download within the client.
        /// For torrents: info hash (SHA1 uppercase hex, 40 characters)
        /// For usenet: client-specific ID (NZO ID, NZB ID, etc)
        /// 
        /// This is the key that links client queue items to Download records.
        /// </summary>
        public string DownloadId { get; set; } = string.Empty;

        /// <summary>
        /// Metadata about which download client this item originated from
        /// </summary>
        public DownloadClientItemClientInfo DownloadClientInfo { get; set; } = new();

        /// <summary>
        /// Download title (release name from client)
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Category or label assigned by the client.
        /// Used to filter downloads by type (audiobooks, music, etc).
        /// </summary>
        public string Category { get; set; } = string.Empty;

        /// <summary>
        /// Total size of the download in bytes
        /// </summary>
        public long TotalSize { get; set; }

        /// <summary>
        /// Remaining bytes to download
        /// </summary>
        public long RemainingSize { get; set; }

        /// <summary>
        /// Estimated time to completion
        /// </summary>
        public TimeSpan? RemainingTime { get; set; }

        /// <summary>
        /// Torrent seed ratio (if applicable)
        /// </summary>
        public double? SeedRatio { get; set; }

        /// <summary>
        /// Final output path after download completes.
        /// May require GetImportItem() to resolve for some clients.
        /// </summary>
        public string OutputPath { get; set; } = string.Empty;

        /// <summary>
        /// Current download status (normalized across all clients)
        /// </summary>
        public DownloadItemStatus Status { get; set; }

        /// <summary>
        /// Optional message from the client (warnings, errors)
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Whether the download is encrypted/password-protected
        /// </summary>
        public bool IsEncrypted { get; set; }

        /// <summary>
        /// Whether the download can be safely removed from the client
        /// </summary>
        public bool CanBeRemoved { get; set; }

        /// <summary>
        /// Whether the download can be moved to another path
        /// </summary>
        public bool CanMoveFiles { get; set; }

        /// <summary>
        /// Whether this item has been marked as removed (for cleanup tracking)
        /// </summary>
        public bool Removed { get; set; }

        /// <summary>
        /// Download progress (0.0 to 100.0)
        /// </summary>
        public double Progress { get; set; }

        /// <summary>
        /// Current download speed in bytes per second
        /// </summary>
        public double DownloadSpeed { get; set; }

        /// <summary>
        /// Number of seeders (for torrents)
        /// </summary>
        public int? Seeders { get; set; }

        /// <summary>
        /// Number of leechers (for torrents)
        /// </summary>
        public int? Leechers { get; set; }

        /// <summary>
        /// When the download was added to the client
        /// </summary>
        public DateTime AddedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Creates a shallow copy of this DownloadClientItem.
        /// Used by GetImportItem to avoid modifying the original item.
        /// Matches the pattern for resolving final paths.
        /// </summary>
        public DownloadClientItem Clone()
        {
            return (DownloadClientItem)MemberwiseClone();
        }

        /// <summary>
        /// Helper to check if download is complete
        /// </summary>
        public bool IsComplete()
        {
            return Status == DownloadItemStatus.Completed && RemainingSize == 0;
        }

        /// <summary>
        /// Helper to check if download has failed
        /// </summary>
        public bool HasFailed()
        {
            return Status == DownloadItemStatus.Failed;
        }

        /// <summary>
        /// Helper to check if download is actively downloading
        /// </summary>
        public bool IsDownloading()
        {
            return Status == DownloadItemStatus.Downloading;
        }
    }
}
