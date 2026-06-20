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
    public static class DownloadQueueSnapshotMapper
    {
        public static string GetClientQueueSnapshotCacheKey(DownloadClientConfiguration client)
        {
            return $"download-queue:snapshot:{client.Id}";
        }

        public static List<QueueItem> CloneQueueItems(IEnumerable<QueueItem>? queueItems)
        {
            if (queueItems == null)
            {
                return new List<QueueItem>();
            }

            return queueItems
                .Where(item => item != null)
                .Select(item => item.Clone())
                .ToList();
        }

        public static void ApplySnapshotMetadata(List<QueueItem> queueItems, ClientQueueFetchResult clientQueueResult)
        {
            if (queueItems == null || queueItems.Count == 0)
            {
                return;
            }

            var snapshotAgeSeconds = clientQueueResult.SnapshotAge.HasValue
                ? (int?)Math.Max(0, Math.Round(clientQueueResult.SnapshotAge.Value.TotalSeconds))
                : null;
            var snapshotRefreshedAt = clientQueueResult.SnapshotRefreshedAtUtc?.UtcDateTime;

            foreach (var queueItem in queueItems)
            {
                queueItem.IsStaleSnapshot = clientQueueResult.UsedCachedSnapshot;
                queueItem.SnapshotState = clientQueueResult.SnapshotState;
                queueItem.SnapshotFailureReason = clientQueueResult.FailureReason;
                queueItem.SnapshotAgeSeconds = snapshotAgeSeconds;
                queueItem.SnapshotRefreshedAt = snapshotRefreshedAt;
            }
        }

        public static List<QueueClientStatus> BuildClientStatuses(IEnumerable<ClientQueueFetchResult> clientQueueResults)
        {
            if (clientQueueResults == null)
            {
                return new List<QueueClientStatus>();
            }

            return clientQueueResults
                .Where(result => result?.Client != null)
                .Select(result =>
                {
                    var snapshotAgeSeconds = result.SnapshotAge.HasValue
                        ? (int?)Math.Max(0, Math.Round(result.SnapshotAge.Value.TotalSeconds))
                        : null;

                    return new QueueClientStatus
                    {
                        ClientId = result.Client.Id ?? string.Empty,
                        ClientName = result.Client.Name ?? result.Client.Id ?? "Download client",
                        ClientType = result.Client.Type?.ToLowerInvariant() ?? "unknown",
                        SnapshotState = result.SnapshotState,
                        IsStaleSnapshot = result.UsedCachedSnapshot,
                        IsUnavailable = result.IsUnavailable,
                        SnapshotFailureReason = result.FailureReason,
                        SnapshotAgeSeconds = snapshotAgeSeconds,
                        SnapshotRefreshedAt = result.SnapshotRefreshedAtUtc?.UtcDateTime,
                        ItemCount = result.QueueItems?.Count ?? 0
                    };
                })
                .OrderBy(status => status.ClientName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
