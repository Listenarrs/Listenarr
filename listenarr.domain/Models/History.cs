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

namespace Listenarr.Domain.Models
{
    public class History
    {
        [Key]
        public int Id { get; set; }

        /// <summary>
        /// The audiobook ID this history entry relates to (nullable for non-audiobook events)
        /// </summary>
        public int? AudiobookId { get; set; }

        /// <summary>
        /// Title of the audiobook (denormalized for display even if audiobook is deleted)
        /// </summary>
        public string? AudiobookTitle { get; set; }

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
    }
}

