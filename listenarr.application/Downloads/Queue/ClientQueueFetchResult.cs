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

namespace Listenarr.Application.Downloads.Queue
{
    public sealed class ClientQueueFetchResult
    {
        public ClientQueueFetchResult(
            DownloadClientConfiguration client,
            List<QueueItem> queueItems,
            bool usedCachedSnapshot,
            bool isUnavailable,
            TimeSpan? snapshotAge,
            string? failureReason,
            string snapshotState,
            DateTimeOffset? snapshotRefreshedAtUtc)
        {
            Client = client;
            QueueItems = queueItems ?? new List<QueueItem>();
            UsedCachedSnapshot = usedCachedSnapshot;
            IsUnavailable = isUnavailable;
            SnapshotAge = snapshotAge;
            FailureReason = failureReason;
            SnapshotState = snapshotState;
            SnapshotRefreshedAtUtc = snapshotRefreshedAtUtc;
        }

        public DownloadClientConfiguration Client { get; }
        public List<QueueItem> QueueItems { get; }
        public bool UsedCachedSnapshot { get; }
        public bool IsUnavailable { get; }
        public TimeSpan? SnapshotAge { get; }
        public string? FailureReason { get; }
        public string SnapshotState { get; }
        public DateTimeOffset? SnapshotRefreshedAtUtc { get; }
    }
}
