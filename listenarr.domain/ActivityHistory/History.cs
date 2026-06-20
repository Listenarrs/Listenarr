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

using System.ComponentModel.DataAnnotations;

namespace Listenarr.Domain.ActivityHistory
{
    public enum HistoryOutcome
    {
        Requested = 0,
        Succeeded = 1,
        Failed = 2,
        Skipped = 3,
        Retrying = 4
    }

    public class History
    {
        [Key]
        public int Id { get; set; }

        /// <summary>
        /// The audiobook ID this history entry relates to (nullable for non-audiobook events)
        /// </summary>
        public int? AudiobookId { get; set; }

        /// <summary>
        /// Compatibility/external audiobook identifier when the source uses a non-integer key.
        /// </summary>
        public string? AudiobookExternalId { get; set; }

        /// <summary>
        /// Title of the audiobook (denormalized for display even if audiobook is deleted)
        /// </summary>
        public string? AudiobookTitle { get; set; }

        /// <summary>
        /// Denormalized release, file, or action subject displayed in activity history.
        /// </summary>
        public string? SourceTitle { get; set; }

        /// <summary>
        /// Type of event: Added, Downloaded, Imported, Deleted, Updated, etc.
        /// </summary>
        public string EventType { get; set; } = string.Empty;

        /// <summary>
        /// Detailed description of the event
        /// </summary>
        public string? Message { get; set; }

        /// <summary>
        /// Source of the action: Manual, Search, Import, Download, etc.
        /// </summary>
        public string? Source { get; set; }

        /// <summary>
        /// Timestamp of the event
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Whether a Discord notification was sent for this event
        /// </summary>
        public bool NotificationSent { get; set; } = false;

        /// <summary>
        /// Optional data payload (JSON string for additional context)
        /// </summary>
        public string? Data { get; set; }

        /// <summary>
        /// Stable operational outcome used by server-side filtering.
        /// </summary>
        public HistoryOutcome Outcome { get; set; } = HistoryOutcome.Succeeded;

        /// <summary>
        /// Listenarr download record or external download identifier.
        /// </summary>
        public string? DownloadId { get; set; }

        /// <summary>
        /// Download client configuration associated with the action.
        /// </summary>
        public string? DownloadClientId { get; set; }

        /// <summary>
        /// Groups attempts that belong to the same logical workflow.
        /// </summary>
        public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// Optional parent event for attempt/detail relationships.
        /// </summary>
        public int? ParentEventId { get; set; }

        /// <summary>
        /// Failure detail retained separately from the display message.
        /// </summary>
        public string? Error { get; set; }
    }

    public sealed class HistoryQuery
    {
        public int Limit { get; set; } = 50;
        public int Offset { get; set; }
        public string SortBy { get; set; } = "timestamp";
        public string SortDirection { get; set; } = "desc";
        public string? EventType { get; set; }
        public HistoryOutcome? Outcome { get; set; }
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public int? AudiobookId { get; set; }
        public string? DownloadId { get; set; }
        public string? DownloadClientId { get; set; }
        public string? CorrelationId { get; set; }
    }

    public sealed record HistoryPage(List<History> Records, int Total, int Limit, int Offset);
}
