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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Downloads.Queue
{
    /// <summary>
    /// Background service that polls external download client queues and pushes updates via SignalR
    /// </summary>
    public class QueueMonitorService(
        IQueueMonitorProcessor processor,
        ILogger<QueueMonitorService> logger,
        IAppMetricsService metrics) : BackgroundService
    {
        private TimeSpan _currentInterval = TimeSpan.FromSeconds(15);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("Queue Monitor Service starting");

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    metrics.Increment("worker.queuemonitorservice.cycle.started");
                    _currentInterval = await processor.RunCycleAsync(stoppingToken);
                    metrics.Increment("worker.queuemonitorservice.cycle.completed");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    metrics.Increment("worker.queuemonitorservice.cycle.skipped");
                    break;
                }
                catch (OperationCanceledException ex)
                {
                    metrics.Increment("worker.queuemonitorservice.cycle.failed");
                    logger.LogWarning(ex, "Queue monitor cycle canceled/timed out; continuing");
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    metrics.Increment("worker.queuemonitorservice.cycle.failed");
                    logger.LogError(ex, "Error in Queue Monitor Service");
                }

                // Wait before next poll
                try
                {
                    await Task.Delay(_currentInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            logger.LogInformation("Queue Monitor Service stopping");
        }
    }

    public class QueueMonitorProcessor(
        IServiceScopeFactory serviceScopeFactory,
        IHubContext<DownloadHub> hubContext,
        ILogger<QueueMonitorProcessor> logger) : IQueueMonitorProcessor
    {
        private readonly TimeSpan _fastPollingInterval = TimeSpan.FromSeconds(5);
        private readonly TimeSpan _normalPollingInterval = TimeSpan.FromSeconds(15);
        private readonly TimeSpan _slowPollingInterval = TimeSpan.FromSeconds(60);
        private QueueSnapshot _lastQueueSnapshot = new();

        public async Task<TimeSpan> RunCycleAsync(CancellationToken cancellationToken)
        {
            using var scope = serviceScopeFactory.CreateScope();
            var downloadQueueService = scope.ServiceProvider.GetRequiredService<IDownloadQueueService>();
            var nextInterval = _normalPollingInterval;

            try
            {
                // Get current queue from the shared queue reconciliation service
                var currentSnapshot = await downloadQueueService.GetQueueSnapshotAsync();
                var currentQueue = currentSnapshot.Items;

                // Determine optimal polling interval based on queue activity
                nextInterval = DeterminePollingInterval(currentQueue);

                // Check if queue has changed
                if (HasQueueChanged(_lastQueueSnapshot, currentSnapshot))
                {
                    logger.LogDebug("Queue changed, broadcasting update ({Count} items) [polling: {Interval}s]",
                        currentQueue.Count, nextInterval.TotalSeconds);

                    // Broadcast queue update via SignalR
                    await hubContext.Clients.All.SendAsync(
                        "QueueUpdate",
                        currentSnapshot,
                        cancellationToken);

                    // Update last known state
                    _lastQueueSnapshot = currentSnapshot;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Failed to monitor queue");
            }

            return nextInterval;
        }

        private TimeSpan DeterminePollingInterval(List<QueueItem> queue)
        {
            if (queue == null || queue.Count == 0)
            {
                // No items: slow polling
                return _slowPollingInterval;
            }

            // Check for active downloads (downloading, queued, or paused with progress < 100)
            // Use HashSet for O(1) lookups instead of multiple string comparisons
            var activeStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "downloading", "queued" };
            var hasActiveDownloads = queue.Any(q =>
                activeStatuses.Contains(q.Status) ||
                (q.Status.Equals("paused", StringComparison.OrdinalIgnoreCase) && q.Progress < 100));

            if (hasActiveDownloads)
            {
                // Active downloads: fast polling for smooth progress updates
                return _fastPollingInterval;
            }

            // Only completed/seeding items: normal polling
            return _normalPollingInterval;
        }

        private bool HasQueueChanged(QueueSnapshot oldSnapshot, QueueSnapshot newSnapshot)
        {
            var oldQueue = oldSnapshot?.Items ?? new List<QueueItem>();
            var newQueue = newSnapshot?.Items ?? new List<QueueItem>();

            // Quick checks first
            if (oldQueue.Count != newQueue.Count)
                return true;

            var oldClients = oldSnapshot?.Clients ?? new List<QueueClientStatus>();
            var newClients = newSnapshot?.Clients ?? new List<QueueClientStatus>();
            if (oldClients.Count != newClients.Count)
                return true;

            // Create lookup for comparison
            var oldLookup = oldQueue
                .GroupBy(q => q.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var newItem in newQueue)
            {
                if (!oldLookup.TryGetValue(newItem.Id, out var oldItem))
                {
                    // New item
                    return true;
                }

                // Check if any important properties changed
                if (oldItem.Status != newItem.Status ||
                    Math.Abs(oldItem.Progress - newItem.Progress) > 1e-9 ||
                    oldItem.Downloaded != newItem.Downloaded ||
                    oldItem.ErrorMessage != newItem.ErrorMessage)
                {
                    return true;
                }
            }

            var oldClientLookup = oldClients.ToDictionary(c => c.ClientId, StringComparer.OrdinalIgnoreCase);
            foreach (var newClient in newClients)
            {
                if (!oldClientLookup.TryGetValue(newClient.ClientId, out var oldClient))
                {
                    return true;
                }

                if (oldClient.SnapshotState != newClient.SnapshotState ||
                    oldClient.SnapshotFailureReason != newClient.SnapshotFailureReason ||
                    oldClient.IsStaleSnapshot != newClient.IsStaleSnapshot ||
                    oldClient.IsUnavailable != newClient.IsUnavailable ||
                    oldClient.ItemCount != newClient.ItemCount)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
