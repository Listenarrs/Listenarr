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

using Microsoft.AspNetCore.SignalR;
using Listenarr.Api.Hubs;
using Listenarr.Domain.Models;

namespace Listenarr.Api.Services
{
    /// <summary>
    /// Background service that polls external download client queues and pushes updates via SignalR
    /// </summary>
    public class QueueMonitorService : BackgroundService
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly IHubContext<DownloadHub> _hubContext;
        private readonly ILogger<QueueMonitorService> _logger;

        // Adaptive polling intervals - conservative since SignalR provides real-time updates
        private readonly TimeSpan _fastPollingInterval = TimeSpan.FromSeconds(5);   // Active downloads (reduced from 2s)
        private readonly TimeSpan _normalPollingInterval = TimeSpan.FromSeconds(15); // Idle/seeding (increased from 10s)
        private readonly TimeSpan _slowPollingInterval = TimeSpan.FromSeconds(60);   // Only completed items (increased from 30s)

        private List<QueueItem> _lastQueueState = new();
        private TimeSpan _currentInterval;

        public QueueMonitorService(
            IServiceScopeFactory serviceScopeFactory,
            IHubContext<DownloadHub> hubContext,
            ILogger<QueueMonitorService> logger)
        {
            _serviceScopeFactory = serviceScopeFactory;
            _hubContext = hubContext;
            _logger = logger;
            _currentInterval = _normalPollingInterval;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Queue Monitor Service starting");

            // Wait a bit before starting to ensure the app is fully initialized

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Queue Monitor Service cancelled before start");
                return;
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning(ex, "Queue Monitor startup delay canceled/timed out; continuing");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await MonitorQueueAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Cancellation requested - exit gracefully
                    break;
                }
                catch (OperationCanceledException ex)
                {
                    _logger.LogWarning(ex, "Queue monitor cycle canceled/timed out; continuing");
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogError(ex, "Error in Queue Monitor Service");
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

            _logger.LogInformation("Queue Monitor Service stopping");
        }

        private async Task MonitorQueueAsync(CancellationToken cancellationToken)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var downloadService = scope.ServiceProvider.GetRequiredService<IDownloadService>();

            try
            {
                // Get current queue from all download clients
                var currentQueue = await downloadService.GetQueueAsync();

                // Determine optimal polling interval based on queue activity
                _currentInterval = DeterminePollingInterval(currentQueue);

                // Check if queue has changed
                if (HasQueueChanged(_lastQueueState, currentQueue))
                {
                    _logger.LogDebug("Queue changed, broadcasting update ({Count} items) [polling: {Interval}s]",
                        currentQueue.Count, _currentInterval.TotalSeconds);

                    // Broadcast queue update via SignalR
                    await _hubContext.Clients.All.SendAsync(
                        "QueueUpdate",
                        currentQueue,
                        cancellationToken);

                    // Update last known state
                    _lastQueueState = currentQueue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Failed to monitor queue");
            }
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

        private bool HasQueueChanged(List<QueueItem> oldQueue, List<QueueItem> newQueue)
        {
            // Quick checks first
            if (oldQueue.Count != newQueue.Count)
                return true;

            // Create lookup for comparison
            var oldLookup = oldQueue.ToDictionary(q => q.Id);

            foreach (var newItem in newQueue)
            {
                if (!oldLookup.TryGetValue(newItem.Id, out var oldItem))
                {
                    // New item
                    return true;
                }

                // Check if any important properties changed
                if (oldItem.Status != newItem.Status ||
                    oldItem.Progress != newItem.Progress ||
                    oldItem.Downloaded != newItem.Downloaded ||
                    oldItem.ErrorMessage != newItem.ErrorMessage)
                {
                    return true;
                }
            }

            return false;
        }
    }
}


