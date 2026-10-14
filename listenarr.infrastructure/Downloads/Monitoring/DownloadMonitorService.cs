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

namespace Listenarr.Infrastructure.Downloads.Monitoring
{
    /// <summary>
    /// Background service that monitors downloads
    /// - Uses DownloadClientGateway to fetch download updates
    /// - Persists updates
    /// - Handles retry timings on polling failures
    /// CompletedDownloadProcessor handles downloads in completed status
    /// </summary>
    public class DownloadMonitorService(
        IDownloadMonitorProcessor processor,
        ILogger<DownloadMonitorService> logger,
        IWorkerCycleRunner cycleRunner,
        IServiceScopeFactory scopeFactory) : BackgroundService
    {
        private int _pollingInterval = 30;

        public void ScheduleNextClientPoll(DownloadClientConfiguration client, double intervalSeconds) =>
            processor.ScheduleNextClientPoll(client, intervalSeconds);

        public Task RunCycleAsync(CancellationToken cancellationToken) => processor.RunCycleAsync(cancellationToken);

        internal Task MonitorDownloadsAsync(CancellationToken cancellationToken) => processor.RunCycleAsync(cancellationToken);

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("Download Monitor Service starting");

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

            using var scope = scopeFactory.CreateScope();
            var configurationService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            var appSettings = await configurationService.GetApplicationSettingsAsync();
            if (appSettings.PollingIntervalSeconds > 0)
            {
                _pollingInterval = appSettings.PollingIntervalSeconds;
            }

            logger.LogInformation("DownloadMonitorService polling interval set to {PollingInterval}s", _pollingInterval);

            await cycleRunner.RunPeriodicAsync(
                nameof(DownloadMonitorService),
                initialDelay: null,
                intervalProvider: () => TimeSpan.FromSeconds(_pollingInterval),
                runCycle: processor.RunCycleAsync,
                cancellationToken);

            logger.LogInformation("Download Monitor Service stopping");
        }
    }

    public partial class DownloadMonitorProcessor(
        IServiceScopeFactory scopeFactory,
        IDownloadPushService downloadPushService,
        TimeProvider timeProvider,
        ILogger<DownloadMonitorProcessor> logger) : IDownloadMonitorProcessor
    {
        private static readonly TimeSpan OrphanCleanupInterval = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan OrphanCleanupQueueTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan OrphanCleanupStaleSnapshotMaxAge = TimeSpan.FromMinutes(3);

        private int _pollingInterval = 30;
        private DateTime _lastFullBroadcast = DateTime.MinValue;

        // Per-client polling controls to avoid overloading download clients
        internal readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _nextClientPoll = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _clientFailureCounts = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _nextOrphanCleanup = new();

        public void ScheduleNextClientPoll(DownloadClientConfiguration client, double interval)
        {
            // Add small jitter to avoid synchronized polls: +/- 5s
            var jitter = (int)(new Random().NextDouble() * 10 - 5);
            var next = DateTime.UtcNow.AddSeconds(interval + jitter);

            _nextClientPoll.AddOrUpdate(client.Id, next, (_, __) => next);
            logger.LogDebug($"Scheduled next poll for client {client.Id} at {next} (interval {interval})");
        }

        /// <summary>
        /// Schedule next poll for a client after a successful interaction
        /// </summary>
        private void ScheduleNextClientPollOnSuccess(DownloadClientConfiguration client)
        {
            // Reset failure count
            _clientFailureCounts.TryRemove(client.Id, out _);
            ScheduleNextClientPoll(client, client.GetPollingInterval(_pollingInterval));
        }

        /// <summary>
        /// Schedule next poll for a client after a failure using exponential backoff
        /// </summary>
        private void ScheduleNextClientPollOnFailure(DownloadClientConfiguration client)
        {
            var count = _clientFailureCounts.AddOrUpdate(client.Id, 1, (_, old) => old + 1);
            ScheduleNextClientPoll(client, Math.Min(900, 30 * Math.Pow(2, count - 1)));
        }

        public Task RunCycleAsync(CancellationToken cancellationToken) => MonitorDownloadsAsync(cancellationToken);

        internal async Task MonitorDownloadsAsync(CancellationToken cancellationToken)
        {
            using var scope = scopeFactory.CreateScope();
            var downloadRepository = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
            var configurationService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            var downloadClientGateway = scope.ServiceProvider.GetRequiredService<IDownloadClientGateway>();
            var clientQueuePoller = scope.ServiceProvider.GetRequiredService<DownloadClientQueuePoller>();
            var orphanCleanupService = scope.ServiceProvider.GetRequiredService<DownloadOrphanCleanupService>();

            var appSettings = await configurationService.GetApplicationSettingsAsync();
            if (appSettings.PollingIntervalSeconds > 0)
            {
                _pollingInterval = appSettings.PollingIntervalSeconds;
            }

            // Settings > Download exposes this as Download Completion Stability. It has had no
            // reader since #535/#492 removed the old one, so until now finalization began in the
            // same pass that first saw the client report completion.
            var stabilityWindow = TimeSpan.FromSeconds(Math.Max(0, appSettings.DownloadCompletionStabilitySeconds));

            var configuredClients = await configurationService.GetDownloadClientConfigurationsAsync();
            HashSet<string> enabledClientIds = configuredClients
                .Where(c => c.IsEnabled && !string.IsNullOrWhiteSpace(c.Id))
                .Select(c => c.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (enabledClientIds.Count == 0)
            {
                logger.LogInformation("No enabled download clients configured; skipping client polling");
                return;
            }

            var activeDownloads = await downloadRepository.GetActiveAsync();

            logger.LogInformation("DownloadMonitorService found {Count} active downloads", activeDownloads.Count);
            foreach (var dl in activeDownloads)
            {
                logger.LogDebug("Active download: {Id} - {Title} - Status: {Status} - Client: {ClientId}",
                    LogRedaction.SanitizeText(dl.Id), LogRedaction.SanitizeText(dl.Title), dl.Status, LogRedaction.SanitizeText(dl.DownloadClientId));
            }

            // Filter download with active client configuration
            activeDownloads = [.. activeDownloads.Where(d => enabledClientIds.Contains(d.DownloadClientId))];
            if (activeDownloads.Count <= 0)
            {
                logger.LogInformation("No active downloads mapped to enabled download clients; skipping client polling");
                return;
            }

            // Lets assume downloads have been updated by now
            if (DateTime.UtcNow - _lastFullBroadcast > TimeSpan.FromSeconds(120))
            {
                await OnDownloadsUpdated(activeDownloads, cancellationToken);
                _lastFullBroadcast = DateTime.UtcNow;
            }

            logger.LogInformation($"Calling PollDownloadClientsAsync with {activeDownloads.Count} downloads");

            // Group downloads by client
            var downloadsByClient = activeDownloads
                .GroupBy(d => d.DownloadClientId);

            foreach (var group in downloadsByClient)
            {
                // Will always succeed as long as downloads are filtered on active clients
                var client = configuredClients.FirstOrDefault(c => c.Id == group.Key);
                if (client == null)
                {
                    continue;
                }

                // Respect per-client poll schedules to avoid overloading qbittorrent
                if (_nextClientPoll.TryGetValue(client.Id, out var scheduled) && DateTime.UtcNow < scheduled)
                {
                    logger.LogDebug($"Skipping qBittorrent poll for client {client.Id}, next scheduled at {scheduled}");
                    continue;
                }

                var clientDownloads = group.ToList();
                logger.LogInformation($"Processing client group: ClientId={client.Id}, Count={clientDownloads.Count}");

                try
                {
                    var previousDownloads = clientDownloads.Select(item => item.Clone()).ToList();
                    var updatedDownloads = await downloadClientGateway.FetchDownloadsAsync(client, clientDownloads, cancellationToken);

                    foreach (Download download in updatedDownloads)
                    {
                        var downloadService = scope.ServiceProvider.GetRequiredService<IDownloadService>();
                        var previousDownload = previousDownloads.FirstOrDefault(d => d.Id == download.Id);

                        if (previousDownload != null && !HasSettledAsComplete(download, previousDownload, stabilityWindow))
                        {
                            // Hold the transition, not the update. Progress and size still persist,
                            // so the row stays current; only finalization waits. Reverting the
                            // status rather than skipping the write also means the next cycle sees
                            // the same transition again and can let it through once the window has
                            // passed, without anything else needing to remember it is pending.
                            download.SetStatus(previousDownload.Status);
                            await downloadService.UpdateAsync(download);
                            continue;
                        }

                        await downloadService.UpdateAsync(download);
                        if (previousDownload == null)
                        {
                            continue;
                        }

                        await TriggerCallbacks(client, download, previousDownload, cancellationToken);
                    }

                    if (ShouldRunOrphanCleanup(client))
                    {
                        await CleanupOrphanedDownloadsAsync(
                            client,
                            clientDownloads,
                            clientQueuePoller,
                            orphanCleanupService);
                        ScheduleNextOrphanCleanup(client);
                    }
                }
                catch (DownloadClientAdapterPollingException exception)
                {
                    logger.LogWarning(exception.Message);
                    ScheduleNextClientPollOnFailure(client);
                    continue;
                }
                catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, $"Timeout polling download client {client.Id}; will retry on next schedule");
                    ScheduleNextClientPollOnFailure(client);
                    continue;
                }

                ScheduleNextClientPollOnSuccess(client);
            }
        }

        private bool ShouldRunOrphanCleanup(DownloadClientConfiguration client)
        {
            return !_nextOrphanCleanup.TryGetValue(client.Id, out var nextCleanup) ||
                timeProvider.GetUtcNow() >= nextCleanup;
        }

        private void ScheduleNextOrphanCleanup(DownloadClientConfiguration client)
        {
            var next = timeProvider.GetUtcNow().Add(OrphanCleanupInterval);
            _nextOrphanCleanup.AddOrUpdate(client.Id, next, (_, __) => next);
        }

        private async Task CleanupOrphanedDownloadsAsync(
            DownloadClientConfiguration client,
            List<Download> clientDownloads,
            DownloadClientQueuePoller clientQueuePoller,
            DownloadOrphanCleanupService orphanCleanupService)
        {
            try
            {
                // Durable orphan removal belongs to the monitor worker, not the
                // queue display path. Use the same full-snapshot poller safeguards
                // as queue display so cached, unavailable, or suspiciously empty
                // snapshots never drive destructive cleanup.
                var queueResults = await clientQueuePoller.FetchAsync(
                    [client],
                    OrphanCleanupQueueTimeout,
                    OrphanCleanupStaleSnapshotMaxAge,
                    maxParallelClientPolls: 1);
                var clientQueueResult = queueResults.Single();

                await orphanCleanupService.RemoveOrphansAsync(
                    client,
                    clientQueueResult,
                    clientQueueResult.QueueItems,
                    clientDownloads);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Error cleaning up orphaned downloads for client {ClientName}", client.Name);
            }
        }

        /// <summary>
        /// Compare previous and current download to trigger events
        /// </summary>
        /// <param name="client"></param>
        /// <param name="current">Current updated download</param>
        /// <param name="previous">Previous state of the download</param>
        /// <param name="cancellationToken"></param>
        /// <returns>True if the download has changed state</returns>
        private async Task TriggerCallbacks(DownloadClientConfiguration client, Download current, Download previous, CancellationToken cancellationToken = default)
        {
            switch (previous.Status, current.Status)
            {
                case var (old, next) when old == next:
                    return;
                case (_, DownloadStatus.Failed):
                    await OnDownloadFailed(current, client, current.ErrorMessage ?? "Download failed in client", cancellationToken);
                    break;
                case (_, DownloadStatus.Completed):
                    await OnDownloadCompleted(current);
                    break;
                default:
                    break;
            }
            ;

            await OnDownloadUpdated(current, cancellationToken);
        }

        /// <summary>
        /// Enqueue the processing for the given job
        /// </summary>
        /// <param name="download">Download that was completed</param>
        private async Task OnDownloadCompleted(Download download)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var downloadProcessingJobService = scope.ServiceProvider.GetRequiredService<IDownloadProcessingJobService>();
                var historyRepository = scope.ServiceProvider.GetRequiredService<IHistoryRepository>();
                await historyRepository.AddAsync(new History
                {
                    AudiobookId = download.AudiobookId,
                    AudiobookTitle = download.Title,
                    SourceTitle = download.Title,
                    DownloadId = download.Id.ToUpperInvariant(),
                    DownloadClientId = download.DownloadClientId,
                    EventType = HistoryEvents.DownloadCompleted,
                    Outcome = HistoryOutcome.Succeeded,
                    Source = "DownloadMonitor",
                    Message = "Download client reported completion",
                    Timestamp = DateTime.UtcNow,
                    CorrelationId = download.Id.ToUpperInvariant(),
                    Data = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        download.DownloadPath,
                        download.CompletedAt
                    })
                });
                await downloadProcessingJobService.EnqueueAsync(download);
            }
            catch (InvalidOperationException exception)
            {
                logger.LogError($"Unexpected error after a download was detected completed: {exception.Message}, download will not be queued for importing");
            }
        }

        /// <summary>
        /// Broadcast download updates
        /// </summary>
        private async Task OnDownloadUpdated(Download download, CancellationToken cancellationToken)
        {
            logger.LogInformation($"Broadcasting download update for {download.Id}");
            await downloadPushService.HandlePushAsync(download, cancellationToken);
        }

        private async Task OnDownloadsUpdated(List<Download> downloads, CancellationToken cancellationToken)
        {
            logger.LogInformation($"Broadcasting downloads update");
            await downloadPushService.HandlePushAsync(downloads, cancellationToken);
        }

        private async Task OnDownloadFailed(
            Download download,
            DownloadClientConfiguration client,
            string errorMessage,
            CancellationToken cancellationToken)
        {
            using var scope = scopeFactory.CreateScope();
            var downloadClientGateway = scope.ServiceProvider.GetRequiredService<IDownloadClientGateway>();
            var audiobookRepository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var downloadHistoryService = scope.ServiceProvider.GetRequiredService<IDownloadHistoryService>();
            var configurationService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            var settings = await configurationService.GetApplicationSettingsAsync();
            var downloadService = scope.ServiceProvider.GetRequiredService<IDownloadService>();

            await downloadHistoryService.RecordDownloadFailedAsync(
                download.Id,
                download.DownloadClientId,
                download.Title ?? "Unknown",
                errorMessage);

            if (!settings.FailedDownloadHandlingEnabled)
            {
                return;
            }

            var clientItemId = download.GetExternalId();
            // NZBGet history is part of failure diagnostics and final-path recovery.
            // Do not remove failed NZBGet history here; successful imports remove client
            // history through the post-import cleanup path.
            if (!string.IsNullOrWhiteSpace(clientItemId) &&
                ShouldRemoveFailedClientItem(client))
            {
                await downloadClientGateway.RemoveAsync(client, clientItemId, deleteFiles: false, cancellationToken);
            }

            if (settings.FailedDownloadAutoSearch && download.AudiobookId.HasValue)
            {
                if (ShouldSuppressFailedDownloadAutoSearch(client, download, errorMessage))
                {
                    logger.LogInformation(
                        "Skipping immediate auto-search for failed NZBGet download {DownloadId}; client failure requires user action or manual retry",
                        download.Id);
                    return;
                }

                try
                {
                    var audiobook = await audiobookRepository.GetByIdAsync(download.AudiobookId!.Value);
                    if (audiobook != null && audiobook.Monitored)
                    {
                        await downloadService.SearchAndDownloadAsync(download.AudiobookId.Value);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    logger.LogDebug(ex, "Failed to auto-search after failed download {DownloadId}", download.Id);
                }
            }
        }

        internal static bool ShouldRemoveFailedClientItem(DownloadClientConfiguration client)
        {
            return !string.Equals(client.Type, "nzbget", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool ShouldSuppressFailedDownloadAutoSearch(
            DownloadClientConfiguration client,
            Download download,
            string errorMessage)
        {
            if (!string.Equals(client.Type, "nzbget", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var clientFailureReason = download.GetMetadataString("ClientFailureReason") ?? errorMessage;
            return NzbgetFailureMessageMapper.IsMoveOrPostProcessingFailure(clientFailureReason);
        }
    }
}
