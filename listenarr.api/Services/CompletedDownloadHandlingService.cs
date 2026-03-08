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

using Listenarr.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Api.Services
{
    /// <summary>
    /// Background service that handles completed downloads for import.
    /// 
    /// This implements the CompletedDownloadService pattern:
    /// 1. Monitor detects completion (Status = Completed)
    /// 2. This handler validates and processes completed downloads
    /// 3. Triggers finalization and import pipeline
    /// 
    /// Runs every 10 seconds to check for completed downloads ready for import.
    /// </summary>
    public class CompletedDownloadHandlingService : BackgroundService
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly Microsoft.EntityFrameworkCore.IDbContextFactory<ListenArrDbContext> _dbFactory;
        private readonly ILogger<CompletedDownloadHandlingService> _logger;
        private TimeSpan _pollingInterval = TimeSpan.FromSeconds(10);
        
        // Track downloads that are in the completion pipeline to avoid duplicate processing
        private readonly Dictionary<string, DateTime> _processingDownloads = new();
        private readonly Lock _processingLock = new();

        public CompletedDownloadHandlingService(
            IServiceScopeFactory serviceScopeFactory,
            Microsoft.EntityFrameworkCore.IDbContextFactory<ListenArrDbContext> dbFactory,
            ILogger<CompletedDownloadHandlingService> logger)
        {
            _serviceScopeFactory = serviceScopeFactory;
            _dbFactory = dbFactory;
            _logger = logger;
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("CompletedDownloadHandlingService starting");
            
            // Load polling interval from settings
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
                var settings = await configService.GetApplicationSettingsAsync();
                if (settings != null && settings.PollingIntervalSeconds > 0)
                {
                    _pollingInterval = TimeSpan.FromSeconds(settings.PollingIntervalSeconds);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("CompletedDownloadHandlingService startup canceled");
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning(ex, "CompletedDownloadHandlingService settings load canceled/timed out during startup; using default interval");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogWarning(ex, "Failed to load polling interval from settings, using default");
            }

            await base.StartAsync(cancellationToken);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("CompletedDownloadHandlingService stopping");
            await base.StopAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("CompletedDownloadHandlingService background task started");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessCompletedDownloadsAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException ex)
                {
                    _logger.LogWarning(ex, "CompletedDownloadHandlingService cycle canceled/timed out; continuing");
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogError(ex, "Error in CompletedDownloadHandlingService");
                }

                // Wait before next poll
                try
                {
                    await Task.Delay(_pollingInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _logger.LogInformation("CompletedDownloadHandlingService background task stopped");
        }

        private async Task ProcessCompletedDownloadsAsync(CancellationToken cancellationToken)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var dbContext = _dbFactory.CreateDbContext();
            var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            var downloadService = scope.ServiceProvider.GetRequiredService<IDownloadService>();

            try
            {
                // Find all downloads that are Completed/ImportPending but not yet imported (FinalPath still empty)
                var completedDownloads = await dbContext.Downloads
                    .Where(d =>
                        (d.Status == DownloadStatus.Completed || d.Status == DownloadStatus.ImportPending) &&
                        string.IsNullOrEmpty(d.FinalPath))
                    .ToListAsync(cancellationToken);

                // Skip completed downloads for disabled/missing external clients.
                // They stay alive so they resume automatically when the client is re-enabled.
                // DDL entries are internal and not tied to external client configuration.
                HashSet<string> enabledClientIds;
                try
                {
                    var configuredClients = await configService.GetDownloadClientConfigurationsAsync();
                    enabledClientIds = configuredClients
                        .Where(c => c.IsEnabled && !string.IsNullOrWhiteSpace(c.Id))
                        .Select(c => c.Id)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogDebug(ex, "CompletedDownloadHandlingService failed to load download client configurations; skipping external-client completed processing for this cycle");
                    enabledClientIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                var completedDownloadsAll = completedDownloads;
                completedDownloads = completedDownloadsAll
                    .Where(d =>
                        string.Equals(d.DownloadClientId, "DDL", StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(d.DownloadClientId) && enabledClientIds.Contains(d.DownloadClientId)))
                    .ToList();

                var skippedDisabledClientDownloads = completedDownloadsAll.Count - completedDownloads.Count;
                if (skippedDisabledClientDownloads > 0)
                {
                    _logger.LogDebug("CompletedDownloadHandlingService skipping {Count} completed downloads from disabled or missing download clients", skippedDisabledClientDownloads);
                }

                if (completedDownloads.Count == 0)
                {
                    // No completed downloads to process — skip import loop but still run deferred removals below
                }

                if (completedDownloads.Count > 0)
                {
                _logger.LogInformation("CompletedDownloadHandlingService found {Count} completed downloads to process", completedDownloads.Count);

                // Load configuration for stability window
                var settings = await configService.GetApplicationSettingsAsync();
                var stabilitySeconds = settings?.DownloadCompletionStabilitySeconds ?? 30;
                var stabilityWindow = TimeSpan.FromSeconds(stabilitySeconds);

                // Get the queue to check client status
                var queue = await downloadService.GetQueueAsync();
                var queueById = queue.ToDictionary(q => q.Id, q => q);

                foreach (var download in completedDownloads)
                {
                    try
                    {
                        // Skip if already being processed
                        lock (_processingLock)
                        {
                            if (_processingDownloads.TryGetValue(download.Id, out var firstSeen))
                            {
                                if (DateTime.UtcNow - firstSeen > TimeSpan.FromMinutes(5))
                                {
                                    // Been processing for too long, reset and retry
                                    _logger.LogWarning("Download {DownloadId} has been processing for over 5 minutes, resetting", download.Id);
                                    _processingDownloads.Remove(download.Id);
                                }
                                else
                                {
                                    _logger.LogDebug("Skipping download {DownloadId} - already being processed", download.Id);
                                    continue;
                                }
                            }

                            // Mark as processing
                            _processingDownloads[download.Id] = DateTime.UtcNow;
                        }

                        _logger.LogInformation("Processing completed download {DownloadId}: {Title}", download.Id, download.Title);

                        // Find the queue item for this download
                        if (!queueById.TryGetValue(download.Id, out var queueItem))
                        {
                            _logger.LogWarning("Completed download {DownloadId} not found in queue, may have been removed from client", download.Id);
                            lock (_processingLock)
                            {
                                _processingDownloads.Remove(download.Id);
                            }
                            continue;
                        }

                        // Check stability window - ensure download has been complete for minimum time
                        // If CompletionTime is set, use it; otherwise use queue item's AddedAt as conservative estimate
                        // (means download must exist in queue for stabilityWindow duration before processing)
                        var completionTimeToCheck = queueItem.CompletionTime ?? queueItem.AddedAt;
                        var timeSinceCompletion = DateTime.UtcNow - completionTimeToCheck;

                        if (timeSinceCompletion < stabilityWindow)
                        {
                            _logger.LogDebug("Download {DownloadId} in stability window ({Elapsed:F1}s / {Stability}s remaining)",
                                download.Id, timeSinceCompletion.TotalSeconds, stabilityWindow.TotalSeconds);
                            lock (_processingLock)
                            {
                                _processingDownloads.Remove(download.Id);
                            }
                            continue;
                        }

                        _logger.LogInformation("Download {DownloadId} passed stability window ({Elapsed:F1}s), starting import process",
                            download.Id, timeSinceCompletion.TotalSeconds);

                        // Trigger the import pipeline via DownloadService
                        try
                        {
                            var contentPath = queueItem.ContentPath ?? queueItem.LocalPath ?? queueItem.RemotePath ?? "";
                            await downloadService.ProcessCompletedDownloadAsync(download.Id, contentPath);
                            _logger.LogInformation("Successfully triggered import for completed download {DownloadId}", download.Id);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                            _logger.LogError(ex, "Failed to process completed download {DownloadId}", download.Id);
                        }

                        lock (_processingLock)
                        {
                            _processingDownloads.Remove(download.Id);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                        _logger.LogError(ex, "Error processing completed download {DownloadId}", download.Id);
                        lock (_processingLock)
                        {
                            _processingDownloads.Remove(download.Id);
                        }
                    }
                }
            }
            } // end if (completedDownloads.Count > 0)
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning(ex, "CompletedDownloadHandlingService processing canceled/timed out");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error in CompletedDownloadHandlingService");
            }

            // Deferred removal: retry removal for imports that were completed but couldn't
            // be removed from the download client (CanBeRemoved was false at import time).
            // The DownloadMonitorService updates CanBeRemoved in metadata on each poll cycle,
            // so eventually the torrent will reach its seed limit and become removable.
            try
            {
                await ProcessDeferredRemovalsAsync(dbContext, scope, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Error processing deferred removals");
            }
        }

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
        private async Task ProcessDeferredRemovalsAsync(
            ListenArrDbContext dbContext,
            IServiceScope scope,
            CancellationToken cancellationToken)
        {
            var movedDownloads = await dbContext.Downloads
                .Where(d => d.Status == DownloadStatus.Moved && !string.IsNullOrEmpty(d.DownloadClientId))
                .ToListAsync(cancellationToken);

            if (movedDownloads.Count == 0) return;

            var configService = scope.ServiceProvider.GetService<IConfigurationService>();
            var downloadClientGateway = scope.ServiceProvider.GetService<IDownloadClientGateway>();
            if (configService == null || downloadClientGateway == null) return;

            // Pre-load all enabled client configurations for cross-client removal fallback
            List<DownloadClientConfiguration> allEnabledClients;
            try
            {
                var allClients = await configService.GetDownloadClientConfigurationsAsync();
                allEnabledClients = allClients.Where(c => c.IsEnabled).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to load client configurations for deferred removal");
                return;
            }

            foreach (var download in movedDownloads)
            {
                try
                {
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
                        _logger.LogInformation(
                            "Deferred removal: Download {DownloadId} CanBeRemoved not set after {Hours:F1}h — " +
                            "treating as removable (possible client ID mismatch)", download.Id, timeSinceCompleted.Value.TotalHours);
                        canBeRemoved = true;
                    }

                    if (!canBeRemoved)
                    {
                        _logger.LogDebug("Deferred removal: Download {DownloadId} still not removable", download.Id);
                        continue;
                    }

                    var clientConfig = await configService.GetDownloadClientConfigurationAsync(download.DownloadClientId);
                    if (clientConfig != null && !clientConfig.IsEnabled)
                    {
                        _logger.LogDebug("Deferred removal: Skipping download {DownloadId} — client {ClientName} is disabled",
                            download.Id, clientConfig.Name);
                        continue;
                    }

                    // Determine removal policy from any configured client (prefer primary)
                    var removalPolicy = clientConfig?.RemoveCompletedDownloads;
                    if (string.IsNullOrEmpty(removalPolicy) || removalPolicy == "none")
                    {
                        // Check if ANY enabled client has removal configured (for cross-client scenarios)
                        var anyRemovalClient = allEnabledClients
                            .FirstOrDefault(c => !string.IsNullOrEmpty(c.RemoveCompletedDownloads) && c.RemoveCompletedDownloads != "none");
                        if (anyRemovalClient == null)
                        {
                            // Removal not configured on any client, just delete the DB record
                            dbContext.Downloads.Remove(download);
                            await dbContext.SaveChangesAsync(cancellationToken);
                            _logger.LogInformation("Deferred removal: Cleaned up DB record for {DownloadId} (removal not configured)", download.Id);
                            continue;
                        }
                        removalPolicy = anyRemovalClient.RemoveCompletedDownloads;
                    }

                    bool deleteFiles = removalPolicy == "remove_and_delete";

                    // Resolve the client-side ID (prefer TorrentHash for torrents)
                    string? torrentHash = null;
                    if (download.Metadata != null && download.Metadata.TryGetValue("TorrentHash", out var hashObj))
                    {
                        torrentHash = hashObj?.ToString();
                    }
                    string clientId = !string.IsNullOrEmpty(torrentHash) ? torrentHash : download.Id;

                    // Attempt 1: Try primary client
                    bool removed = false;
                    if (clientConfig != null)
                    {
                        try
                        {
                            removed = await downloadClientGateway.RemoveAsync(clientConfig, clientId, deleteFiles);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogDebug(ex, "Deferred removal: Primary client {ClientName} removal failed for {DownloadId}",
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
                                    _logger.LogInformation(
                                        "Deferred removal: Cross-client removal succeeded — removed {DownloadId} from {ClientName} " +
                                        "(was assigned to {OriginalClientId})",
                                        download.Id, altClient.Name, download.DownloadClientId);
                                    break;
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                _logger.LogDebug(ex, "Deferred removal: Cross-client removal from {ClientName} failed for {DownloadId}",
                                    altClient.Name, download.Id);
                            }
                        }
                    }

                    if (removed)
                    {
                        _logger.LogInformation("Deferred removal: Successfully removed {DownloadId} (deleteFiles={DeleteFiles})",
                            download.Id, deleteFiles);
                        dbContext.Downloads.Remove(download);
                        await dbContext.SaveChangesAsync(cancellationToken);
                    }
                    else if (timeSinceCompleted.HasValue && timeSinceCompleted.Value > TimeSpan.FromHours(24))
                    {
                        // All removal attempts failed and the download has been Moved for > 24h.
                        // The item was likely already removed from the client manually, or the
                        // DownloadClientId is wrong and the torrent is unrecoverable.
                        // Clean up the DB record to stop the endless polling loop.
                        _logger.LogWarning(
                            "Deferred removal: All removal attempts failed for {DownloadId} after {Hours:F1}h — " +
                            "cleaning up stale DB record (import already completed)",
                            download.Id, timeSinceCompleted.Value.TotalHours);
                        dbContext.Downloads.Remove(download);
                        await dbContext.SaveChangesAsync(cancellationToken);
                    }
                    else
                    {
                        _logger.LogDebug("Deferred removal: Failed to remove {DownloadId}, will retry next cycle", download.Id);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogDebug(ex, "Error processing deferred removal for {DownloadId}", download.Id);
                }
            }
        }
    }
}

