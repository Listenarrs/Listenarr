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
using System.Text.Json;

namespace Listenarr.Infrastructure.Downloads.Cleanup
{
    /// <summary>
    /// Background service that handles moved downloads to remove them from client
    /// Runs every 10 seconds to check for moved downloads
    /// </summary>
    public class MovedDownloadCleanupService(
        IMovedDownloadCleanupProcessor processor,
        ILogger<MovedDownloadCleanupService> logger,
        IWorkerCycleRunner cycleRunner,
        IServiceScopeFactory scopeFactory) : BackgroundService
    {
        private TimeSpan _pollingInterval = TimeSpan.FromSeconds(10);

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("CompletedDownloadHandlingService starting");

            try
            {
                using var scope = scopeFactory.CreateScope();
                var configurationService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
                var settings = await configurationService.GetApplicationSettingsAsync();
                if (settings.PollingIntervalSeconds > 0)
                {
                    _pollingInterval = TimeSpan.FromSeconds(settings.PollingIntervalSeconds);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("CompletedDownloadHandlingService startup canceled");
            }
            catch (OperationCanceledException ex)
            {
                logger.LogWarning(ex, "CompletedDownloadHandlingService settings load canceled/timed out during startup; using default interval");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Failed to load polling interval from settings, using default");
            }

            await base.StartAsync(cancellationToken);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("CompletedDownloadHandlingService stopping");
            await base.StopAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("CompletedDownloadHandlingService background task started");

            await cycleRunner.RunPeriodicAsync(
                nameof(MovedDownloadCleanupService),
                initialDelay: null,
                intervalProvider: () => _pollingInterval,
                runCycle: processor.RunCycleAsync,
                cancellationToken);

            logger.LogInformation("CompletedDownloadHandlingService background task stopped");
        }
    }

    public class MovedDownloadCleanupProcessor(
        IServiceScopeFactory scopeFactory,
        ILogger<MovedDownloadCleanupProcessor> logger) : IMovedDownloadCleanupProcessor
    {
        /// <summary>
        /// Processes deferred removals for downloads that have been imported (Status == Moved)
        /// but couldn't be removed from the client because the torrent hadn't reached its seed limit.
        /// Checks metadata CanBeRemoved flag which is updated by DownloadMonitorService on each poll.
        /// 
        /// Handles edge cases:
        /// - CanBeRemoved never set (download assigned to wrong client type, e.g. NZBGet for a torrent)
        /// - Primary client removal fails (cross-client fallback using TorrentHash)
        /// - All removal attempts fail (stale DB record cleanup after grace period)
        /// </summary>
        public async Task RunCycleAsync(CancellationToken cancellationToken)
        {
            using var scope = scopeFactory.CreateScope();
            var downloadRepository = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
            var configurationService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            var downloadClientGateway = scope.ServiceProvider.GetRequiredService<IDownloadClientGateway>();
            var processingJobRepository = scope.ServiceProvider.GetRequiredService<IDownloadProcessingJobRepository>();
            var historyRepository = scope.ServiceProvider.GetRequiredService<IHistoryRepository>();

            var movedDownloads = (await downloadRepository.GetActiveAsync())
                .Where(d => d.Status == DownloadStatus.Moved)
                .ToList();

            if (movedDownloads.Count == 0) return;

            // Pre-load all enabled client configurations for cross-client removal fallback
            List<DownloadClientConfiguration> allEnabledClients;
            try
            {
                var allClients = await configurationService.GetDownloadClientConfigurationsAsync();
                allEnabledClients = allClients.Where(c => c.IsEnabled).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogDebug(ex, "Failed to load client configurations for deferred removal");
                return;
            }

            foreach (var download in movedDownloads)
            {
                try
                {
                    var completedJob = (await processingJobRepository.GetByDownloadIdAsync(download.Id))
                        .Where(job => job.Status == ProcessingJobStatus.Completed)
                        .OrderByDescending(job => job.CompletedAt)
                        .FirstOrDefault();
                    if (completedJob == null)
                    {
                        logger.LogWarning(
                            "Deferred removal: Download {DownloadId} is Moved without a completed import job; cleanup is blocked",
                            download.Id);
                        continue;
                    }

                    var correlationId = completedJob.GetOrCreateCorrelationId();

                    // Check if CanBeRemoved is now true (updated by DownloadMonitorService)
                    bool canBeRemoved = false;
                    if (download.Metadata != null && download.Metadata.TryGetValue("CanBeRemoved", out var canRemoveObj))
                    {
                        canBeRemoved = canRemoveObj is bool b ? b : (canRemoveObj is System.Text.Json.JsonElement je ? je.GetBoolean() : bool.TryParse(canRemoveObj?.ToString(), out var parsed) && parsed);
                    }

                    // Fallback: if CanBeRemoved was never set (e.g. download assigned to wrong
                    // client type — NZBGet record for a Transmission torrent — so the correct
                    // poller never updates it), treat as removable after a grace period.
                    // The import is already done (status == Moved), so cleaning up is safe.
                    var timeSinceCompleted = download.CompletedAt.HasValue
                        ? DateTime.UtcNow - download.CompletedAt.Value
                        : (TimeSpan?)null;

                    if (!canBeRemoved && timeSinceCompleted.HasValue && timeSinceCompleted.Value > TimeSpan.FromHours(2))
                    {
                        logger.LogInformation(
                            "Deferred removal: Download {DownloadId} CanBeRemoved not set after {Hours:F1}h — " +
                            "treating as removable (possible client ID mismatch)", download.Id, timeSinceCompleted.Value.TotalHours);
                        canBeRemoved = true;
                    }

                    if (!canBeRemoved)
                    {
                        logger.LogDebug("Deferred removal: Download {DownloadId} still not removable", download.Id);
                        continue;
                    }

                    var clientConfig = await configurationService.GetDownloadClientConfigurationAsync(download.DownloadClientId);
                    if (clientConfig != null && !clientConfig.IsEnabled)
                    {
                        logger.LogDebug("Deferred removal: Skipping download {DownloadId} — client {ClientName} is disabled",
                            download.Id, clientConfig.Name);
                        continue;
                    }

                    // Determine removal policy from any configured client (prefer primary)
                    var removalPolicy = clientConfig?.RemoveCompletedDownloads;
                    if (string.IsNullOrEmpty(removalPolicy) || removalPolicy == "none")
                    {
                        // "None" means retain both the client item and the durable imported record.
                        logger.LogDebug("Deferred removal: Retaining imported download {DownloadId}; client action is none", download.Id);
                        continue;
                    }

                    bool deleteFiles = removalPolicy == "remove_and_delete";

                    // Resolve the client-side ID (prefer TorrentHash for torrents, then ClientDownloadId for usenet)
                    string? torrentHash = null;
                    if (download.Metadata != null && download.Metadata.TryGetValue("TorrentHash", out var hashObj))
                    {
                        torrentHash = hashObj?.ToString();
                    }
                    string? clientDownloadId = null;
                    if (download.Metadata != null && download.Metadata.TryGetValue("ClientDownloadId", out var clientIdObj))
                    {
                        clientDownloadId = clientIdObj?.ToString();
                    }
                    string clientId = !string.IsNullOrEmpty(torrentHash) ? torrentHash
                        : !string.IsNullOrEmpty(clientDownloadId) ? clientDownloadId
                        : download.Id;

                    // Attempt 1: Try primary client
                    bool removed = false;
                    await AddCleanupHistoryAsync(
                        historyRepository,
                        download,
                        HistoryEvents.CleanupRequested,
                        HistoryOutcome.Requested,
                        correlationId,
                        $"Client cleanup requested ({removalPolicy})",
                        new Dictionary<string, object>
                        {
                            ["ProcessingJobId"] = completedJob.Id,
                            ["RemovalPolicy"] = removalPolicy,
                            ["DeleteFiles"] = deleteFiles
                        },
                        cancellationToken);
                    if (clientConfig != null)
                    {
                        try
                        {
                            removed = await downloadClientGateway.RemoveAsync(clientConfig, clientId, deleteFiles);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            logger.LogDebug(ex, "Deferred removal: Primary client {ClientName} removal failed for {DownloadId}",
                                clientConfig.Name, download.Id);
                        }
                    }

                    // Attempt 2: Cross-client fallback — if primary failed and we have a torrent
                    // hash, try all other enabled torrent clients. This handles cases where the
                    // download was recorded under the wrong DownloadClientId.
                    if (!removed && !string.IsNullOrEmpty(torrentHash))
                    {
                        var torrentClientTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            { "qbittorrent", "transmission" };
                        var otherTorrentClients = allEnabledClients
                            .Where(c => torrentClientTypes.Contains(c.Type ?? "") &&
                                        c.Id != download.DownloadClientId)
                            .ToList();

                        foreach (var altClient in otherTorrentClients)
                        {
                            try
                            {
                                var altDeleteFiles = altClient.RemoveCompletedDownloads == "remove_and_delete";
                                removed = await downloadClientGateway.RemoveAsync(altClient, torrentHash, altDeleteFiles);
                                if (removed)
                                {
                                    logger.LogInformation(
                                        "Deferred removal: Cross-client removal succeeded — removed {DownloadId} from {ClientName} " +
                                        "(was assigned to {OriginalClientId})",
                                        download.Id, altClient.Name, download.DownloadClientId);
                                    break;
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                logger.LogDebug(ex, "Deferred removal: Cross-client removal from {ClientName} failed for {DownloadId}",
                                    altClient.Name, download.Id);
                            }
                        }
                    }

                    if (removed)
                    {
                        logger.LogInformation("Deferred removal: Successfully removed {DownloadId} (deleteFiles={DeleteFiles})",
                            download.Id, deleteFiles);
                        await AddCleanupHistoryAsync(
                            historyRepository,
                            download,
                            HistoryEvents.CleanupSucceeded,
                            HistoryOutcome.Succeeded,
                            correlationId,
                            "Client cleanup completed",
                            new Dictionary<string, object>
                            {
                                ["ProcessingJobId"] = completedJob.Id,
                                ["RemovalPolicy"] = removalPolicy,
                                ["DeleteFiles"] = deleteFiles
                            },
                            cancellationToken);
                        await downloadRepository.RemoveAsync(download.Id);
                    }
                    else if (timeSinceCompleted.HasValue && timeSinceCompleted.Value > TimeSpan.FromHours(24))
                    {
                        logger.LogWarning(
                            "Deferred removal: All removal attempts failed for {DownloadId} after {Hours:F1}h — " +
                            "retaining the operational record for a future retry",
                            download.Id, timeSinceCompleted.Value.TotalHours);
                        await AddCleanupHistoryAsync(
                            historyRepository,
                            download,
                            HistoryEvents.CleanupFailed,
                            HistoryOutcome.Failed,
                            correlationId,
                            "Client cleanup failed after the grace period; operational record retained",
                            new Dictionary<string, object>
                            {
                                ["ProcessingJobId"] = completedJob.Id,
                                ["RemovalPolicy"] = removalPolicy,
                                ["OperationalRecordRemoved"] = false
                            },
                            cancellationToken);
                    }
                    else
                    {
                        logger.LogDebug("Deferred removal: Failed to remove {DownloadId}, will retry next cycle", download.Id);
                        await AddCleanupHistoryAsync(
                            historyRepository,
                            download,
                            HistoryEvents.CleanupFailed,
                            HistoryOutcome.Retrying,
                            correlationId,
                            "Client cleanup failed and will be retried",
                            new Dictionary<string, object>
                            {
                                ["ProcessingJobId"] = completedJob.Id,
                                ["RemovalPolicy"] = removalPolicy
                            },
                            cancellationToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    logger.LogDebug(ex, "Error processing deferred removal for {DownloadId}", download.Id);
                }
            }
        }

        private static Task AddCleanupHistoryAsync(
            IHistoryRepository historyRepository,
            Download download,
            string eventType,
            HistoryOutcome outcome,
            string correlationId,
            string message,
            Dictionary<string, object> details,
            CancellationToken ct) =>
            historyRepository.AddAsync(new History
            {
                AudiobookId = download.AudiobookId,
                AudiobookTitle = download.Title,
                SourceTitle = download.Title,
                DownloadId = download.Id.ToUpperInvariant(),
                DownloadClientId = download.DownloadClientId,
                EventType = eventType,
                Outcome = outcome,
                Source = "DownloadCleanup",
                Message = message,
                Error = outcome == HistoryOutcome.Failed ? message : null,
                Timestamp = DateTime.UtcNow,
                CorrelationId = correlationId,
                Data = JsonSerializer.Serialize(details)
            }, ct);
    }
}
