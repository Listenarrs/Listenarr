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
    public class MovedDownloadCleanupProcessor(
        IServiceScopeFactory scopeFactory,
        ILogger<MovedDownloadCleanupProcessor> logger) : IMovedDownloadCleanupProcessor
    {
        private static readonly TimeSpan ClientRemovalGracePeriod = TimeSpan.FromHours(2);
        private static readonly TimeSpan FailedCleanupWarningGracePeriod = TimeSpan.FromHours(24);
        private static readonly TimeSpan LegacyMovedProofGracePeriod = TimeSpan.FromDays(7);

        private enum ImportProofKind
        {
            None,
            CompletedProcessingJob,
            LastImportedAt,
            ImportedHistory,
            LegacyDownloadHistory,
            LegacyMovedState
        }

        private sealed record ImportProof(
            ImportProofKind Kind,
            string CorrelationId,
            string? ProcessingJobId,
            DateTime? ProvenAt,
            bool? SourceRetained = null)
        {
            public bool AllowsDestructiveCleanup =>
                Kind is not ImportProofKind.LegacyMovedState
                && SourceRetained is false;
        }

        /// <summary>
        /// Processes deferred removals for downloads that have been imported (Status == Moved)
        /// but couldn't be removed from the client because the torrent hadn't reached its seed limit.
        /// Checks metadata CanBeRemoved flag which is updated by DownloadMonitorService on each poll.
        /// </summary>
        public async Task RunCycleAsync(CancellationToken cancellationToken)
        {
            using var scope = scopeFactory.CreateScope();
            var downloadRepository = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
            var configurationService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            var downloadClientGateway = scope.ServiceProvider.GetRequiredService<IDownloadClientGateway>();
            var processingJobRepository = scope.ServiceProvider.GetRequiredService<IDownloadProcessingJobRepository>();
            var historyRepository = scope.ServiceProvider.GetRequiredService<IHistoryRepository>();
            var downloadHistoryRepository = scope.ServiceProvider.GetRequiredService<IDownloadHistoryRepository>();

            var movedDownloads = (await downloadRepository.GetActiveAsync())
                .Where(d => d.Status == DownloadStatus.Moved)
                .ToList();

            if (movedDownloads.Count == 0) return;

            // Pre-load enabled clients once so torrent cross-client cleanup does not reload
            // configuration for every moved download in a cleanup cycle.
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
                    var clientConfig = await configurationService.GetDownloadClientConfigurationAsync(download.DownloadClientId);
                    if (clientConfig == null)
                    {
                        logger.LogDebug(
                            "Deferred removal: Skipping download {DownloadId} because client {ClientId} no longer exists",
                            download.Id,
                            download.DownloadClientId);
                        continue;
                    }

                    if (!clientConfig.IsEnabled)
                    {
                        logger.LogDebug("Deferred removal: Skipping download {DownloadId} — client {ClientName} is disabled",
                            download.Id, clientConfig.Name);
                        continue;
                    }

                    var removalPolicy = clientConfig.RemoveCompletedDownloads;
                    if (string.IsNullOrEmpty(removalPolicy) || removalPolicy == "none")
                    {
                        // Policy none is an intentional retention choice, so do not resolve import
                        // proof, infer removability, or write cleanup history for these records.
                        logger.LogDebug("Deferred removal: Retaining imported download {DownloadId}; client action is none", download.Id);
                        continue;
                    }

                    var proof = await ResolveImportProofAsync(
                        download,
                        processingJobRepository,
                        historyRepository,
                        downloadHistoryRepository,
                        cancellationToken);
                    if (proof.Kind == ImportProofKind.None)
                    {
                        logger.LogWarning(
                            "Deferred removal: Download {DownloadId} is Moved without durable import proof; cleanup is blocked",
                            download.Id);
                        continue;
                    }

                    var hasCanBeRemoved = false;
                    var canBeRemoved = false;
                    if (download.Metadata != null && download.Metadata.TryGetValue("CanBeRemoved", out var canRemoveObj))
                    {
                        hasCanBeRemoved = true;
                        canBeRemoved = canRemoveObj is bool b
                            ? b
                            : canRemoveObj is JsonElement je
                                ? je.GetBoolean()
                                : bool.TryParse(canRemoveObj?.ToString(), out var parsed) && parsed;
                    }

                    // Only infer removability when the client never reported CanBeRemoved. A stored
                    // false value means the client explicitly says cleanup is not ready yet.
                    var timeSinceImportProof = proof.ProvenAt.HasValue
                        ? DateTime.UtcNow - proof.ProvenAt.Value
                        : (TimeSpan?)null;
                    if (!canBeRemoved && !hasCanBeRemoved &&
                        timeSinceImportProof.HasValue &&
                        timeSinceImportProof.Value > ClientRemovalGracePeriod)
                    {
                        logger.LogDebug(
                            "Deferred removal: Download {DownloadId} CanBeRemoved not set after {Hours:F1}h — " +
                            "treating as removable (possible client ID mismatch)", download.Id, timeSinceImportProof.Value.TotalHours);
                        canBeRemoved = true;
                    }

                    if (!canBeRemoved)
                    {
                        logger.LogDebug("Deferred removal: Download {DownloadId} still not removable", download.Id);
                        continue;
                    }

                    var deleteFiles = removalPolicy == "remove_and_delete";
                    if (deleteFiles && !proof.AllowsDestructiveCleanup)
                    {
                        var reason = proof.Kind == ImportProofKind.LegacyMovedState
                            ? "only legacy Moved-state import proof is available"
                            : proof.SourceRetained is true
                                ? "the import retained its source"
                                : "the source-retention disposition is unavailable";
                        logger.LogWarning(
                            "Deferred removal: Download {DownloadId} cannot be deleted because {Reason}; " +
                            "remove_and_delete was downgraded to remove",
                            download.Id,
                            reason);
                        deleteFiles = false;
                    }

                    if (proof.Kind == ImportProofKind.LegacyMovedState)
                    {
                        logger.LogInformation(
                            "Deferred removal: Attempting non-destructive legacy cleanup for download {DownloadId}",
                            download.Id);
                    }

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

                    var removed = false;
                    await AddCleanupHistoryAsync(
                        historyRepository,
                        download,
                        HistoryEvents.CleanupRequested,
                        HistoryOutcome.Requested,
                        proof.CorrelationId,
                        $"Client cleanup requested ({removalPolicy})",
                        BuildCleanupDetails(proof, removalPolicy, deleteFiles),
                        cancellationToken);

                    try
                    {
                        removed = await downloadClientGateway.RemoveAsync(clientConfig, clientId, deleteFiles);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        logger.LogDebug(ex, "Deferred removal: Primary client {ClientName} removal failed for {DownloadId}",
                            clientConfig.Name, download.Id);
                    }

                    // If the primary client did not remove a torrent, try other enabled torrent
                    // clients by hash. This keeps cleanup resilient to older records assigned to
                    // the wrong DownloadClientId.
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
                                var altDeleteFiles = proof.AllowsDestructiveCleanup &&
                                    altClient.RemoveCompletedDownloads == "remove_and_delete";
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
                            proof.CorrelationId,
                            "Client cleanup completed",
                            BuildCleanupDetails(proof, removalPolicy, deleteFiles),
                            cancellationToken);
                        await downloadRepository.RemoveAsync(download.Id);
                    }
                    else if (timeSinceImportProof.HasValue && timeSinceImportProof.Value > FailedCleanupWarningGracePeriod)
                    {
                        logger.LogWarning(
                            "Deferred removal: All removal attempts failed for {DownloadId} after {Hours:F1}h — " +
                            "retaining the operational record for a future retry",
                            download.Id, timeSinceImportProof.Value.TotalHours);
                        var details = BuildCleanupDetails(proof, removalPolicy, deleteFiles);
                        details["OperationalRecordRemoved"] = false;
                        await AddCleanupHistoryAsync(
                            historyRepository,
                            download,
                            HistoryEvents.CleanupFailed,
                            HistoryOutcome.Failed,
                            proof.CorrelationId,
                            "Client cleanup failed after the grace period; operational record retained",
                            details,
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
                            proof.CorrelationId,
                            "Client cleanup failed and will be retried",
                            BuildCleanupDetails(proof, removalPolicy, deleteFiles),
                            cancellationToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    logger.LogDebug(ex, "Error processing deferred removal for {DownloadId}", download.Id);
                }
            }
        }

        private static async Task<ImportProof> ResolveImportProofAsync(
            Download download,
            IDownloadProcessingJobRepository processingJobRepository,
            IHistoryRepository historyRepository,
            IDownloadHistoryRepository downloadHistoryRepository,
            CancellationToken cancellationToken)
        {
            var persistedSourceRetained = bool.TryParse(
                download.GetMetadataString(Download.SourceRetainedMetadataKey),
                out var parsedPersistedSourceRetained)
                    ? parsedPersistedSourceRetained
                    : (bool?)null;
            var completedJob = (await processingJobRepository.GetByDownloadIdAsync(download.Id))
                .Where(job => job.Status == ProcessingJobStatus.Completed)
                .OrderByDescending(job => job.CompletedAt ?? job.CreatedAt)
                .FirstOrDefault();
            if (completedJob != null)
            {
                return new ImportProof(
                    ImportProofKind.CompletedProcessingJob,
                    completedJob.GetOrCreateCorrelationId(),
                    completedJob.Id,
                    completedJob.CompletedAt,
                    completedJob.TryGetJobDataString(
                            Download.SourceRetainedMetadataKey,
                            out var retainedValue)
                        && bool.TryParse(retainedValue, out var sourceRetained)
                            ? sourceRetained
                            : persistedSourceRetained);
            }

            var importedHistory = await historyRepository.GetSucceededImportedByDownloadIdAsync(
                download.Id,
                cancellationToken);
            if (importedHistory != null)
            {
                return new ImportProof(
                    ImportProofKind.ImportedHistory,
                    importedHistory.CorrelationId ?? download.Id.ToUpperInvariant(),
                    null,
                    importedHistory.Timestamp,
                    persistedSourceRetained ?? ReadSourceRetained(importedHistory.Data));
            }

            if (download.LastImportedAt.HasValue)
            {
                return new ImportProof(
                    ImportProofKind.LastImportedAt,
                    download.Id.ToUpperInvariant(),
                    null,
                    download.LastImportedAt.Value,
                    persistedSourceRetained);
            }

            var legacyDownloadHistory = await downloadHistoryRepository.GetImportedByDownloadIdAsync(
                download.Id,
                cancellationToken);
            if (legacyDownloadHistory != null)
            {
                return new ImportProof(
                    ImportProofKind.LegacyDownloadHistory,
                    download.Id.ToUpperInvariant(),
                    null,
                    legacyDownloadHistory.ImportedAt ?? legacyDownloadHistory.EventDate);
            }

            var oldestHistoryAt = await historyRepository.GetOldestTimestampByDownloadIdAsync(
                download.Id,
                cancellationToken);
            if (oldestHistoryAt.HasValue && DateTime.UtcNow - oldestHistoryAt.Value > LegacyMovedProofGracePeriod)
            {
                // Older builds used Moved as the only durable import marker. This is enough
                // to clean stale client/DB state, but not enough to delete external files.
                return new ImportProof(
                    ImportProofKind.LegacyMovedState,
                    download.Id.ToUpperInvariant(),
                    null,
                    oldestHistoryAt.Value);
            }

            return new ImportProof(
                ImportProofKind.None,
                download.Id.ToUpperInvariant(),
                null,
                null);
        }

        private static Dictionary<string, object> BuildCleanupDetails(
            ImportProof proof,
            string removalPolicy,
            bool deleteFiles)
        {
            var details = new Dictionary<string, object>
            {
                ["ImportProof"] = proof.Kind.ToString(),
                ["RemovalPolicy"] = removalPolicy,
                ["DeleteFiles"] = deleteFiles,
                ["SourceRetentionKnown"] = proof.SourceRetained.HasValue
            };

            if (proof.SourceRetained.HasValue)
            {
                details[Download.SourceRetainedMetadataKey] = proof.SourceRetained.Value;
            }

            if (!string.IsNullOrWhiteSpace(proof.ProcessingJobId))
            {
                details["ProcessingJobId"] = proof.ProcessingJobId;
            }

            if (proof.ProvenAt.HasValue)
            {
                details["ImportProofAt"] = proof.ProvenAt.Value;
            }

            return details;
        }

        private static bool? ReadSourceRetained(string? data)
        {
            if (string.IsNullOrWhiteSpace(data)) return null;

            try
            {
                using var document = JsonDocument.Parse(data);
                if (!document.RootElement.TryGetProperty(
                        Download.SourceRetainedMetadataKey,
                        out var value))
                {
                    return null;
                }

                return value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
                    _ => null
                };
            }
            catch (JsonException)
            {
                return null;
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
