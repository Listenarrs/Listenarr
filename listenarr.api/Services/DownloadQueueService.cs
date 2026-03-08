using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Listenarr.Api.Repositories;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Listenarr.Api.Services
{
    public class DownloadQueueService : IDownloadQueueService
    {
        private readonly IMemoryCache _cache;
        private readonly IConfigurationService _configurationService;
        private readonly IDownloadRepository _downloadRepository;
        private readonly IDownloadProcessingJobRepository _downloadProcessingJobRepository;
        private readonly IDownloadClientGateway _clientGateway;
        private readonly IRemotePathMappingService _pathMappingService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IAppMetricsService _metrics;
        private readonly ILogger<DownloadQueueService> _logger;

        private const int QueueCacheExpirationSeconds = 10;
        private const int ClientStatusCacheExpirationSeconds = 30;

        public DownloadQueueService(
            IMemoryCache cache,
            IConfigurationService configurationService,
            IDownloadRepository downloadRepository,
            IDownloadProcessingJobRepository downloadProcessingJobRepository,
            IDownloadClientGateway clientGateway,
            IRemotePathMappingService pathMappingService,
            IHttpClientFactory httpClientFactory,
            IServiceScopeFactory scopeFactory,
            IAppMetricsService metrics,
            ILogger<DownloadQueueService> logger)
        {
            _cache = cache;
            _configurationService = configurationService;
            _downloadRepository = downloadRepository;
            _downloadProcessingJobRepository = downloadProcessingJobRepository;
            _clientGateway = clientGateway;
            _pathMappingService = pathMappingService;
            _httpClientFactory = httpClientFactory;
            _scopeFactory = scopeFactory;
            _metrics = metrics ?? new NoopAppMetricsService();
            _logger = logger;
        }

        public async Task<List<QueueItem>> GetQueueAsync()
        {
            var queueItems = new List<QueueItem>();

            var downloadClients = await _cache.GetOrCreateAsync("DownloadClients", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(QueueCacheExpirationSeconds);
                return await _configurationService.GetDownloadClientConfigurationsAsync();
            }) ?? new List<DownloadClientConfiguration>();

            var enabledClients = downloadClients.Where(c => c.IsEnabled).ToList();

            // Build listenarrDownloads list using repository
            List<Download> listenarrDownloads;
            // Track all known client-specific IDs (TorrentHash, ClientDownloadId) across ALL downloads
            // (including Moved/Failed) so that "unmatched completed" external items can be correctly
            // identified as tracked — even when the DB record points to a different client.
            var allKnownClientItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            {
                var allDownloads = await _downloadRepository.GetAllAsync();
                _logger.LogInformation("Found {TotalDownloads} downloads (including failed)", allDownloads.Count);

                var ddlDownloads = allDownloads.Where(d => d.DownloadClientId == "DDL").ToList();
                var ddlToShow = new List<Download>();

                if (ddlDownloads.Any())
                {
                    var ddlCompleted = ddlDownloads.Where(d => d.Status == DownloadStatus.Completed).ToList();
                    if (ddlCompleted.Any())
                    {
                        var completedIds = ddlCompleted.Select(d => d.Id).ToList();
                        var pendingJobs = await _downloadProcessingJobRepository.GetPendingDownloadIdsAsync(completedIds);
                        var allJobDownloads = await _downloadProcessingJobRepository.GetAllJobDownloadIdsAsync(completedIds);

                        var ddlCompletedToShow = ddlCompleted
                            .Where(d => pendingJobs.Contains(d.Id) || !allJobDownloads.Contains(d.Id))
                            .ToList();

                        ddlToShow.AddRange(ddlCompletedToShow);
                        _logger.LogInformation("DDL pending jobs count: {PendingJobs}, All job downloads count: {AllJobs}, DDL completed to show: {CompletedToShow}",
                            pendingJobs.Count, allJobDownloads.Count, ddlCompletedToShow.Count);
                    }

                    ddlToShow.AddRange(ddlDownloads.Where(d => d.Status != DownloadStatus.Completed && d.Status != DownloadStatus.Moved));
                }

                var externalDownloads = allDownloads
                    .Where(d => d.DownloadClientId != "DDL" &&
                                d.Status != DownloadStatus.Moved &&
                                d.Status != DownloadStatus.Failed &&
                                (d.Status != DownloadStatus.Completed || string.IsNullOrEmpty(d.FinalPath)))
                    .ToList();

                listenarrDownloads = ddlToShow.Concat(externalDownloads).ToList();
                _logger.LogDebug("Final filtering result: {FinalCount} downloads to include in queue filtering ({DdlCount} DDL, {ExternalCount} external)",
                    listenarrDownloads.Count, ddlToShow.Count, externalDownloads.Count);

                // Collect all known client item IDs from ALL downloads (including Moved/Failed)
                // so we can suppress "unmatched external" items that are actually tracked.
                foreach (var clientItemId in allDownloads.SelectMany(dl => GetKnownClientItemIds(dl.Metadata)))
                {
                    allKnownClientItemIds.Add(clientItemId);
                }
            }

            // Application settings cache
            ApplicationSettings? appSettings = await _cache.GetOrCreateAsync("ApplicationSettings", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(ClientStatusCacheExpirationSeconds);
                try
                {
                    return await _configurationService.GetApplicationSettingsAsync();
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogDebug(ex, "Failed to load application settings while building queue (non-fatal)");
                    return null;
                }
            });

            var includeCompletedExternal = appSettings != null && appSettings.ShowCompletedExternalDownloads;

            foreach (var client in enabledClients)
            {
                try
                {
                    var clientQueue = await _clientGateway.GetQueueAsync(client);

                    _logger.LogInformation("Before filtering - Client {ClientName} has {TotalItems} queue items", client.Name ?? client.Id, clientQueue.Count);
                    _logger.LogInformation("Database has {DatabaseItems} Listenarr downloads for filtering", listenarrDownloads.Count);

                    // Filter queue to Listenarr downloads
                    var initialFiltered = clientQueue.Where(queueItem =>
                        FindBestMatchingDownload(queueItem, client, listenarrDownloads) != null
                    ).ToList();

                    var mappedFiltered = new List<QueueItem>();
                    foreach (var queueItem in initialFiltered)
                    {
                        try
                        {
                            var matchedDownload = FindBestMatchingDownload(queueItem, client, listenarrDownloads);

                            if (matchedDownload != null)
                            {
                                queueItem.Id = matchedDownload.Id;
                            }

                            mappedFiltered.Add(queueItem);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                            _logger.LogDebug(ex, "Error mapping filtered queue item to Listenarr download");
                            mappedFiltered.Add(queueItem);
                        }
                    }

                    queueItems.AddRange(mappedFiltered);

                    if (includeCompletedExternal)
                    {
                        var existingIds = queueItems.Select(q => q.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

                        var unmatchedCompleted = clientQueue
                            .Where(q => (q.Status ?? string.Empty).Equals("completed", StringComparison.OrdinalIgnoreCase))
                            .Where(q => !existingIds.Contains(q.Id))
                            // Skip items whose client ID (hash) is already tracked by ANY download
                            // record, even one tied to a different client or in a terminal state.
                            // This prevents showing torrents as "unmatched" when they were grabbed
                            // by Listenarr but recorded under a different download client.
                            .Where(q => string.IsNullOrEmpty(q.Id) || !allKnownClientItemIds.Contains(q.Id))
                            .ToList();

                        foreach (var uc in unmatchedCompleted)
                        {
                            var clientName = client.Name ?? uc.DownloadClient ?? client.Id;
                            var clientType = client.Type?.ToLowerInvariant() ?? uc.DownloadClientType ?? "external";

                            if (existingIds.Contains(uc.Id)) continue;

                            queueItems.Add(new QueueItem
                            {
                                Id = uc.Id,
                                Title = uc.Title ?? "Unknown",
                                Quality = uc.Quality ?? "Unknown",
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

                    _logger.LogDebug("Client {ClientName}: {TotalItems} total items, {FilteredItems} Listenarr items", client.Name, clientQueue.Count, mappedFiltered.Count);

                    // Keep tracked downloads when queue snapshots temporarily
                    // miss them instead of deleting records via orphan heuristics.
                    try
                    {
                        var clientDownloads = listenarrDownloads.Where(d => d.DownloadClientId == client.Id).ToList();
                        var mappedDownloadIds = mappedFiltered.Select(q => q.Id).ToHashSet();
                        var orphanedDownloads = clientDownloads.Where(d => !mappedDownloadIds.Contains(d.Id)).ToList();

                        if (orphanedDownloads.Any())
                        {
                            _logger.LogInformation("Detected {Count} tracked downloads missing from {ClientName} queue snapshot; keeping records for resilient monitoring/import handling",
                                orphanedDownloads.Count, client.Name);
                            try { _metrics.Increment("download.purge.skipped.tracked_orphan_retained", orphanedDownloads.Count); } catch (Exception caughtEx_3) when (caughtEx_3 is not OperationCanceledException && caughtEx_3 is not OutOfMemoryException && caughtEx_3 is not StackOverflowException) {
                                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                            }
                        }
                    }
                    catch (Exception purgeEx) when (purgeEx is not OperationCanceledException && purgeEx is not OutOfMemoryException && purgeEx is not StackOverflowException) {
                        _logger.LogError(purgeEx, "Error purging orphaned downloads for client {ClientName}", client.Name);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogError(ex, "Error getting queue from download client {ClientName}", client.Name);
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

                    foreach (var d in completedExternal)
                    {
                        if (existingIds.Contains(d.Id)) continue;

                        var clientCfg = enabledClients.FirstOrDefault(c => c.Id == d.DownloadClientId);
                        var clientName = clientCfg?.Name ?? d.DownloadClientId ?? "External Client";
                        var clientType = clientCfg?.Type?.ToLowerInvariant() ?? "external";

                        queueItems.Add(new QueueItem
                        {
                            Id = d.Id,
                            Title = d.Title ?? "Unknown",
                            Quality = d.Metadata != null && d.Metadata.TryGetValue("Quality", out var q) ? (q?.ToString() ?? "Unknown") : "Unknown",
                            Status = "completed",
                            Progress = 100,
                            Size = d.TotalSize,
                            Downloaded = d.DownloadedSize,
                            DownloadSpeed = 0,
                            Eta = null,
                            DownloadClient = clientName,
                            DownloadClientId = d.DownloadClientId ?? string.Empty,
                            DownloadClientType = clientType,
                            AddedAt = d.StartedAt,
                            CanPause = false,
                            CanRemove = true,
                            RemotePath = d.DownloadPath,
                            LocalPath = d.FinalPath,
                            ContentPath = d.FinalPath ?? d.DownloadPath
                        });

                        existingIds.Add(d.Id);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogWarning(ex, "Error while appending completed external downloads to queue (non-fatal)");
                }
            }

            return queueItems.OrderByDescending(q => q.AddedAt).ToList();
        }

        private bool AreTitlesSimilar(string a, string b)
        {
            try
            {
                // Conservative normalization used originally in DownloadService.IsMatchingTitle
                var An = NormalizeTitle(a);
                var Bn = NormalizeTitle(b);
                return An.Contains(Bn) || Bn.Contains(An) || An == Bn;
            }
            catch (Exception caughtEx_4) when (caughtEx_4 is not OperationCanceledException && caughtEx_4 is not OutOfMemoryException && caughtEx_4 is not StackOverflowException) { return false; }
        }

        private string NormalizeTitle(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var lower = s.ToLowerInvariant();
            var cleaned = new string(lower.Where(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch)).ToArray());
            return string.Join(' ', cleaned.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private Download? FindBestMatchingDownload(QueueItem queueItem, DownloadClientConfiguration client, List<Download> listenarrDownloads)
        {
            if (queueItem == null || client == null || listenarrDownloads == null || listenarrDownloads.Count == 0)
            {
                return null;
            }

            var bestMatch = listenarrDownloads
                .Where(download => download.DownloadClientId == client.Id)
                .Select(download => new
                {
                    Download = download,
                    Score = GetMatchScore(download, queueItem)
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Download.StartedAt)
                .FirstOrDefault();

            return bestMatch?.Download;
        }

        private int GetMatchScore(Download download, QueueItem queueItem)
        {
            if (download == null || queueItem == null)
            {
                return 0;
            }

            if (!string.IsNullOrWhiteSpace(download.Id) &&
                !string.IsNullOrWhiteSpace(queueItem.Id) &&
                string.Equals(download.Id, queueItem.Id, StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }

            try
            {
                if (download.Metadata != null && !string.IsNullOrWhiteSpace(queueItem.Id))
                {
                    // Check ClientDownloadId (stored for all client types on add)
                    var storedId = GetMetadataString(download.Metadata, "ClientDownloadId");
                    if (!string.IsNullOrWhiteSpace(storedId) &&
                        string.Equals(storedId, queueItem.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        return 2;
                    }

                    // Legacy fallback: TorrentHash (older qBittorrent records)
                    var storedHash = GetMetadataString(download.Metadata, "TorrentHash");
                    if (!string.IsNullOrWhiteSpace(storedHash) &&
                        string.Equals(storedHash, queueItem.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        return 2;
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(
                    ex,
                    "Failed to inspect download metadata while scoring queue match for download {DownloadId} and queue item {QueueItemId}",
                    LogRedaction.SanitizeText(download.Id),
                    LogRedaction.SanitizeText(queueItem.Id));
            }

            if (!string.IsNullOrWhiteSpace(download.Title) &&
                !string.IsNullOrWhiteSpace(queueItem.Title) &&
                AreTitlesSimilar(download.Title, queueItem.Title))
            {
                return 1;
            }

            return 0;
        }

        private static IEnumerable<string> GetKnownClientItemIds(Dictionary<string, object>? metadata)
        {
            var clientDownloadId = GetMetadataString(metadata, "ClientDownloadId");
            if (!string.IsNullOrWhiteSpace(clientDownloadId))
            {
                yield return clientDownloadId;
            }

            var torrentHash = GetMetadataString(metadata, "TorrentHash");
            if (!string.IsNullOrWhiteSpace(torrentHash))
            {
                yield return torrentHash;
            }
        }

        private static string? GetMetadataString(Dictionary<string, object>? metadata, string key)
        {
            if (metadata == null || !metadata.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            if (value is JsonElement element)
            {
                return element.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.Undefined => null,
                    _ => element.ToString()
                };
            }

            return value.ToString();
        }
    }
}

