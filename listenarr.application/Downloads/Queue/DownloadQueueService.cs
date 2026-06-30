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

            // Direct downloads are internal work items, not rows reported by an
            // external client queue. Add them from the DB before polling clients
            // so DDL reservations, progress, completion, and import-pending work
            // are visible in Activity.
            queueItems.AddRange(listenarrDownloads
                .Where(download => string.Equals(download.DownloadClientId, DirectDownloadMetadataKeys.ClientId, StringComparison.OrdinalIgnoreCase))
                .Select(ToDirectDownloadQueueItem));

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
                        var ownershipEstablished = false;
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
                                ownershipEstablished = true;

                                // Establish user-visible Listenarr ownership before optional
                                // metadata persistence. If rebinding persistence fails, the
                                // catch below can still keep the matched Listenarr item visible
                                // instead of leaking the raw external client row or hiding work
                                // that we already proved belongs to Listenarr.
                                queueItem.Id = matchedDownload.Id;

                                await PersistDiscoveredClientIdentifiersAsync(matchedDownload, client, originalClientId, allKnownClientItemIds);

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

                                // Activity is Listenarr's operational queue, not a mirror of every
                                // transfer in a shared external client. Full snapshots are still
                                // used for reconciliation above. Unmatched active items stay hidden
                                // here; unmatched completed items are handled only by the explicit
                                // completed-external display block below.
                                if (includeCompletedExternal && IsUnmatchedCompletedExternalDisplayCandidate(queueItem))
                                {
                                    logger.LogDebug(
                                        "Queue item {QueueId} '{Title}' from client {ClientName} is completed, not tracked by Listenarr, and will be handled by completed external display",
                                        queueItem.Id,
                                        queueItem.Title,
                                        client.Name ?? client.Id);
                                }
                                else
                                {
                                    logger.LogDebug(
                                        "Queue item {QueueId} '{Title}' from client {ClientName} is not tracked by Listenarr and will be hidden from Activity",
                                        queueItem.Id,
                                        queueItem.Title,
                                        client.Name ?? client.Id);
                                }

                                continue;
                            }

                            mappedQueueItems.Add(queueItem);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            if (ownershipEstablished)
                            {
                                logger.LogWarning(
                                    ex,
                                    "Error enriching matched queue item {QueueId}; including matched Listenarr-owned item",
                                    queueItem.Id);
                                mappedQueueItems.Add(queueItem);
                                continue;
                            }

                            logger.LogWarning(
                                ex,
                                "Error processing unmatched queue item {QueueId}; hiding it from Activity",
                                queueItem.Id);
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
                                CompletionTime = uc.CompletionTime,
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
                        .Where(d => !string.Equals(d.DownloadClientId, DirectDownloadMetadataKeys.ClientId, StringComparison.OrdinalIgnoreCase) && d.Status == DownloadStatus.Completed)
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

        private static bool IsUnmatchedCompletedExternalDisplayCandidate(QueueItem queueItem)
        {
            return string.Equals(queueItem.Status, "completed", StringComparison.OrdinalIgnoreCase);
        }

        private static QueueItem ToDirectDownloadQueueItem(Download download)
        {
            var quality = download.Metadata != null && download.Metadata.TryGetValue("Quality", out var qualityObj)
                ? qualityObj?.ToString() ?? "Unknown"
                : "Unknown";

            return new QueueItem
            {
                Id = download.Id,
                Title = download.Title ?? "Unknown",
                Author = download.Artist,
                Quality = quality,
                Language = download.Language,
                Status = ToQueueStatus(download.Status),
                Progress = (double)download.Progress,
                Size = download.TotalSize,
                Downloaded = download.DownloadedSize,
                DownloadSpeed = 0,
                Eta = null,
                DownloadClient = "Direct Download",
                DownloadClientId = DirectDownloadMetadataKeys.ClientId,
                DownloadClientType = "ddl",
                AddedAt = download.StartedAt,
                CanPause = false,
                CanRemove = true,
                AudiobookId = download.AudiobookId,
                RemotePath = download.OriginalUrl,
                LocalPath = download.DownloadPath,
                ContentPath = string.IsNullOrWhiteSpace(download.FinalPath)
                    ? download.DownloadPath
                    : download.FinalPath,
                ErrorMessage = download.ErrorMessage
            };
        }

        private static string ToQueueStatus(DownloadStatus status) => status switch
        {
            DownloadStatus.Queued => "queued",
            DownloadStatus.Downloading => "downloading",
            DownloadStatus.Paused => "paused",
            DownloadStatus.Completed => "completed",
            DownloadStatus.Processing => "processing",
            DownloadStatus.ImportPending => "importpending",
            DownloadStatus.ImportBlocked => "importblocked",
            DownloadStatus.Failed => "failed",
            DownloadStatus.Moved => "moved",
            _ => status.ToString().ToLowerInvariant()
        };

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
