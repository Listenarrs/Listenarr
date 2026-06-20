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
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads.Queue
{
    /// <summary>
    /// Provides download queues from download clients
    /// Cache results for efficiency
    /// </summary>
    public class DownloadQueueService(
        IMemoryCache cache,
        IConfigurationService configurationService,
        IDownloadRepository downloadRepository,
        DownloadQueueCandidateLoader candidateLoader,
        DownloadClientQueuePoller clientQueuePoller,
        IAppMetricsService metrics,
        ILogger<DownloadQueueService> logger) : IDownloadQueueService
    {
        internal TimeSpan _clientQueueTimeout = TimeSpan.FromSeconds(15);
        internal TimeSpan _staleSnapshotMaxAge = TimeSpan.FromMinutes(3);
        private readonly int _maxParallelClientPolls = 4;

        private const int QueueCacheExpirationSeconds = 10;
        private const int ClientStatusCacheExpirationSeconds = 30;

        public async Task<QueueSnapshot> GetQueueSnapshotAsync()
        {
            var queueItems = new List<QueueItem>();

            var downloadClients = await cache.GetOrCreateAsync("DownloadClients", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(QueueCacheExpirationSeconds);
                return await configurationService.GetDownloadClientConfigurationsAsync();
            }) ?? [];

            var enabledClients = downloadClients.Where(c => c.IsEnabled).ToList();

            var candidateSet = await candidateLoader.LoadAsync();
            var listenarrDownloads = candidateSet.VisibleDownloads;
            var allDownloadsForMatching = candidateSet.MatchingDownloads;
            var allKnownClientItemIds = candidateSet.KnownClientItemIds;

            ApplicationSettings? appSettings = await cache.GetOrCreateAsync("ApplicationSettings", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(ClientStatusCacheExpirationSeconds);
                try
                {
                    return await configurationService.GetApplicationSettingsAsync();
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    logger.LogDebug(ex, "Failed to load application settings while building queue (non-fatal)");
                    return null;
                }
            });

            var includeCompletedExternal = appSettings != null && appSettings.ShowCompletedExternalDownloads;
            var clientQueueResults = await clientQueuePoller.FetchAsync(
                enabledClients,
                _clientQueueTimeout,
                _staleSnapshotMaxAge,
                _maxParallelClientPolls);
            var clientStatuses = DownloadQueueSnapshotMapper.BuildClientStatuses(clientQueueResults);

            foreach (var clientQueueResult in clientQueueResults)
            {
                var client = clientQueueResult.Client;
                var clientQueue = clientQueueResult.QueueItems;
                DownloadQueueSnapshotMapper.ApplySnapshotMetadata(clientQueue, clientQueueResult);

                try
                {
                    logger.LogInformation("Client {ClientName} has {TotalItems} queue items", client.Name ?? client.Id, clientQueue.Count);
                    logger.LogInformation("Database has {DatabaseItems} Listenarr downloads for metadata enrichment", listenarrDownloads.Count);

                    if (clientQueueResult.UsedCachedSnapshot)
                    {
                        logger.LogWarning(
                            "Using cached queue snapshot for client {ClientName} (age: {AgeSeconds:F1}s, reason: {Reason})",
                            client.Name ?? client.Id,
                            clientQueueResult.SnapshotAge?.TotalSeconds ?? 0,
                            clientQueueResult.FailureReason ?? "unknown");
                    }
                    else if (clientQueueResult.IsUnavailable)
                    {
                        logger.LogWarning(
                            "No live queue snapshot available for client {ClientName}; proceeding with an empty queue view for this client",
                            client.Name ?? client.Id);
                    }

                    var mappedQueueItems = new List<QueueItem>();
                    foreach (var queueItem in clientQueue)
                    {
                        try
                        {
                            if (queueItem.Status == "completed" && queueItem.CompletionTime == null)
                            {
                                queueItem.CompletionTime = DateTime.UtcNow;
                            }

                            var matchedDownload = DownloadQueueMetadataMatcher.FindBestMatchingDownload(queueItem, client, allDownloadsForMatching, logger);
                            if (matchedDownload != null)
                            {
                                var originalClientId = queueItem.Id;
                                await PersistDiscoveredClientIdentifiersAsync(matchedDownload, client, originalClientId, allKnownClientItemIds);

                                queueItem.Id = matchedDownload.Id;
                                if (!string.IsNullOrWhiteSpace(matchedDownload.Title))
                                {
                                    queueItem.Title = matchedDownload.Title;
                                }

                                if (string.IsNullOrWhiteSpace(queueItem.Author) && !string.IsNullOrWhiteSpace(matchedDownload.Artist))
                                {
                                    queueItem.Author = matchedDownload.Artist;
                                }

                                if (matchedDownload.AudiobookId.HasValue)
                                {
                                    queueItem.AudiobookId = matchedDownload.AudiobookId;
                                }

                                if (string.IsNullOrWhiteSpace(queueItem.Language) && !string.IsNullOrWhiteSpace(matchedDownload.Language))
                                {
                                    queueItem.Language = matchedDownload.Language;
                                }

                                logger.LogDebug(
                                    "Enriched queue item (original: {OriginalId}) with DB metadata from download {DownloadId}",
                                    originalClientId,
                                    matchedDownload.Id);
                            }
                            else
                            {
                                if (!string.IsNullOrEmpty(queueItem.Id) && allKnownClientItemIds.Contains(queueItem.Id))
                                {
                                    logger.LogDebug(
                                        "Queue item {QueueId} '{Title}' has known client ID from a tracked download and will be suppressed to avoid duplication",
                                        queueItem.Id,
                                        queueItem.Title);
                                    continue;
                                }

                                logger.LogDebug("Queue item {QueueId} '{Title}' not tracked in database - showing as untracked",
                                    queueItem.Id,
                                    queueItem.Title);
                            }

                            mappedQueueItems.Add(queueItem);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            logger.LogWarning(ex, "Error processing queue item {QueueId}, including anyway", queueItem.Id);
                            mappedQueueItems.Add(queueItem);
                        }
                    }

                    queueItems.AddRange(mappedQueueItems);

                    if (includeCompletedExternal)
                    {
                        var existingIds = queueItems.Select(q => q.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var unmatchedCompleted = clientQueue
                            .Where(q => (q.Status ?? string.Empty).Equals("completed", StringComparison.OrdinalIgnoreCase))
                            .Where(q => !existingIds.Contains(q.Id))
                            .Where(q => string.IsNullOrEmpty(q.Id) || !allKnownClientItemIds.Contains(q.Id))
                            .ToList();

                        foreach (var uc in unmatchedCompleted)
                        {
                            if (existingIds.Contains(uc.Id))
                            {
                                continue;
                            }

                            var clientName = client.Name ?? uc.DownloadClient ?? client.Id;
                            var clientType = client.Type?.ToLowerInvariant() ?? uc.DownloadClientType ?? "external";

                            queueItems.Add(new QueueItem
                            {
                                Id = uc.Id,
                                Title = uc.Title ?? "Unknown",
                                Quality = uc.Quality ?? "Unknown",
                                Language = uc.Language,
                                Status = "completed",
                                Progress = 100,
                                Size = uc.Size,
                                Downloaded = uc.Downloaded,
                                DownloadSpeed = 0,
                                Eta = null,
                                DownloadClient = clientName,
                                DownloadClientId = client.Id,
                                DownloadClientType = clientType,
                                AddedAt = uc.AddedAt,
                                IsStaleSnapshot = uc.IsStaleSnapshot,
                                SnapshotState = uc.SnapshotState,
                                SnapshotFailureReason = uc.SnapshotFailureReason,
                                SnapshotAgeSeconds = uc.SnapshotAgeSeconds,
                                SnapshotRefreshedAt = uc.SnapshotRefreshedAt,
                                CanPause = false,
                                CanRemove = true,
                                RemotePath = uc.RemotePath,
                                LocalPath = uc.LocalPath,
                                ContentPath = uc.ContentPath,
                                Seeders = uc.Seeders,
                                Leechers = uc.Leechers,
                                Ratio = uc.Ratio
                            });

                            existingIds.Add(uc.Id);
                        }
                    }

                    logger.LogDebug("Client {ClientName}: showing {TotalItems} queue items", client.Name, mappedQueueItems.Count);

                    try
                    {
                        var clientDownloads = listenarrDownloads.Where(d => d.DownloadClientId == client.Id).ToList();

                        if (clientQueueResult.UsedCachedSnapshot)
                        {
                            logger.LogInformation(
                                "Skipping orphan retention analysis for client {ClientName}: using cached queue snapshot",
                                client.Name);
                            continue;
                        }

                        if (clientQueueResult.IsUnavailable)
                        {
                            logger.LogWarning(
                                "Skipping orphan retention analysis for client {ClientName}: no live queue snapshot was available",
                                client.Name);
                            continue;
                        }

                        if (clientQueue.Count == 0 && clientDownloads.Any())
                        {
                            logger.LogWarning(
                                "Skipping orphan purge for client {ClientName}: client returned 0 queue items but {Count} downloads are tracked. Client may be temporarily unreachable.",
                                client.Name,
                                clientDownloads.Count);
                            continue;
                        }

                        var allClientItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var mapped in mappedQueueItems)
                        {
                            allClientItemIds.Add(mapped.Id);
                        }

                        foreach (var itemId in clientQueue.Where(item => !string.IsNullOrWhiteSpace(item.Id)).Select(item => item.Id!))
                        {
                            allClientItemIds.Add(itemId!);
                        }

                        var orphanedDownloads = clientDownloads.Where(d =>
                        {
                            if (allClientItemIds.Contains(d.Id))
                            {
                                return false;
                            }

                            if (DownloadQueueMetadataMatcher.GetKnownClientItemIds(d.Metadata).Any(allClientItemIds.Contains))
                            {
                                return false;
                            }

                            if (d.Status == DownloadStatus.Completed ||
                                d.Status == DownloadStatus.Moved ||
                                d.Status == DownloadStatus.Failed)
                            {
                                return false;
                            }

                            if (d.Status == DownloadStatus.Downloading ||
                                d.Status == DownloadStatus.Processing)
                            {
                                return false;
                            }

                            if ((DateTime.UtcNow - d.StartedAt).TotalMinutes < 5)
                            {
                                logger.LogDebug(
                                    "Skipping purge for recent download {DownloadId} '{Title}' (age: {Age:F1} min)",
                                    d.Id,
                                    d.Title,
                                    (DateTime.UtcNow - d.StartedAt).TotalMinutes);
                                return false;
                            }

                            return true;
                        }).ToList();

                        if (orphanedDownloads.Any())
                        {
                            logger.LogInformation(
                                "Detected {Count} tracked downloads missing from current {ClientName} queue snapshot; keeping records for resilient monitoring/import handling",
                                orphanedDownloads.Count,
                                client.Name);

                            try
                            {
                                metrics.Increment("download.purge.skipped.tracked_orphan_retained", orphanedDownloads.Count);
                            }
                            catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
                            {
                                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                            }
                        }
                    }
                    catch (Exception purgeEx) when (purgeEx is not OperationCanceledException && purgeEx is not OutOfMemoryException && purgeEx is not StackOverflowException)
                    {
                        logger.LogError(purgeEx, "Error retaining orphaned downloads for client {ClientName}", client.Name);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    logger.LogError(ex, "Error getting queue from download client {ClientName}", client.Name);
                }
            }

            if (includeCompletedExternal)
            {
                try
                {
                    var existingIds = queueItems.Select(q => q.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

                    var completedExternal = listenarrDownloads
                        .Where(d => d.DownloadClientId != "DDL" && d.Status == DownloadStatus.Completed)
                        .ToList();

                    foreach (var download in completedExternal)
                    {
                        if (existingIds.Contains(download.Id))
                        {
                            continue;
                        }

                        var clientCfg = enabledClients.FirstOrDefault(c => c.Id == download.DownloadClientId);
                        var clientName = clientCfg?.Name ?? download.DownloadClientId ?? "External Client";
                        var clientType = clientCfg?.Type?.ToLowerInvariant() ?? "external";

                        queueItems.Add(new QueueItem
                        {
                            Id = download.Id,
                            Title = download.Title ?? "Unknown",
                            Quality = download.Metadata != null && download.Metadata.TryGetValue("Quality", out var qualityObj)
                                ? (qualityObj?.ToString() ?? "Unknown")
                                : "Unknown",
                            Language = download.Metadata != null && download.Metadata.TryGetValue("Language", out var langObj)
                                ? langObj?.ToString()
                                : null,
                            Status = "completed",
                            Progress = 100,
                            Size = download.TotalSize,
                            Downloaded = download.DownloadedSize,
                            DownloadSpeed = 0,
                            Eta = null,
                            DownloadClient = clientName,
                            DownloadClientId = download.DownloadClientId ?? string.Empty,
                            DownloadClientType = clientType,
                            AddedAt = download.StartedAt,
                            CanPause = false,
                            CanRemove = true,
                            AudiobookId = download.AudiobookId,
                            RemotePath = download.DownloadPath,
                            LocalPath = download.FinalPath,
                            ContentPath = download.FinalPath ?? download.DownloadPath
                        });

                        existingIds.Add(download.Id);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    logger.LogWarning(ex, "Error while appending completed external downloads to queue (non-fatal)");
                }
            }

            var orderedItems = queueItems.OrderByDescending(q => q.AddedAt).ToList();

            return new QueueSnapshot
            {
                Items = orderedItems,
                Clients = clientStatuses,
                GeneratedAt = DateTime.UtcNow,
                HasStaleData = clientStatuses.Any(c => c.IsStaleSnapshot),
                HasUnavailableClients = clientStatuses.Any(c => c.IsUnavailable)
            };
        }

        public async Task<List<QueueItem>> GetQueueAsync()
        {
            var snapshot = await GetQueueSnapshotAsync();
            return snapshot.Items;
        }

        private async Task PersistDiscoveredClientIdentifiersAsync(
            Download matchedDownload,
            DownloadClientConfiguration client,
            string? originalClientId,
            HashSet<string> allKnownClientItemIds)
        {
            if (matchedDownload == null ||
                string.IsNullOrWhiteSpace(originalClientId) ||
                string.Equals(originalClientId, matchedDownload.Id, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            matchedDownload.Metadata ??= new Dictionary<string, object>();

            var existingClientDownloadId = DownloadQueueMetadataMatcher.GetMetadataString(matchedDownload.Metadata, "ClientDownloadId");
            if (!string.Equals(existingClientDownloadId, originalClientId, StringComparison.OrdinalIgnoreCase))
            {
                matchedDownload.Metadata["ClientDownloadId"] = originalClientId;
                await downloadRepository.UpdateMetadataAsync(matchedDownload.Id, "ClientDownloadId", originalClientId);
            }

            allKnownClientItemIds.Add(originalClientId);

            if (string.Equals(client.Type, "qbittorrent", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(client.Type, "transmission", StringComparison.OrdinalIgnoreCase))
            {
                var existingTorrentHash = DownloadQueueMetadataMatcher.GetMetadataString(matchedDownload.Metadata, "TorrentHash");
                if (!string.Equals(existingTorrentHash, originalClientId, StringComparison.OrdinalIgnoreCase))
                {
                    matchedDownload.Metadata["TorrentHash"] = originalClientId;
                    await downloadRepository.UpdateMetadataAsync(matchedDownload.Id, "TorrentHash", originalClientId);
                }
            }
        }

    }
}
