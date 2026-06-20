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

namespace Listenarr.Domain.Downloads
{
    public class QueueSnapshot
    {
        public List<QueueItem> Items { get; set; } = new();
        public List<QueueClientStatus> Clients { get; set; } = new();
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public bool HasStaleData { get; set; }
        public bool HasUnavailableClients { get; set; }
    }

    public class QueueClientStatus
    {
        public string ClientId { get; set; } = string.Empty;
        public string ClientName { get; set; } = string.Empty;
        public string ClientType { get; set; } = string.Empty;
        public string SnapshotState { get; set; } = "live"; // live, cached, unavailable
        public bool IsStaleSnapshot { get; set; }
        public bool IsUnavailable { get; set; }
        public string? SnapshotFailureReason { get; set; }
        public int? SnapshotAgeSeconds { get; set; }
        public DateTime? SnapshotRefreshedAt { get; set; }
        public int ItemCount { get; set; }
    }
}
