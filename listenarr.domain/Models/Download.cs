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

using System.Text.Json;
using System.Text.Json.Serialization;
using Listenarr.Domain.Common;

namespace Listenarr.Domain.Models
{
    public enum DownloadStatus
    {
        Queued,
        Downloading,
        Paused,
        Completed,
        Failed,
        Processing,
        Ready,
        Moved,           // Added to track completed moves
        ImportBlocked,   // Added: Block re-processing of bad imports
        ImportPending    // Added: Explicitly waiting on import completion/manual interaction
    }

    public class Download
    {
        public static int MaxImportAttempts = 3;

        public string Id { get; set; } = Guid.NewGuid().ToString();
        public int? AudiobookId { get; set; } // Link to Audiobook record for metadata
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;

        // Enhanced identifying metadata for better matching
        public string? Asin { get; set; } // Amazon identifier - most reliable
        public string? Isbn { get; set; } // ISBN - very reliable
        public string? Series { get; set; } // Series name
        public string? SeriesNumber { get; set; } // Book number in series
        public string? Publisher { get; set; }
        public string? Language { get; set; }
        public int? Runtime { get; set; } // Runtime in minutes
        public long? ExpectedFileSize { get; set; } // Expected file size from search result
        public string OriginalUrl { get; set; } = string.Empty;
        public DownloadStatus Status
        {
            get;
            set;
        } = DownloadStatus.Queued;
        public decimal Progress
        {
            get;
            set
            {
                field = Math.Min(value, 100);
            }
        }
        public long TotalSize { get; set; }
        public long DownloadedSize { get; set; }
        // Configured DownloadPath of the client
        public string DownloadPath { get; set; } = string.Empty;
        public string FinalPath { get; set; } = string.Empty;
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? ErrorMessage { get; set; }
        public string DownloadClientId { get; set; } = string.Empty;
        public Dictionary<string, object> Metadata { get; set; } = new();

        /// <summary>
        /// Tracks why a download is ImportBlocked (error code for categorization)
        /// Examples: "InvalidFormat", "MissingMetadata", "FileMoveError", "AlreadyImported"
        /// </summary>
        public string? ImportBlockReason { get; set; }

        /// <summary>
        /// List of user-facing error messages explaining why import failed
        /// Multiple messages can be added as validation discovers multiple issues
        /// </summary>
        public List<string>? ImportBlockMessages { get; set; }

        /// <summary>
        /// Number of import attempts made for this download
        /// Helps identify chronically failing imports
        /// </summary>
        public int ImportAttempts { get; set; } = 0;

        /// <summary>
        /// When the download was last imported successfully (for idempotency tracking)
        /// If null and Status=Moved, import happened but wasn't recorded
        /// </summary>
        public DateTime? LastImportedAt { get; set; }

        /// <summary>
        /// Unique identifier from download history to track import event
        /// Links to DownloadHistory.Id for audit trail
        /// </summary>
        public int? HistoryId { get; set; }

        public string? GetMetadataString(string key)
        {
            if (!Metadata.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            if (value is JsonElement element)
            {
                return element.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.Undefined => null,
                    _ => element.ToString()
                };
            }

            return value.ToString();
        }

        public string? GetClientDownloadItemId()
        {
            if (Metadata.TryGetValue("ClientDownloadId", out var clientIdObj))
            {
                var clientId = clientIdObj?.ToString();
                if (!string.IsNullOrWhiteSpace(clientId))
                {
                    return clientId;
                }
            }

            if (Metadata.TryGetValue("TorrentHash", out var torrentHashObj))
            {
                var torrentHash = torrentHashObj?.ToString();
                if (!string.IsNullOrWhiteSpace(torrentHash))
                {
                    return torrentHash;
                }
            }

            return null;
        }

        public void SetClientDownloadId(string value)
        {
            Metadata["ClientDownloadId"] = value;
        }

        public Download SetStatus(DownloadStatus value)
        {
            if (IsValidStatusTransition(value))
            {
                Status = value;
            }
            else
            {
                throw new InvalidOperationException($"Download {Id} cannot go from {Status} to {value}");
            }

            return this;
        }

        public Download Importing()
        {
            SetStatus(DownloadStatus.ImportPending);
            return this;
        }

        public Download Imported()
        {
            SetStatus(DownloadStatus.Moved);
            return this;
        }

        public Download Failed(string message)
        {
            SetStatus(DownloadStatus.Failed);
            ErrorMessage = message;
            Metadata["ClientFailureReason"] = message;
            return this;
        }

        public Download Blocked(string reason, string message)
        {
            SetStatus(DownloadStatus.ImportBlocked);
            AddBlockMessage(message);
            ImportBlockReason = reason;
            return this;
        }

        public Download Unblock()
        {
            ImportBlockReason = null;
            ImportBlockMessages = null;
            ImportAttempts = 0;

            Importing();

            return this;
        }

        public Download Processing()
        {
            SetStatus(DownloadStatus.Processing);
            return this;
        }

        public Download Downloading()
        {
            SetStatus(DownloadStatus.Downloading);
            return this;
        }

        public Download Completed()
        {
            Progress = 100.0M;
            SetStatus(DownloadStatus.Completed);
            return this;
        }

        public bool IsBlocked()
        {
            return Status == DownloadStatus.ImportBlocked;
        }

        public void AddBlockMessage(string message)
        {
            if (ImportBlockMessages == null)
            {
                ImportBlockMessages = [];
            }
            ImportBlockMessages.Add(message);
        }

        private bool IsValidStatusTransition(DownloadStatus toStatus)
        {
            if (Status == toStatus)
            {
                return true;
            }

            return toStatus switch
            {
                DownloadStatus.ImportPending => Status is DownloadStatus.Queued
                    or DownloadStatus.Downloading
                    or DownloadStatus.Paused
                    or DownloadStatus.Processing
                    or DownloadStatus.Completed
                    or DownloadStatus.ImportBlocked,

                DownloadStatus.ImportBlocked => Status is DownloadStatus.ImportPending
                    or DownloadStatus.Processing
                    or DownloadStatus.Completed
                    or DownloadStatus.Downloading,

                DownloadStatus.Moved => Status is DownloadStatus.ImportPending
                    or DownloadStatus.Completed
                    or DownloadStatus.Processing
                    or DownloadStatus.Downloading,

                _ => true,
            };
        }

        public bool AwaitsImportation()
        {
            return Status == DownloadStatus.Completed ||
                Status == DownloadStatus.ImportPending;
        }

        public Download Clone()
        {
            return new Download
            {
                Id = Id,
                Title = Title,
                Artist = Artist,
                Album = Album,
                OriginalUrl = OriginalUrl,
                Status = Status,
                Progress = Progress,
                TotalSize = TotalSize,
                DownloadedSize = DownloadedSize,
                DownloadPath = DownloadPath,
                FinalPath = FinalPath,
                StartedAt = StartedAt,
                CompletedAt = CompletedAt,
                ErrorMessage = ErrorMessage,
                DownloadClientId = DownloadClientId
            };
        }
    }

    /// <summary>
    /// Represents a download item in the queue with live status from download clients
    /// </summary>
    public class QueueItem
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Author { get; set; }
        public string? Series { get; set; }
        public string? SeriesNumber { get; set; }
        public string Quality { get; set; } = string.Empty;
        public string? Language { get; set; }
        public string Status { get; set; } = string.Empty; // downloading, paused, queued, completed, failed
        public double Progress { get; set; } // 0-100
        public long Size { get; set; } // in bytes
        public long Downloaded { get; set; } // in bytes
        public double DownloadSpeed { get; set; } // bytes per second
        public int? Eta { get; set; } // seconds remaining
        public string? Indexer { get; set; }
        public string DownloadClient { get; set; } = string.Empty;
        public string DownloadClientId { get; set; } = string.Empty;
        public string DownloadClientType { get; set; } = string.Empty; // qbittorrent, transmission, etc
        public DateTime AddedAt { get; set; } = DateTime.UtcNow;
        public string? ErrorMessage { get; set; }
        public bool IsStaleSnapshot { get; set; }
        public string? SnapshotState { get; set; } // live, cached
        public string? SnapshotFailureReason { get; set; } // timeout, error, canceled
        public int? SnapshotAgeSeconds { get; set; }
        public DateTime? SnapshotRefreshedAt { get; set; }
        public bool CanPause { get; set; } = true;
        public bool CanRemove { get; set; } = true;
        public int? Seeders { get; set; }
        public int? Leechers { get; set; }
        public double? Ratio { get; set; }

        /// <summary>
        /// Link to the audiobook record this download is associated with (if any).
        /// </summary>
        public int? AudiobookId { get; set; }

        /// <summary>
        /// The path as reported by the download client (may be in different mount point)
        /// </summary>
        public string? RemotePath { get; set; }

        /// <summary>
        /// The path translated for Listenarr's filesystem (after applying remote path mapping)
        /// </summary>
        public string? LocalPath { get; set; }

        /// <summary>
        /// The full path to the downloaded content (file or folder).
        /// For qBittorrent: content_path field (save_path + name)
        /// This is often the most accurate path for locating the actual downloaded file/folder.
        /// </summary>
        public string? ContentPath { get; set; }

        /// <summary>
        /// Exact source files reported by the download client for this download.
        /// Used internally to scope automatic imports to files that actually belong
        /// to the download, even when the surrounding directory contains unrelated files.
        /// </summary>
        [JsonIgnore]
        public List<string>? SourceFiles { get; set; }

        /// <summary>
        /// The time when the download was detected as complete.
        /// Used for stability window validation before import.
        /// </summary>
        public DateTime? CompletionTime { get; set; }

        /// <summary>
        /// Creates a shallow copy of this QueueItem.
        /// Used by GetImportItem to avoid modifying the original item.
        /// Matches DownloadClientItem.Clone() pattern.
        /// </summary>
        public QueueItem Clone()
        {
            return (QueueItem)MemberwiseClone();
        }

        public int GetMatchScore(Download download)
        {
            if (download == null)
            {
                return 0;
            }

            if (!string.IsNullOrWhiteSpace(download.Id) &&
                !string.IsNullOrWhiteSpace(Id) &&
                string.Equals(download.Id, Id, StringComparison.OrdinalIgnoreCase))
            {
                return 4;
            }

            var clientDownloadId = download.GetMetadataString("ClientDownloadId");
            if (!string.IsNullOrWhiteSpace(clientDownloadId) &&
                string.Equals(clientDownloadId, Id, StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }

            var torrentHash = download.GetMetadataString("TorrentHash");
            if (!string.IsNullOrWhiteSpace(torrentHash) &&
                string.Equals(torrentHash, Id, StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }

            if (TitleUtils.TitlesExactlyMatch(download.Title, Title))
            {
                return 2;
            }

            if (TitleUtils.IsMatchingTitle(download.Title, Title))
            {
                if (TitleContainsArtist(download))
                {
                    return 2;
                }

                if (TitleUtils.IsStrongStandaloneTitleMatch(download.Title, Title))
                {
                    return 1;
                }
            }

            return 0;
        }

        public bool TitleContainsArtist(Download download)
        {
            var normalizedArtist = TitleUtils.NormalizeTitle(download?.Artist ?? string.Empty);
            if (string.IsNullOrWhiteSpace(normalizedArtist) || normalizedArtist.Length < 4)
            {
                return false;
            }

            var normalizedQueueTitle = TitleUtils.NormalizeTitle(Title ?? string.Empty);
            return normalizedQueueTitle.Contains(normalizedArtist, StringComparison.OrdinalIgnoreCase);
        }
    }
}
