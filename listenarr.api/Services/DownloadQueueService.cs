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
                    .Where(d => d.DownloadClientId != "DDL" && d.Status != DownloadStatus.Completed && d.Status != DownloadStatus.Moved)
                    .ToList();

                listenarrDownloads = ddlToShow.Concat(externalDownloads).ToList();
                _logger.LogDebug("Final filtering result: {FinalCount} downloads to include in queue filtering ({DdlCount} DDL, {ExternalCount} external)",
                    listenarrDownloads.Count, ddlToShow.Count, externalDownloads.Count);
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
                        listenarrDownloads.Any(download =>
                        {
                            var idMatch = download.Id == queueItem.Id;
                            var hashMatch = false;
                            if (string.Equals(client.Type, "qbittorrent", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    if (download.Metadata != null && download.Metadata.TryGetValue("TorrentHash", out var hashObj))
                                    {
                                        var storedHash = hashObj?.ToString();
                                        if (!string.IsNullOrEmpty(storedHash))
                                        {
                                            hashMatch = storedHash.Equals(queueItem.Id, StringComparison.OrdinalIgnoreCase);
                                        }
                                    }
                                }
                                catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) { hashMatch = false; }
                            }

                            var titleMatch = false;
                            try
                            {
                                if (!string.IsNullOrEmpty(download.Title) && !string.IsNullOrEmpty(queueItem.Title))
                                {
                                    titleMatch = AreTitlesSimilar(download.Title, queueItem.Title);
                                }
                            }
                            catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException) { titleMatch = false; }

                            var clientMatch = download.DownloadClientId == client.Id;
                            var overallMatch = clientMatch && (idMatch || hashMatch || titleMatch);
                            return overallMatch;
                        })
                    ).ToList();

                    var mappedFiltered = new List<QueueItem>();
                    foreach (var queueItem in initialFiltered)
                    {
                        try
                        {
                            var matchedDownload = listenarrDownloads.FirstOrDefault(download =>
                                download.DownloadClientId == client.Id && (
                                    download.Id == queueItem.Id ||
                                    (download.Metadata != null && download.Metadata.TryGetValue("TorrentHash", out var h) && (h?.ToString() ?? string.Empty).Equals(queueItem.Id, StringComparison.OrdinalIgnoreCase)) ||
                                    (!string.IsNullOrEmpty(download.Title) && !string.IsNullOrEmpty(queueItem.Title) && AreTitlesSimilar(download.Title, queueItem.Title))
                                )
                            );

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

                    // Purge orphaned downloads
                    try
                    {
                        var clientDownloads = listenarrDownloads.Where(d => d.DownloadClientId == client.Id).ToList();
                        var mappedDownloadIds = mappedFiltered.Select(q => q.Id).ToHashSet();
                        var orphanedDownloads = clientDownloads.Where(d => !mappedDownloadIds.Contains(d.Id)).ToList();

                        if (orphanedDownloads.Any())
                        {
                            // Don't purge NZBGet downloads - they move to history immediately and need CompletedDownloadProcessor
                            var toPurge = orphanedDownloads.Where(d => client.Type?.ToLowerInvariant() != "nzbget").ToList();
                            
                            if (toPurge.Count < orphanedDownloads.Count)
                            {
                                _logger.LogDebug("Skipped purge of {SkippedCount} NZBGet downloads (may be in history)", 
                                    orphanedDownloads.Count - toPurge.Count);
                            }

                            foreach (var orphanedDownload in toPurge)
                            {
                                try
                                {
                                    await _downloadRepository.RemoveAsync(orphanedDownload.Id);
                                    _logger.LogInformation("Purged orphaned download record: {DownloadId} '{Title}' (no longer exists in {ClientName} queue)",
                                        orphanedDownload.Id, orphanedDownload.Title, client.Name);
                                    try { _metrics.Increment("download.purged.count"); } catch (Exception caughtEx_3) when (caughtEx_3 is not OperationCanceledException && caughtEx_3 is not OutOfMemoryException && caughtEx_3 is not StackOverflowException) { 
                                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                    }
                                }
                                catch (Exception exRemove) when (exRemove is not OperationCanceledException && exRemove is not OutOfMemoryException && exRemove is not StackOverflowException) {
                                    _logger.LogWarning(exRemove, "Failed to purge orphaned download {DownloadId} from repository, continuing", orphanedDownload.Id);
                                }
                            }

                            _logger.LogInformation("Attempted purge of {Count} orphaned download records from {ClientName}", toPurge.Count, client.Name);
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
    }
}

