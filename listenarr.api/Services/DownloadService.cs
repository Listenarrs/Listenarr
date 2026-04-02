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

using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Listenarr.Application.Services;
using Microsoft.AspNetCore.SignalR;
using Listenarr.Api.Hubs;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Listenarr.Api.Services.Adapters;
using Listenarr.Domain.Models.Converters;

namespace Listenarr.Api.Services
{
    public class DownloadService : IDownloadService, IDownloadOrchestrator
    {
        // Cache expiration constants
        private const int QueueCacheExpirationSeconds = 10;
        private const int ClientStatusCacheExpirationSeconds = 30;
        private const int DirectDownloadTimeoutHours = 2;

        private readonly IHubContext<DownloadHub> _hubContext;
        private readonly Listenarr.Application.Services.IHubBroadcaster _hubBroadcaster;
        private readonly IAudiobookRepository _audiobookRepository;
        private readonly IConfigurationService _configurationService;
        private readonly IDbContextFactory<ListenArrDbContext> _dbContextFactory;
        private readonly ILogger<DownloadService> _logger;
        private readonly HttpClient _httpClient;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly IRemotePathMappingService _pathMappingService;
        private readonly IImportService _importService;
        private readonly ISearchService _searchService;
        private readonly IDownloadClientGateway? _clientGateway;
        private readonly NotificationService _notificationService;
        private readonly IMemoryCache _cache;
        private readonly IAppMetricsService _metrics;
        private readonly IDownloadQueueService _downloadQueueService;
        private readonly ICompletedDownloadProcessor _completedDownloadProcessor;
        private readonly IDownloadHistoryService? _downloadHistoryService;
        private readonly IDownloadClientAdapterFactory _downloadClientAdapter;

        // Track qBittorrent sync state for incremental updates (clientId -> last rid)
        private readonly Dictionary<string, int> _qbittorrentSyncState = new();

        // Track qBittorrent torrent cache for merging incremental updates (clientId -> (torrentHash -> QueueItem))
        private readonly Dictionary<string, Dictionary<string, QueueItem>> _qbittorrentTorrentCache = new();

        // Explicit constructor with injected dependencies (avoids IServiceProvider resolves)
        public DownloadService(
            IHubContext<DownloadHub> hubContext,
            IAudiobookRepository audiobookRepository,
            IConfigurationService configurationService,
            IDbContextFactory<ListenArrDbContext> dbContextFactory,
            ILogger<DownloadService> logger,
            IHttpClientFactory httpClientFactory,
            IServiceScopeFactory serviceScopeFactory,
            IRemotePathMappingService pathMappingService,
            IImportService importService,
            ISearchService searchService,
            IDownloadClientGateway? clientGateway,
            IMemoryCache cache,
            IDownloadQueueService downloadQueueService,
            ICompletedDownloadProcessor completedDownloadProcessor,
            IAppMetricsService metrics,
            NotificationService notificationService,
            IDownloadClientAdapterFactory downloadClientAdapter,
            Listenarr.Application.Services.IHubBroadcaster? hubBroadcaster = null,
            IDownloadHistoryService? downloadHistoryService = null)
        {
            _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
            _hubBroadcaster = hubBroadcaster ?? new NoopHubBroadcaster();
            _audiobookRepository = audiobookRepository ?? throw new ArgumentNullException(nameof(audiobookRepository));
            _configurationService = configurationService ?? throw new ArgumentNullException(nameof(configurationService));
            _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            // Create a default HttpClient from factory for general use
            _httpClient = _httpClientFactory.CreateClient();
            _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
            _pathMappingService = pathMappingService ?? throw new ArgumentNullException(nameof(pathMappingService));
            _importService = importService ?? throw new ArgumentNullException(nameof(importService));
            _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
            _clientGateway = clientGateway;
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _downloadQueueService = downloadQueueService ?? throw new ArgumentNullException(nameof(downloadQueueService));
            _completedDownloadProcessor = completedDownloadProcessor ?? throw new ArgumentNullException(nameof(completedDownloadProcessor));
            _downloadHistoryService = downloadHistoryService;
            _downloadClientAdapter = downloadClientAdapter;
        }

        /// <summary>
        /// Normalize mam_id by decoding any existing encoding and then encoding exactly once
        /// </summary>
        private static string NormalizeMamId(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            var decoded = raw;
            while (true)
            {
                var next = Uri.UnescapeDataString(decoded);
                if (next == decoded) break;
                decoded = next;
            }
            return Uri.EscapeDataString(decoded);
        }

        public async Task<string?> StartDownloadAsync(SearchResult searchResult, string downloadClientId, int? audiobookId = null)
        {
            return await SendToDownloadClientAsync(searchResult, downloadClientId, audiobookId);
        }

        /// <summary>
        /// Retrieve cached torrent bytes and filename for a given download id if available
        /// </summary>
        public Task<(byte[]? Bytes, string? FileName)> GetCachedTorrentAsync(string downloadId)
        {
            var cacheKey = $"mam:cachedtorrent:{downloadId}";
            var bytes = _cache.Get<byte[]>(cacheKey + ":bytes");
            var name = _cache.Get<string>(cacheKey + ":name");
            return Task.FromResult((bytes, name));
        }

        /// <summary>
        /// Retrieve cached announce URLs for a given download id if available
        /// </summary>
        public Task<System.Collections.Generic.List<string>?> GetCachedAnnouncesAsync(string downloadId)
        {
            try
            {
                if (string.IsNullOrEmpty(downloadId)) return Task.FromResult<System.Collections.Generic.List<string>?>(null);
                var cacheKey = $"mam:cachedtorrent:{downloadId}:announces";
                var announces = _cache.Get<System.Collections.Generic.List<string>>(cacheKey);
                if (announces != null && announces.Count > 0)
                {
                    return Task.FromResult<System.Collections.Generic.List<string>?>(announces);
                }

                // Fallback: if announces not cached, try to extract from cached bytes
                var bytes = _cache.Get<byte[]>($"mam:cachedtorrent:{downloadId}:bytes");
                if (bytes != null)
                {
                    var extracted = MyAnonamouseHelper.ExtractAnnounceUrls(bytes);
                    if (extracted != null && extracted.Count > 0)
                    {
                        // cache for future retrievals
                        _cache.Set($"mam:cachedtorrent:{downloadId}:announces", extracted, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(30) });
                        return Task.FromResult<System.Collections.Generic.List<string>?>(extracted);
                    }
                }

                return Task.FromResult<System.Collections.Generic.List<string>?>(null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogDebug(ex, "Failed to retrieve cached announces for download {DownloadId} (non-fatal)", downloadId);
                return Task.FromResult<System.Collections.Generic.List<string>?>(null);
            }
        }

        public async Task<List<Download>> GetActiveDownloadsAsync()
        {
            // NOTE: Not implemented - download tracking happens via external clients (qBittorrent, Transmission, etc.)
            // The queue is fetched directly from download clients, so this method is intentionally minimal.
            // See GetQueueAsync for actual download retrieval from external clients.
            return await Task.FromResult(new List<Download>());
        }

        public async Task<Download?> GetDownloadAsync(string downloadId)
        {
            // NOTE: Not implemented - downloads are managed by external clients
            // Use database queries or GetQueueAsync() to retrieve download information
            return await Task.FromResult<Download?>(null);
        }

        public async Task<bool> CancelDownloadAsync(string downloadId)
        {
            // NOTE: Not implemented - cancellation must be done through download client APIs
            // Each client (qBittorrent, Transmission, etc.) has its own cancellation mechanism
            return await Task.FromResult(false);
        }

        public async Task UpdateDownloadStatusAsync()
        {
            // NOTE: Not implemented - status updates are handled via SignalR broadcasts
            // The DownloadMonitorService continuously polls clients and broadcasts updates
            // No manual update trigger is needed in the current architecture
            await Task.CompletedTask;
        }

        // Minimal but safe implementations for newly-added IDownloadService members.
        // These are intentionally conservative placeholders so the service satisfies the
        // interface while the full reprocessing/import workflow is implemented elsewhere.
        public async Task ProcessCompletedDownloadAsync(string downloadId, string finalPath)
        {
            _logger.LogInformation("ProcessCompletedDownloadAsync called for {DownloadId} (finalPath: {FinalPath})", downloadId, finalPath);

            try
            {
                // Prefer factory-created DbContext for persistence so background workers don't rely on scoped ambient contexts.
                // We also attempt to update any scoped ListenArrDbContext instances (used in tests) so in-memory tracked
                // entities reflect the persisted changes.
                var dbContext = await _dbContextFactory.CreateDbContextAsync();
                var download = await dbContext.Downloads.FindAsync(downloadId);
                if (download == null)
                {
                    _logger.LogWarning("ProcessCompletedDownloadAsync: download record not found: {DownloadId}", downloadId);
                }
                else
                {
                    // Update status to Completed now; FinalPath will be updated after import completes.
                    download.Status = DownloadStatus.Completed;
                    dbContext.Downloads.Update(download);
                    await dbContext.SaveChangesAsync();
                    _logger.LogInformation("Marked download {DownloadId} as Completed (pre-import)", downloadId);

                    // Sync status into any scoped ListenArrDbContext registered in DI so tests that are holding
                    // a tracked DbContext instance observe the state change.
                    try
                    {
                        var scopeFactoryToUse = (_importService as ImportService)?.ScopeFactory ?? _serviceScopeFactory;
                        using var scopeSync = scopeFactoryToUse.CreateScope();
                        var scopedDb = scopeSync.ServiceProvider.GetService<ListenArrDbContext>();
                        if (scopedDb != null)
                        {
                            var local = await scopedDb.Downloads.FindAsync(downloadId);
                            if (local != null)
                            {
                                local.Status = DownloadStatus.Completed;
                                _logger.LogDebug("Synchronized Completed status into scoped ListenArrDbContext for {DownloadId}", downloadId);
                            }
                        }
                    }
                    catch (Exception syncEx) when (syncEx is not OperationCanceledException && syncEx is not OutOfMemoryException && syncEx is not StackOverflowException) {
                        _logger.LogDebug(syncEx, "Failed to synchronize status into scoped ListenArrDbContext (non-fatal)");
                    }
                }

                // CompletedDownloadProcessor handles the entire import workflow
                // Don't do any import logic here - just delegate to the processor
                try
                {
                    _logger.LogInformation("Calling CompletedDownloadProcessor for download {DownloadId}", downloadId);
                    await _completedDownloadProcessor.ProcessCompletedDownloadAsync(downloadId, finalPath);
                    _logger.LogInformation("CompletedDownloadProcessor finished for download {DownloadId}", downloadId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogError(ex, "Failed to process completed download removal for {DownloadId}", downloadId);
                }

                try
                {
                    var currentQueueSnapshot = await _downloadQueueService.GetQueueSnapshotAsync();
                    if (_hubBroadcaster != null)
                    {
                        await _hubBroadcaster.BroadcastQueueUpdateAsync(currentQueueSnapshot);
                        _logger.LogInformation("Broadcasted QueueUpdate via IHubBroadcaster after processing download {DownloadId}", downloadId);
                    }
                    else
                    {
                        // Fallback to direct hub context for older registrations
                        var clientProxy = _hubContext.Clients.All;
                        await clientProxy.SendAsync("QueueUpdate", currentQueueSnapshot);
                        try
                        {
                            await clientProxy.SendCoreAsync("QueueUpdate", new object[] { currentQueueSnapshot }, System.Threading.CancellationToken.None);
                        }
                        catch (Exception exInner) when (exInner is not OperationCanceledException && exInner is not OutOfMemoryException && exInner is not StackOverflowException) {
                            _logger.LogDebug(exInner, "Direct SendCoreAsync for QueueUpdate failed (non-fatal)");
                        }

                        _logger.LogInformation("Broadcasted QueueUpdate after processing download {DownloadId}", downloadId);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogWarning(ex, "Failed to broadcast QueueUpdate after processing download {DownloadId}", downloadId);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Unexpected error in ProcessCompletedDownloadAsync for {DownloadId}", downloadId);
            }
        }

        public async Task<(bool Success, string Message, DownloadClientConfiguration? Client)> TestDownloadClientAsync(DownloadClientConfiguration client)
        {
            if (client == null)
            {
                return (false, "Download client configuration not provided", null);
            }

            if (_clientGateway == null)
            {
                _logger.LogWarning("TestDownloadClientAsync invoked but no download client gateway is registered");
                return (false, "Download client gateway unavailable", client);
            }

            try
            {
                var (success, message) = await _clientGateway.TestConnectionAsync(client);
                return (success, message, client);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error during TestDownloadClientAsync for client {ClientId}", LogRedaction.SanitizeText(client.Id ?? client.Name ?? client.Type));
                return (false, ex.Message, client);
            }
        }

        public async Task<string?> ReprocessDownloadAsync(string downloadId)
        {
            _logger.LogInformation("ReprocessDownloadAsync called for {DownloadId}", LogRedaction.SanitizeText(downloadId));

            // Placeholder: return null to indicate no job was created.
            // Concrete implementation should enqueue a reprocess job and return its ID.
            return await Task.FromResult<string?>(null);
        }

        public async Task<List<ReprocessResult>> ReprocessDownloadsAsync(List<string> downloadIds)
        {
            _logger.LogInformation("ReprocessDownloadsAsync called for {Count} downloads", downloadIds?.Count ?? 0);

            // Placeholder implementation: return empty results list.
            // A full implementation should iterate downloadIds and invoke reprocessing,
            // collecting per-download results.
            return await Task.FromResult(new List<ReprocessResult>());
        }

        public async Task<List<ReprocessResult>> ReprocessAllCompletedDownloadsAsync(bool includeProcessed = false, TimeSpan? maxAge = null)
        {
            _logger.LogInformation("ReprocessAllCompletedDownloadsAsync called includeProcessed={IncludeProcessed}, maxAge={MaxAge}", includeProcessed, maxAge);

            // Placeholder implementation: no-op and return empty list.
            // Full implementation should query completed downloads, apply filters and enqueue reprocess jobs.
            return await Task.FromResult(new List<ReprocessResult>());
        }

        public async Task<SearchAndDownloadResult> SearchAndDownloadAsync(int audiobookId)
        {
            // Get the audiobook
            var audiobook = await _audiobookRepository.GetByIdAsync(audiobookId);
            if (audiobook == null)
            {
                return new SearchAndDownloadResult
                {
                    Success = false,
                    Message = "Audiobook not found"
                };
            }

            if (audiobook.QualityProfile == null)
            {
                _logger.LogWarning("Audiobook '{Title}' has no quality profile assigned", audiobook.Title);
                return new SearchAndDownloadResult
                {
                    Success = false,
                    Message = "Audiobook has no quality profile assigned"
                };
            }

            // Build search query from audiobook metadata
            var searchQuery = BuildSearchQuery(audiobook);
            _logger.LogInformation("Searching for audiobook '{Title}' with query: {Query}", LogRedaction.SanitizeText(audiobook.Title), LogRedaction.SanitizeText(searchQuery));

            // Search using the working search service. This is an automatic search (triggered
            // by the background/manual 'search-and-download' endpoint), so set isAutomaticSearch
            // to true to ensure only indexers are queried (no Amazon/Audible scraping).
            var searchResults = await _searchService.SearchAsync(searchQuery, isAutomaticSearch: true);

            if (searchResults == null || !searchResults.Any())
            {
                return new SearchAndDownloadResult
                {
                    Success = false,
                    Message = "No search results found"
                };
            }

            // Score results against quality profile
            using var scope = _serviceScopeFactory.CreateScope();
            var qualityProfileService = scope.ServiceProvider.GetRequiredService<IQualityProfileService>();
            var scoredResults = await qualityProfileService.ScoreSearchResults(searchResults, audiobook.QualityProfile);

            // Log all scored results for debugging
            _logger.LogInformation("Scored {Count} search results for audiobook '{Title}':", scoredResults.Count, LogRedaction.SanitizeText(audiobook.Title));
            foreach (var scoredResult in scoredResults.OrderByDescending(s => s.TotalScore))
            {
                var status = scoredResult.IsRejected ? "REJECTED" : (scoredResult.TotalScore > 0 ? "ACCEPTABLE" : "LOW SCORE");
                _logger.LogInformation("  [{Status}] Score: {Score} | Title: {Title} | Source: {Source} | Size: {Size}MB | Seeders: {Seeders} | Quality: {Quality}",
                    status, scoredResult.TotalScore, LogRedaction.SanitizeText(scoredResult.SearchResult.Title), LogRedaction.SanitizeText(scoredResult.SearchResult.Source),
                    scoredResult.SearchResult.Size / 1024 / 1024, scoredResult.SearchResult.Seeders, scoredResult.SearchResult.Quality);
                if (scoredResult.IsRejected && scoredResult.RejectionReasons.Any())
                {
                    _logger.LogInformation("    Rejection reasons: {Reasons}", string.Join(", ", scoredResult.RejectionReasons));
                }
            }

            // Only consider non-rejected, score > 0 results
            var topResult = scoredResults
                .Where(s => !s.IsRejected && s.TotalScore > 0)
                .OrderByDescending(s => s.TotalScore)
                .FirstOrDefault();

            if (topResult == null)
            {
                _logger.LogWarning("No acceptable search results found for audiobook '{Title}' after quality filtering", audiobook.Title);
                return new SearchAndDownloadResult
                {
                    Success = false,
                    Message = "No acceptable search results found"
                };
            }

            // Assign score to SearchResult
            topResult.SearchResult.Score = topResult.TotalScore;

            var protocol = await ResolveDownloadProtocolAsync(topResult.SearchResult);

            // Handle trusted direct-download results directly
            if (DownloadProtocol.DirectDownload == protocol)
            {
                _logger.LogInformation("Top result is DDL, processing directly for: {Title}", topResult.SearchResult.Title);
                var downloadId = await DownloadDirectlyAsync(topResult.SearchResult, audiobookId);
                await LogDownloadHistory(audiobook, "Search", topResult.SearchResult);
                return new SearchAndDownloadResult
                {
                    Success = true,
                    Message = $"Successfully processed DDL download",
                    DownloadId = downloadId,
                    IndexerUsed = "Search",
                    DownloadClientUsed = "DDL",
                    SearchResult = topResult.SearchResult
                };
            }

            // Use topResult.SearchResult for torrent/nzb download
            var isTorrent = DownloadProtocol.Torrent == protocol;
            var downloadClient = await GetAppropriateDownloadClient((DownloadProtocol)protocol);

            if (downloadClient == null)
            {
                _logger.LogWarning("No suitable download client found for protocol: {Protocol}", protocol);
                return new SearchAndDownloadResult
                {
                    Success = false,
                    Message = $"No suitable download client found for {(isTorrent ? "torrent" : "NZB")} results"
                };
            }

            // Send to download client with audiobookId for proper metadata linking
            var downloadId2 = await SendToDownloadClientAsync(topResult.SearchResult, downloadClient.Id, audiobookId);
            if (downloadId2 == null)
            {
                return new SearchAndDownloadResult
                {
                    Success = false,
                    Message = $"Download client failed to process the download. Check your client configuration for {downloadClient.ToString()}"
                };
            }

            // Log to history
            await LogDownloadHistory(audiobook, "Search", topResult.SearchResult);

            return new SearchAndDownloadResult
            {
                Success = true,
                Message = $"Successfully sent to download client",
                DownloadId = downloadId2,
                IndexerUsed = "Search",
                DownloadClientUsed = downloadClient.Id,
                SearchResult = topResult.SearchResult
            };
        }

        public async Task<string?> SendToDownloadClientAsync(SearchResult searchResult, string? downloadClientId = null, int? audiobookId = null)
        {
            _logger.LogInformation("SendToDownloadClientAsync called - Title: {Title}, DownloadType: '{DownloadType}', TorrentUrl: {TorrentUrl}, AudiobookId: {AudiobookId}",
                searchResult.Title,
                searchResult.Protocol.ToString(),
                searchResult.TorrentUrl ?? "(null)",
                audiobookId);

            await ResolveDownloadProtocolAsync(searchResult);

            // Check if this is a trusted direct download and handle it differently
            if (DownloadProtocol.DirectDownload == searchResult.Protocol)
            {
                _logger.LogInformation("Processing DDL for: {Title}, AudiobookId: {AudiobookId}", searchResult.Title, audiobookId);
                return await DownloadDirectlyAsync(searchResult, audiobookId);
            }

            _logger.LogInformation(
                "Processing as {DownloadType} after server-side validation for '{Title}'",
                searchResult.Protocol,
                searchResult.Title);

            DownloadClientConfiguration? downloadClient = null;
            if (downloadClientId == null)
            {
                downloadClient = await GetAppropriateDownloadClient(searchResult.Protocol);
                if (downloadClient == null)
                {
                    List<string> validDownloadClientTypes = _downloadClientAdapter.GetClientTypeSupportingProtocol(searchResult.Protocol);
                    throw new Exception($"No suitable download client found for {searchResult.Protocol}. Please configure and enable a {searchResult.Protocol} client ({string.Join(", ", validDownloadClientTypes)}) in Settings.");
                }

                downloadClientId = downloadClient.Id;
                _logger.LogInformation("Auto-selected download client {ClientId} for {Protocol}", downloadClientId, searchResult.Protocol);
            } 
            else
            {
                downloadClient = await _configurationService.GetDownloadClientConfigurationAsync(downloadClientId);
            }

            if (downloadClient == null || !downloadClient.IsEnabled)
            {
                throw new Exception("Download client not found or disabled");
            }

            _logger.LogInformation("Sending to {ClientType} download client: {ClientName}", downloadClient.Type, downloadClient.Name);

            // Ensure downloadClientId is non-null before assignment into model
            var downloadClientIdForModel = downloadClientId ?? string.Empty;

            // Guard against duplicate downloads for the same audiobook.
            // Only block when a truly active download exists (Queued/Downloading/ImportPending)
            // for an enabled download client. Completed downloads don't block — ImportPending
            // covers the "waiting for import" window, and stale records from deleted/reconfigured
            // clients are excluded so they can't silently phantom-block re-downloads.
            if (audiobookId is int audiobookIdValue && audiobookIdValue > 0)
            {
                try
                {
                    var checkContext = await _dbContextFactory.CreateDbContextAsync();

                    var downloadClients = await _configurationService.GetDownloadClientConfigurationsAsync();
                    var enabledClientIds = downloadClients
                        .Where(c => c.IsEnabled && !string.IsNullOrWhiteSpace(c.Id))
                        .Select(c => c.Id)
                        .ToHashSet();

                    var existingActive = await checkContext.Downloads
                        .Where(d => d.AudiobookId == audiobookIdValue)
                        .Where(d => d.Status == DownloadStatus.Queued ||
                                    d.Status == DownloadStatus.Downloading ||
                                    d.Status == DownloadStatus.ImportPending)
                        .Where(d => d.DownloadClientId == "DDL" ||
                                    (!string.IsNullOrEmpty(d.DownloadClientId) && enabledClientIds.Contains(d.DownloadClientId)))
                        .AnyAsync();

                    if (existingActive)
                    {
                        _logger.LogInformation(
                            "Skipping duplicate download for audiobook {AudiobookId} — an active download already exists. Title: '{Title}'",
                            audiobookIdValue, searchResult.Title);
                        return string.Empty;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogDebug(ex, "Failed to check for duplicate downloads for audiobook {AudiobookId} (non-blocking)", audiobookIdValue);
                }
            }

            if (DownloadProtocol.Torrent == searchResult.Protocol)
            {
                // Attempt to cache MyAnonamouse torrents ahead of handing off to qBittorrent
                await TryPrepareMyAnonamouseTorrentAsync(searchResult);
            }

            if (_clientGateway == null)
            {
                throw new InvalidOperationException("Download client gateway is not registered. Ensure AddListenarrAdapters() is invoked during startup.");
            }

            // Route to appropriate client handler via adapter and capture client-specific IDs when provided
            var download = await _clientGateway.AddAsync(downloadClient, SearchResultConverters.ToIndexerSearchResult(searchResult));
            if (download == null)
            {
                _logger.LogError("Unable to add the search result to the appropriate download client");
                return null; 
            }

            download.AudiobookId = audiobookId;
            var dbContext = await _dbContextFactory.CreateDbContextAsync();
            dbContext.Downloads.Add(download);
            await dbContext.SaveChangesAsync();
            _logger.LogInformation("Created download record in database: {DownloadId} for '{Title}'", download.Id, searchResult.Title);
            
            // Record in download history for idempotency tracking
            if (_downloadHistoryService != null && !string.IsNullOrEmpty(downloadClientIdForModel))
            {
                try
                {
                    await _downloadHistoryService.RecordGrabbedAsync(
                        download.Id,
                        downloadClientIdForModel,
                        searchResult.Title ?? "Unknown",
                        searchResult.Protocol);
                    _logger.LogInformation("Recorded grabbed event in history for download {DownloadId}", download.Id);
                }
                catch (Exception histEx) when (histEx is not OperationCanceledException && histEx is not OutOfMemoryException && histEx is not StackOverflowException) {
                    _logger.LogWarning(histEx, "Failed to record grabbed event in history for download {DownloadId} (non-critical)", download.Id);
                }
            }

            // Send notification for book-downloading event
            if (_notificationService != null)
            {
                using (var scope = _serviceScopeFactory.CreateScope())
                {
                    var configService = scope.ServiceProvider.GetService<IConfigurationService>() ?? _configurationService;
                    var settings = configService != null ? await configService.GetApplicationSettingsAsync() : new ApplicationSettings();

                    // Fetch audiobook data if available for better notification content
                    object notificationData;
                    if (audiobookId.HasValue)
                    {
                        var notifContext = await _dbContextFactory.CreateDbContextAsync();
                        var audiobook = await notifContext.Audiobooks.FindAsync(audiobookId.Value);
                        notificationData = audiobook != null
                            ? new
                            {
                                title = audiobook.Title,
                                authors = audiobook.Authors,
                                asin = audiobook.Asin,
                                publisher = audiobook.Publisher,
                                year = audiobook.PublishYear?.ToString(),
                                publishedDate = audiobook.PublishYear?.ToString(),
                                imageUrl = audiobook.ImageUrl,
                                narrators = audiobook.Narrators,
                                description = audiobook.Description,
                                downloadId = download.Id,
                                source = searchResult.Source ?? "Unknown Source",
                                downloadClient = downloadClient.Name ?? "Unknown Client",
                                size = searchResult.Size
                            }
                            : new
                            {
                                downloadId = download.Id,
                                title = searchResult.Title ?? "Unknown Title",
                                artist = searchResult.Artist ?? "Unknown Artist",
                                album = searchResult.Album ?? "Unknown Album",
                                size = searchResult.Size,
                                source = searchResult.Source ?? "Unknown Source",
                                downloadClient = downloadClient.Name ?? "Unknown Client",
                                audiobookId = audiobookId
                            };
                    }
                    else
                    {
                        // No audiobook ID, use search result data
                        notificationData = new
                        {
                            downloadId = download.Id,
                            title = searchResult.Title ?? "Unknown Title",
                            artist = searchResult.Artist ?? "Unknown Artist",
                            album = searchResult.Album ?? "Unknown Album",
                            size = searchResult.Size,
                            source = searchResult.Source ?? "Unknown Source",
                            downloadClient = downloadClient.Name ?? "Unknown Client"
                        };
                    }

                    await _notificationService.SendNotificationAsync("book-downloading", notificationData, settings.WebhookUrl, settings.EnabledNotificationTriggers);
                }
            }

            // Trigger immediate queue update via SignalR so the UI shows the new download right away
            // Add a small delay to allow the download client to process and index the new download
            try
            {
                _logger.LogInformation("Waiting briefly for download client to process new download...");
                await Task.Delay(1500); // Give qBittorrent/other clients time to index the torrent

                _logger.LogInformation("Triggering immediate queue update after sending download to client");
                var currentQueueSnapshot = await _downloadQueueService.GetQueueSnapshotAsync();
                if (_hubBroadcaster != null)
                {
                    await _hubBroadcaster.BroadcastQueueUpdateAsync(currentQueueSnapshot);
                    _logger.LogInformation("Immediate queue update sent with {Count} items via IHubBroadcaster", currentQueueSnapshot?.Items?.Count ?? 0);
                }
                else
                {
                    // Fallback for older registrations
                    using (var scope = _serviceScopeFactory.CreateScope())
                    {
                        var hubContext = scope.ServiceProvider.GetService<Microsoft.AspNetCore.SignalR.IHubContext<Listenarr.Api.Hubs.DownloadHub>>();
                        if (hubContext != null)
                        {
                            await hubContext.Clients.All.SendAsync("QueueUpdate", currentQueueSnapshot);
                            _logger.LogInformation("Immediate queue update sent with {Count} items", currentQueueSnapshot?.Items?.Count ?? 0);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogWarning(ex, "Failed to trigger immediate queue update (non-fatal)");
            }

            return download.Id;
        }

        private async Task TryPrepareMyAnonamouseTorrentAsync(SearchResult searchResult, string? downloadId = null)
        {
            ArgumentNullException.ThrowIfNull(searchResult);

            _logger.LogInformation("TryPrepareMyAnonamouseTorrentAsync called for '{Title}', IndexerId: {IndexerId}, TorrentUrl: '{TorrentUrl}'", 
                searchResult.Title, searchResult.IndexerId, searchResult.TorrentUrl);
            
            // Security: Validate all preconditions before performing sensitive operations
            // This method downloads content using authenticated HTTP clients, so we must
            // ensure the request is legitimate and comes from a trusted, configured source.
            
            if (searchResult.IndexerId == null)
            {
                _logger.LogWarning("TryPrepareMyAnonamouseTorrentAsync: No IndexerId for '{Title}' - skipping", searchResult.Title);
                // Reject: No database-backed indexer ID provided
                return;
            }

            if (string.IsNullOrEmpty(searchResult.TorrentUrl))
            {
                _logger.LogDebug("Skipping MyAnonamouse cache: no TorrentUrl for '{Title}'", LogRedaction.SanitizeText(searchResult.Title));
                return;
            }

            if (searchResult.TorrentFileContent != null && searchResult.TorrentFileContent.Length > 0)
            {
                _logger.LogDebug("MyAnonamouse torrent already cached for '{Title}'", searchResult.Title);
                return;
            }

            try
            {
                var dbContext = await _dbContextFactory.CreateDbContextAsync();
                
                // Security: Fetch indexer from database using the validated ID
                // Only trusted, administrator-configured indexers can trigger authenticated requests
                var indexer = await dbContext.Indexers.FindAsync(searchResult.IndexerId.Value);

                // Security: Indexer must exist in database - reject if not found
                if (indexer == null)
                {
                    _logger.LogWarning("Unable to cache MyAnonamouse torrent for '{Title}': indexer configuration not found", searchResult.Title);
                    return;
                }

                // Security: Validate against database-stored indexer configuration, not user-provided search result
                if (Implementation.MyAnonamouse != indexer.Implementation)
                {
                    _logger.LogDebug("Skipping MyAnonamouse cache: indexer {IndexerName} is not MyAnonamouse (is {Implementation})", 
                        indexer.Name, indexer.Implementation);
                    return;
                }

                // Parse and validate URLs
                if (!Uri.TryCreate(searchResult.TorrentUrl, UriKind.Absolute, out var torrentUri) ||
                    !Uri.TryCreate(indexer.Url, UriKind.Absolute, out var indexerUri))
                {
                    _logger.LogWarning("Unable to cache MyAnonamouse torrent for '{Title}': invalid URL(s). Torrent={Url}, Indexer={IndexerUrl}", searchResult.Title, LogRedaction.SanitizeUrl(searchResult.TorrentUrl), indexer.Url);
                    return;
                }

                if (!string.Equals(torrentUri.Host, indexerUri.Host, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("MyAnonamouse torrent host {TorrentHost} differs from indexer host {IndexerHost}. Proceeding with explicit cookie header.", torrentUri.Host, indexerUri.Host);
                }

                var mamId = MyAnonamouseHelper.TryGetMamId(indexer.AdditionalSettings);
                if (string.IsNullOrEmpty(mamId))
                {
                    _logger.LogWarning("Unable to cache MyAnonamouse torrent for '{Title}': mam_id missing from indexer {IndexerName}", searchResult.Title, indexer.Name);
                    return;
                }

                // Use factory client for the initial attempt (allows test injection).
                // If auto-redirect drops the Cookie header, a fallback retry with
                // CreateAuthenticatedHttpClient (AllowAutoRedirect=false) handles it below.
                var httpClientToUse = _httpClientFactory != null
                    ? _httpClientFactory.CreateClient()
                    : MyAnonamouseHelper.CreateAuthenticatedHttpClient(mamId, indexer.Url);

                _logger.LogDebug("Downloading MyAnonamouse torrent for '{Title}' from {Url}", searchResult.Title, LogRedaction.SanitizeUrl(searchResult.TorrentUrl));

                // Follow redirects manually so we can re-apply cookies and Host header on each hop (mimic Prowlarr)
                var currentUri = torrentUri;
                HttpResponseMessage? response = null;
                for (int redirectAttempt = 0; redirectAttempt < 6; redirectAttempt++)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, currentUri);
                    // Set common headers for MAM to mimic a browser request (some endpoints require this)
                    req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
                    req.Headers.Referrer = new Uri("https://www.myanonamouse.net/");
                    req.Headers.Accept.ParseAdd("application/x-bittorrent, application/octet-stream, */*; q=0.01");

                    // Ensure the authenticated session is sent even if the download host differs by adding Cookie header as well
                    if (!string.IsNullOrEmpty(mamId))
                        req.Headers.Add("Cookie", $"mam_id={mamId}");

                    // Always set Host header to the indexer host so tracker sees the expected host
                    var hostHeader = indexerUri.IsDefaultPort ? indexerUri.Host : $"{indexerUri.Host}:{indexerUri.Port}";
                    req.Headers.Host = hostHeader;

                    _logger.LogDebug("Downloading MyAnonamouse torrent for '{Title}' from {Url} (attempt {Attempt})", searchResult.Title, LogRedaction.SanitizeUrl(currentUri.ToString()), redirectAttempt + 1);

                    response = await httpClientToUse.SendAsync(req);

                    // Persist mam_id from intermediate responses (Set-Cookie)
                    try
                    {
                        var newMam = MyAnonamouseHelper.TryExtractMamIdFromResponse(response);
                        if (!string.IsNullOrEmpty(newMam) && !string.Equals(newMam, mamId, StringComparison.Ordinal))
                        {
                            _logger.LogInformation("MyAnonamouse: received updated mam_id from download redirect response for indexer {Name}", indexer.Name);
                            // Persist to database by re-loading the tracked indexer entity and updating it
                            var persistedIndexer = await dbContext.Indexers.FindAsync(indexer.Id);
                            if (persistedIndexer != null)
                            {
                                persistedIndexer.AdditionalSettings = MyAnonamouseHelper.UpdateMamIdInAdditionalSettings(persistedIndexer.AdditionalSettings, newMam);
                                await dbContext.SaveChangesAsync();
                            }

                            // Keep local copy in sync
                            indexer.AdditionalSettings = MyAnonamouseHelper.UpdateMamIdInAdditionalSettings(indexer.AdditionalSettings, newMam);
                            mamId = newMam;
                        }
                    }
                    catch (Exception exMam) when (exMam is not OperationCanceledException && exMam is not OutOfMemoryException && exMam is not StackOverflowException) {
                        _logger.LogDebug(exMam, "Failed to persist updated mam_id from MyAnonamouse redirect response");
                    }

                    // Handle redirects manually
                    if (response.StatusCode == System.Net.HttpStatusCode.MovedPermanently ||
                        response.StatusCode == System.Net.HttpStatusCode.Found ||
                        response.StatusCode == System.Net.HttpStatusCode.SeeOther ||
                        response.StatusCode == System.Net.HttpStatusCode.TemporaryRedirect ||
                        response.StatusCode == System.Net.HttpStatusCode.PermanentRedirect)
                    {
                        if (response.Headers.Location == null)
                        {
                            _logger.LogWarning("MyAnonamouse torrent download redirect without Location header for '{Title}'", searchResult.Title);
                            response.Dispose();
                            return;
                        }

                        var next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(currentUri, response.Headers.Location);
                        _logger.LogDebug("Following MyAnonamouse redirect to {Next}", LogRedaction.SanitizeUrl(next.ToString()));
                        response.Dispose();
                        currentUri = next;
                        continue;
                    }

                    // Not a redirect - break to process the response
                    break;
                }

                if (response == null)
                {
                    _logger.LogWarning("Failed to download MyAnonamouse torrent for '{Title}': no response", searchResult.Title);
                    return;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("MyAnonamouse torrent download failed for '{Title}' with status {Status}", searchResult.Title, response.StatusCode);
                    response.Dispose();
                    return;
                }

                var torrentBytes = await response.Content.ReadAsByteArrayAsync();
                if (torrentBytes == null || torrentBytes.Length == 0)
                {
                    _logger.LogWarning("MyAnonamouse torrent download for '{Title}' returned empty payload", searchResult.Title);
                    response.Dispose();
                    return;
                }

                // Quick sanity check: ensure the payload looks like a torrent (bencoded dictionary / contains 'announce'/'info')
                var looksLikeTorrent = (torrentBytes.Length > 0 && torrentBytes[0] == (byte)'d') ||
                                       System.Text.Encoding.ASCII.GetString(torrentBytes.Take(Math.Min(200, torrentBytes.Length)).ToArray()).IndexOf("announce", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!looksLikeTorrent)
                {
                    // The factory HttpClient may have auto-followed redirects, silently
                    // dropping the Cookie header (AllowAutoRedirect=true is the default).
                    // Retry with a dedicated client that disables auto-redirect so the
                    // manual redirect loop can re-apply cookies on each hop.
                    _logger.LogDebug("Factory client returned non-torrent payload for '{Title}', retrying with authenticated MAM client", searchResult.Title);
                    response.Dispose();
                    response = null;

                    try
                    {
                        using var authClient = MyAnonamouseHelper.CreateAuthenticatedHttpClient(mamId, indexer.Url);
                        var retryUri = torrentUri;
                        for (int retryHop = 0; retryHop < 6; retryHop++)
                        {
                            using var retryReq = new HttpRequestMessage(HttpMethod.Get, retryUri);
                            retryReq.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
                            retryReq.Headers.Referrer = new Uri("https://www.myanonamouse.net/");
                            retryReq.Headers.Accept.ParseAdd("application/x-bittorrent, application/octet-stream, */*; q=0.01");
                            if (!string.IsNullOrEmpty(mamId))
                                retryReq.Headers.Add("Cookie", $"mam_id={mamId}");
                            var retryHost = indexerUri.IsDefaultPort ? indexerUri.Host : $"{indexerUri.Host}:{indexerUri.Port}";
                            retryReq.Headers.Host = retryHost;

                            response = await authClient.SendAsync(retryReq);

                            if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400 && response.Headers.Location != null)
                            {
                                retryUri = response.Headers.Location.IsAbsoluteUri
                                    ? response.Headers.Location
                                    : new Uri(retryUri, response.Headers.Location);
                                response.Dispose();
                                response = null;
                                continue;
                            }
                            break;
                        }

                        if (response != null && response.IsSuccessStatusCode)
                        {
                            torrentBytes = await response.Content.ReadAsByteArrayAsync();
                            looksLikeTorrent = torrentBytes != null && torrentBytes.Length > 0 &&
                                ((torrentBytes[0] == (byte)'d') ||
                                 System.Text.Encoding.ASCII.GetString(torrentBytes.Take(Math.Min(200, torrentBytes.Length)).ToArray())
                                     .IndexOf("announce", StringComparison.OrdinalIgnoreCase) >= 0);
                            if (looksLikeTorrent)
                                _logger.LogInformation("Authenticated MAM client successfully downloaded torrent for '{Title}' ({Bytes} bytes)", searchResult.Title, torrentBytes!.Length);
                        }
                    }
                    catch (Exception retryEx) when (retryEx is not OperationCanceledException && retryEx is not OutOfMemoryException && retryEx is not StackOverflowException)
                    {
                        _logger.LogDebug(retryEx, "Retry with authenticated MAM client also failed (non-fatal)");
                    }
                }

                if (!looksLikeTorrent)
                {
                    var snippet = System.Text.Encoding.UTF8.GetString((torrentBytes ?? Array.Empty<byte>()).Take(Math.Min(512, torrentBytes?.Length ?? 0)).ToArray());
                    if (System.Text.RegularExpressions.Regex.IsMatch(snippet, "Unrecognized host|PassKey|Pass Key|Unrecognized", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        _logger.LogWarning("MyAnonamouse torrent download for '{Title}' returned an authorization error page from tracker: {Snippet}", searchResult.Title, LogRedaction.RedactText(snippet, LogRedaction.GetSensitiveValuesFromEnvironment()));
                    }
                    else
                    {
                        _logger.LogWarning("MyAnonamouse torrent download for '{Title}' returned unexpected non-torrent payload (first 200 chars): {Snippet}", searchResult.Title, LogRedaction.RedactText(snippet, LogRedaction.GetSensitiveValuesFromEnvironment()));
                    }

                    response?.Dispose();
                    return;
                }

                // torrentBytes is guaranteed non-null here: looksLikeTorrent check above returns early otherwise
                if (torrentBytes == null) return;

                // Additional debug info to help diagnose cases where content looks like a torrent but tracker still rejects it
                var contentType = response?.Content.Headers.ContentType?.ToString() ?? "(none)";
                var firstBytesHex = BitConverter.ToString(torrentBytes.Take(Math.Min(16, torrentBytes.Length)).ToArray()).Replace("-", " ");
                var containsAnnounce = System.Text.Encoding.ASCII.GetString(torrentBytes.Take(Math.Min(512, torrentBytes.Length)).ToArray()).IndexOf("announce", StringComparison.OrdinalIgnoreCase) >= 0;
                _logger.LogDebug("MyAnonamouse torrent payload debug: ContentType={ContentType}, FirstBytes={FirstBytesHex}, ContainsAnnounce={ContainsAnnounce}", contentType, firstBytesHex, containsAnnounce);

                // If the torrent references the numeric IP host, rewrite announce/tracker strings to the configured indexer host
                try
                {
                    if (!string.IsNullOrEmpty(indexerUri.Host))
                    {
                        var ascii = System.Text.Encoding.ASCII.GetString(torrentBytes);

                        // 1) If torrent references the original torrent host (often IP), replace it
                        if (!string.IsNullOrEmpty(torrentUri.Host) && ascii.IndexOf(torrentUri.Host, StringComparison.OrdinalIgnoreCase) >= 0 &&
                            !string.Equals(torrentUri.Host, indexerUri.Host, StringComparison.OrdinalIgnoreCase))
                        {
                            var replaced = MyAnonamouseHelper.ReplaceHostInTorrent(torrentBytes, torrentUri.Host, indexerUri.Host);
                            if (replaced != null && replaced.Length > 0)
                            {
                                torrentBytes = replaced;
                                _logger.LogInformation("Rewrote torrent tracker host from {OldHost} to {NewHost} for '{Title}'", torrentUri.Host, indexerUri.Host, searchResult.Title);
                                ascii = System.Text.Encoding.ASCII.GetString(torrentBytes);
                            }
                        }

                        // 2) Heuristic: replace any bare IPv4 addresses found inside torrent with the indexer host
                        try
                        {
                            var ipMatches = System.Text.RegularExpressions.Regex.Matches(ascii, @"\b\d{1,3}(?:\.\d{1,3}){3}\b");
                            var distinctIps = ipMatches.Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value).Distinct().ToList();
                            foreach (var ip in distinctIps.Where(ip =>
                                !ip.StartsWith("127.")
                                && !ip.StartsWith("10.")
                                && !ip.StartsWith("192.168.")
                                && !ip.StartsWith("172.")
                                && !string.Equals(ip, indexerUri.Host, StringComparison.OrdinalIgnoreCase)))
                            {
                                var replaced2 = MyAnonamouseHelper.ReplaceHostInTorrent(torrentBytes, ip, indexerUri.Host);
                                if (replaced2 != null && replaced2.Length > 0)
                                {
                                    torrentBytes = replaced2;
                                    _logger.LogInformation("Rewrote torrent IP host {Ip} to indexer host {Host} for '{Title}'", ip, indexerUri.Host, searchResult.Title);
                                }
                            }
                        }
                        catch (Exception rex2) when (rex2 is not OperationCanceledException && rex2 is not OutOfMemoryException && rex2 is not StackOverflowException) {
                            _logger.LogDebug(rex2, "Failed to rewrite numeric IPs inside torrent (non-fatal)");
                        }

                        // 3) Log announce URLs for diagnostics — do NOT rewrite legitimate tracker subdomains
                        //    (e.g., t.myanonamouse.net is the actual tracker and must not be changed to www.myanonamouse.net)
                        try
                        {
                            var announces = MyAnonamouseHelper.ExtractAnnounceUrls(torrentBytes);
                            if (announces != null && announces.Count > 0)
                            {
                                _logger.LogDebug("Torrent announce URLs for '{Title}': {Announces}", searchResult.Title, string.Join(", ", announces.Distinct()));
                            }
                        }
                        catch (Exception rex3) when (rex3 is not OperationCanceledException && rex3 is not OutOfMemoryException && rex3 is not StackOverflowException) {
                            _logger.LogDebug(rex3, "Failed to extract announce URLs from torrent (non-fatal)");
                        }
                    }
                }
                catch (Exception rex) when (rex is not OperationCanceledException && rex is not OutOfMemoryException && rex is not StackOverflowException) {
                    _logger.LogDebug(rex, "Failed to rewrite torrent tracker hosts (non-fatal)");
                }

                // If we have a mam_id, attempt to append it to any announce URLs inside the torrent so trackers that rely on passkey in query will accept it.
                try
                {
                    if (!string.IsNullOrEmpty(mamId))
                    {
                        var normalizedMamId = NormalizeMamId(mamId);
                        _logger.LogInformation("MyAnonamouse: normalizing mam_id from '{Raw}' to '{Normalized}' for '{Title}'", LogRedaction.RedactText(mamId, LogRedaction.GetSensitiveValuesFromEnvironment()), LogRedaction.RedactText(normalizedMamId, LogRedaction.GetSensitiveValuesFromEnvironment()), searchResult.Title);

                        var currentAnnounces = MyAnonamouseHelper.ExtractAnnounceUrls(torrentBytes);
                        var updatedAnnounces = new System.Collections.Generic.List<string>();
                        var modified = false;

                        foreach (var ann in (currentAnnounces ?? new System.Collections.Generic.List<string>())
                            .Where(ann => !string.IsNullOrWhiteSpace(ann))
                            .Distinct())
                        {
                            // Only append mam_id to actual tracker announce URLs, not file/web-seed URLs
                            if (!ann.Contains("/announce", StringComparison.OrdinalIgnoreCase) && !ann.Contains("/tracker", StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.LogDebug("Skipping non-tracker URL for mam_id append: {Url}", ann);
                                continue;
                            }
                            // don't double-append if already present
                            if (ann.IndexOf("mam_id=", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                updatedAnnounces.Add(ann);
                                continue;
                            }

                            try
                            {
                                var separator = ann.Contains("?") ? "&" : "?";
                                var newAnn = ann + separator + "mam_id=" + normalizedMamId;

                                var replaced = MyAnonamouseHelper.ReplaceStringInTorrent(torrentBytes, ann, newAnn);
                                if (replaced != null && replaced.Length > 0)
                                {
                                    torrentBytes = replaced;
                                    modified = true;
                                }

                                updatedAnnounces.Add(newAnn);
                            }
                            catch (Exception inner) when (inner is not OperationCanceledException && inner is not OutOfMemoryException && inner is not StackOverflowException) {
                                _logger.LogDebug(inner, "Non-fatal failure while attempting to append mam_id to announce {Ann} for '{Title}'", ann, searchResult.Title);
                                updatedAnnounces.Add(ann);
                            }
                        }

                        if (modified)
                            _logger.LogInformation("Appended mam_id to MyAnonamouse announce URLs for '{Title}' - count={Count}", searchResult.Title, updatedAnnounces.Count);
                    }
                }
                catch (Exception exAppend) when (exAppend is not OperationCanceledException && exAppend is not OutOfMemoryException && exAppend is not StackOverflowException) {
                    _logger.LogDebug(exAppend, "Failed to append mam_id to MyAnonamouse announces (non-fatal)");
                }

                searchResult.TorrentFileContent = torrentBytes;
                searchResult.TorrentFileName = response != null ? MyAnonamouseHelper.ResolveTorrentFileName(response, searchResult.TorrentUrl) : "myanonamouse.torrent";
                _logger.LogInformation("Cached MyAnonamouse torrent for '{Title}' ({Bytes} bytes)", searchResult.Title, torrentBytes.Length);

                // If a downloadId was provided, store the cached torrent (bytes + filename) to the in-memory cache so it can be retrieved for diagnostics.
                if (!string.IsNullOrEmpty(downloadId))
                {
                    try
                    {
                        var cacheKey = $"mam:cachedtorrent:{downloadId}";
                        _cache.Set(cacheKey + ":bytes", torrentBytes, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(30) });
                        _cache.Set(cacheKey + ":name", searchResult.TorrentFileName ?? "download.torrent", new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(30) });
                        _logger.LogInformation("Cached MyAnonamouse torrent bytes and filename to memory for download {DownloadId}", downloadId);
                    }
                    catch (Exception cex) when (cex is not OperationCanceledException && cex is not OutOfMemoryException && cex is not StackOverflowException) {
                        _logger.LogDebug(cex, "Failed to place cached MyAnonamouse torrent into memory cache (non-fatal)");
                    }
                }
                try
                {
                    var announces = MyAnonamouseHelper.ExtractAnnounceUrls(torrentBytes);
                    var count = announces?.Count ?? 0;
                    var unique = count > 0 ? string.Join(", ", announces?.Take(10) ?? Enumerable.Empty<string>()) : "(none)";
                    _logger.LogInformation("Cached MyAnonamouse torrent announces for '{Title}' - count={Count}: {Announces}", searchResult.Title, count, LogRedaction.RedactText(unique, LogRedaction.GetSensitiveValuesFromEnvironment()));

                    // Also cache the extracted announce URLs for quick retrieval by diagnostics endpoints
                    if (!string.IsNullOrEmpty(downloadId) && announces != null && announces.Count > 0)
                    {
                        try
                        {
                            var cacheKey = $"mam:cachedtorrent:{downloadId}";
                            _cache.Set(cacheKey + ":announces", announces, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(30) });
                            _logger.LogInformation("Cached MyAnonamouse torrent announces to memory for download {DownloadId}", downloadId);
                        }
                        catch (Exception cexAnn) when (cexAnn is not OperationCanceledException && cexAnn is not OutOfMemoryException && cexAnn is not StackOverflowException) {
                            _logger.LogDebug(cexAnn, "Failed to place cached MyAnonamouse announces into memory cache (non-fatal)");
                        }
                    }
                }
                catch (Exception exAnn) when (exAnn is not OperationCanceledException && exAnn is not OutOfMemoryException && exAnn is not StackOverflowException) {
                    _logger.LogDebug(exAnn, "Failed to extract announce URLs from cached torrent (non-fatal)");
                }
                response?.Dispose();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogWarning(ex, "Failed to cache MyAnonamouse torrent for '{Title}'", searchResult.Title);
            }
        }

        private string BuildSearchQuery(Audiobook audiobook)
        {
            // Build a search query from audiobook metadata
            var parts = new List<string>();

            if (!string.IsNullOrEmpty(audiobook.Title))
                parts.Add(audiobook.Title);

            if (audiobook.Authors != null && audiobook.Authors.Any())
                parts.Add(audiobook.Authors.First());

            return string.Join(" ", parts);
        }

        private SearchResult GetBestResult(List<SearchResult> results, string indexerType)
        {
            // For torrents, prefer highest seeders
            // For NZBs, prefer newest/largest
            return IsTorrentIndexer(indexerType)
                ? results.OrderByDescending(r => r.Seeders).ThenByDescending(r => r.Size).First()
                : results.OrderByDescending(r => r.PublishedDate).ThenByDescending(r => r.Size).First();
        }

        private bool IsTorrentIndexer(string indexerType)
        {
            return indexerType.ToLower() == "torrent";
        }

        /// <summary>
        /// Derives the protocol to use for this search result
        /// Serves as a spoof protection and gives back the original protocol if none is found
        /// </summary>
        /// <param name="result">Search result to check</param>
        /// <returns></returns>
        private async Task<DownloadProtocol> ResolveDownloadProtocolAsync(SearchResult result)
        {
            ArgumentNullException.ThrowIfNull(result);

            var protocol = result.Protocol;

            if (!string.IsNullOrWhiteSpace(result.NzbUrl))
            {
                _logger.LogDebug("Result identified as Usenet from NzbUrl: {Title}", result.Title);
                protocol = DownloadProtocol.Usenet;
            }

            else if (!string.IsNullOrWhiteSpace(result.MagnetLink))
            {
                _logger.LogDebug("Result identified as Torrent from MagnetLink: {Title}", result.Title);
                protocol = DownloadProtocol.Torrent;
            }

            else if (result.TorrentFileContent != null && result.TorrentFileContent.Length > 0)
            {
                _logger.LogDebug("Result identified as Torrent from cached torrent bytes: {Title}", result.Title);
                protocol = DownloadProtocol.Torrent;
            }

            else if (await IsTrustedDirectDownloadAsync(result))
            {
                _logger.LogDebug("Result identified as trusted DDL from configured Internet Archive indexer: {Title}", result.Title);
                protocol = DownloadProtocol.DirectDownload;
            }

            else if (DownloadClientUriBuilder.TryParseHttpOrHttpsAbsoluteUri(result.TorrentUrl, out _))
            {
                _logger.LogDebug("Result identified as Torrent from TorrentUrl: {Title}", result.Title);
                protocol = DownloadProtocol.Torrent;
            }

            result.Protocol = protocol;

            return protocol;
        }

        private async Task<bool> IsTrustedDirectDownloadAsync(SearchResult result)
        {
            if (result?.IndexerId is not int indexerId || indexerId <= 0)
            {
                return false;
            }

            if (!DownloadClientUriBuilder.TryParseHttpOrHttpsAbsoluteUri(result.TorrentUrl, out var downloadUri) ||
                downloadUri == null)
            {
                return false;
            }

            if (!IsTrustedArchiveOrgHost(downloadUri) ||
                !downloadUri.AbsolutePath.StartsWith("/download/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                var dbContext = await _dbContextFactory.CreateDbContextAsync();
                var indexer = await dbContext.Indexers
                    .AsNoTracking()
                    .FirstOrDefaultAsync(i => i.Id == indexerId);

                if (indexer == null || !indexer.IsEnabled)
                {
                    _logger.LogDebug(
                        "Direct-download validation rejected '{Title}': indexer {IndexerId} was missing or disabled",
                        result.Title,
                        indexerId);
                    return false;
                }

                if (Implementation.InternetArchive != indexer.Implementation)
                {
                    _logger.LogDebug(
                        "Direct-download validation rejected '{Title}': indexer {IndexerId} implementation was {Implementation}",
                        result.Title,
                        indexerId,
                        indexer.Implementation);
                    return false;
                }

                if (!Uri.TryCreate(indexer.Url, UriKind.Absolute, out var indexerUri) ||
                    !IsTrustedArchiveOrgHost(indexerUri))
                {
                    _logger.LogDebug(
                        "Direct-download validation rejected '{Title}': configured indexer URL '{IndexerUrl}' is not a trusted archive.org host",
                        result.Title,
                        indexer.Url);
                    return false;
                }

                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to validate direct-download route for '{Title}' against configured indexer {IndexerId}",
                    result.Title,
                    indexerId);
                return false;
            }
        }

        private static bool IsTrustedArchiveOrgHost(Uri uri)
        {
            var host = uri.Host.Trim();
            return host.Equals("archive.org", StringComparison.OrdinalIgnoreCase) ||
                   host.EndsWith(".archive.org", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTorrentResult(SearchResult result)
        {
            // Use transport indicators only. Do not trust caller-provided DownloadType.
            if (!string.IsNullOrEmpty(result.NzbUrl))
            {
                //_logger.LogDebug("Result identified as NZB (has NzbUrl): {Title}", result.Title);
                return false;
            }

            if (result.TorrentFileContent != null && result.TorrentFileContent.Length > 0)
            {
                //_logger.LogDebug("Result identified as Torrent (has cached torrent bytes): {Title}", result.Title);
                return true;
            }

            if (!string.IsNullOrEmpty(result.MagnetLink) ||
                DownloadClientUriBuilder.TryParseHttpOrHttpsAbsoluteUri(result.TorrentUrl, out _))
            {
                //_logger.LogDebug("Result identified as Torrent (has MagnetLink or TorrentUrl): {Title}", result.Title);
                return true;
            }

            // If neither is set, we can't reliably determine the type
            // Log a warning and default to false (NZB) as a safer choice
            //_logger.LogWarning("Unable to determine result type for '{Title}' from source '{Source}'. No MagnetLink, TorrentUrl, or NzbUrl found. Defaulting to NZB.",
            //    result.Title, result.Source);
            return false;
        }

        // Small container for caching torrent bytes + filename in memory
        private class CachedTorrent
        {
            public byte[]? Bytes { get; set; }
            public string? FileName { get; set; }
        }

        private async Task<DownloadClientConfiguration?> GetAppropriateDownloadClient(DownloadProtocol protocol)
        {
            var downloadClients = await _configurationService.GetDownloadClientConfigurationsAsync();
            var enabledClients = downloadClients.Where(c => c.IsEnabled).ToList();

            _logger.LogInformation("Looking for {Protocol} client. Found {Count} enabled download clients: {Clients}",
                protocol,
                enabledClients.Count,
                string.Join(", ", enabledClients.Select(c => c.ToString())));
            
            // Check type of download client required based on existing IDownloadClientAdapter
            List<string> validDownloadClientTypes = _downloadClientAdapter.GetClientTypeSupportingProtocol(protocol);
            
            _logger.LogDebug("Found the following valid client types: {types}", string.Join(", ", validDownloadClientTypes));
            
            var client = downloadClients
                .FirstOrDefault(configuration => validDownloadClientTypes.Contains(configuration.Type));
            if (client != null)
            {
                _logger.LogInformation("Selected {Protocol} client: {ClientName} ({ClientType})", protocol, client.Name, client.Type);
                return client;
            }

            _logger.LogWarning("No {Protocol} client found among enabled clients", protocol);
            return null;
        }

        public async Task<List<QueueItem>> GetQueueAsync()
        {
            // Queue assembly now lives in DownloadQueueService so the API and background
            // monitor both read from the same reconciliation path.
            return await _downloadQueueService.GetQueueAsync();
        }

        public async Task<bool> RemoveFromQueueAsync(string downloadId, string? downloadClientId = null, bool force = false)
        {
            try
            {
                bool removedFromClient = false;
                Download? downloadRecord = null;

                // Find the database record first
                var dbContext = await _dbContextFactory.CreateDbContextAsync();

                // Try to find by direct ID match first
                downloadRecord = await dbContext.Downloads.FindAsync(downloadId);

                // If not found, try to find by client-specific ID (e.g., torrent hash)
                // Note: Metadata is JSON, so we need to load and filter in memory
                if (downloadRecord == null)
                {
                    var allDownloads = await dbContext.Downloads.ToListAsync();
                    downloadRecord = allDownloads.FirstOrDefault(d => 
                        d.Metadata != null &&
                        ((d.Metadata.TryGetValue("ClientDownloadId", out var clientIdObj) &&
                          string.Equals(clientIdObj?.ToString(), downloadId, StringComparison.OrdinalIgnoreCase)) ||
                         (d.Metadata.TryGetValue("TorrentHash", out var hashObj) &&
                          string.Equals(hashObj?.ToString(), downloadId, StringComparison.OrdinalIgnoreCase))));
                }

                // If still not found, try enhanced title/name matching for legacy downloads
                if (downloadRecord == null && downloadClientId != null)
                {
                    var client = await _configurationService.GetDownloadClientConfigurationAsync(downloadClientId);
                    if (client != null)
                    {
                        // Get queue item to find title
                        var queue = await GetQueueAsync();
                        var queueItem = queue.FirstOrDefault(q => q.Id == downloadId && q.DownloadClientId == downloadClientId);

                        if (queueItem != null)
                        {
                            downloadRecord = await dbContext.Downloads
                                .Where(d => d.DownloadClientId == downloadClientId)
                                .ToListAsync()
                                .ContinueWith(task => task.Result.FirstOrDefault(d =>
                                    IsMatchingTitle(d.Title, queueItem.Title)));
                        }
                    }
                }

                // FIXME: Rework this so the method does not accepts download ID in the first place
                // Nothing worked, create a fake Download record with the given ID
                if (downloadRecord == null)
                {
                    // Create a fake record for now so this does not needs to be refactored just now
                    downloadRecord = new Download()
                    {
                        Id = downloadId,
                        ErrorMessage = "NO_DB_RECORD"
                    };
                }

                // If force=true, skip client removal and just remove from database
                if (force)
                {
                    _logger.LogWarning("Force removal requested for {DownloadId}, skipping client removal", downloadId);
                    removedFromClient = true;
                }
                else if (downloadClientId == null)
                {
                    // Try all clients to find and remove the item
                    var downloadClients = await _configurationService.GetDownloadClientConfigurationsAsync();
                    var enabledClients = downloadClients.Where(c => c.IsEnabled).ToList();

                    foreach (var client in enabledClients)
                    {
                        removedFromClient = await RemoveFromClientAsync(client, downloadRecord);
                        if (removedFromClient)
                        {
                            downloadClientId = client.Id; // Track which client it was removed from
                            break;
                        }
                    }
                }
                else
                {
                    // Check if the downloadClientId is a valid client configuration
                    var client = await _configurationService.GetDownloadClientConfigurationAsync(downloadClientId);
                    if (client != null && !client.IsEnabled)
                    {
                        _logger.LogInformation("Skipping removal of {DownloadId} from disabled client {ClientName}", downloadId, client.Name);
                    }
                    else if (client != null)
                    {
                        removedFromClient = await RemoveFromClientAsync(client, downloadRecord);
                    }
                    else
                    {
                        // If client not found by ID, this might be a legacy/invalid client ID
                        // Try to find the download in the database and check if it's DDL or has a valid client
                        if (downloadRecord.ErrorMessage != "NO_DB_RECORD")
                        {
                            if (downloadRecord.DownloadClientId == "DDL")
                            {
                                // DDL downloads don't have an external client to remove from
                                removedFromClient = true;
                                _logger.LogInformation("Download {DownloadId} is DDL, skipping external client removal", downloadId);
                            }
                            else if (!string.IsNullOrEmpty(downloadRecord.DownloadClientId))
                            {
                                // Try with the download record's client ID
                                var recordClient = await _configurationService.GetDownloadClientConfigurationAsync(downloadRecord.DownloadClientId);
                                if (recordClient != null && !recordClient.IsEnabled)
                                {
                                    _logger.LogInformation("Skipping removal of {DownloadId} from disabled client {ClientName}", downloadId, recordClient.Name);
                                    removedFromClient = true; // Treat as success so DB record is cleaned up
                                }
                                else if (recordClient != null)
                                {
                                    removedFromClient = await RemoveFromClientAsync(recordClient, downloadRecord);
                                    downloadClientId = recordClient.Id;
                                }
                                else
                                {
                                    // Client no longer exists, just remove from database
                                    removedFromClient = true;
                                    _logger.LogWarning("Download client {ClientId} not found for download {DownloadId}, removing from database only", 
                                        downloadRecord.DownloadClientId, downloadId);
                                }
                            }
                        }
                        else
                        {
                            // Download not in database and invalid client ID provided
                            // This could be an external queue item with a bad client ID reference
                            // Try all enabled clients to find and remove it
                            _logger.LogWarning("Invalid client ID {ClientId} and download {DownloadId} not in database, trying all clients", 
                                downloadClientId, downloadId);
                            
                            var downloadClients = await _configurationService.GetDownloadClientConfigurationsAsync();
                            var enabledClients = downloadClients.Where(c => c.IsEnabled).ToList();

                            foreach (var tryClient in enabledClients)
                            {
                                removedFromClient = await RemoveFromClientAsync(tryClient, downloadRecord);
                                if (removedFromClient)
                                {
                                    downloadClientId = tryClient.Id;
                                    _logger.LogInformation("Successfully removed {DownloadId} from client {ClientName}", downloadId, tryClient.Name);
                                    break;
                                }
                            }

                            // If still not removed but not in any queue, consider it success
                            if (!removedFromClient)
                            {
                                _logger.LogInformation("Could not remove {DownloadId} from any client, verifying it's not in any queue", downloadId);
                                var currentQueue = await GetQueueAsync();
                                if (!currentQueue.Any(q => q.Id.Equals(downloadId, StringComparison.OrdinalIgnoreCase)))
                                {
                                    _logger.LogInformation("Download {DownloadId} not found in any queue, treating as successfully removed", downloadId);
                                    removedFromClient = true;
                                }
                            }
                        }
                    }
                }

                // If successfully removed from client (or force=true), also remove from database
                if (removedFromClient && downloadRecord != null)
                {
                    // Use a factory-created DbContext instead of resolving a scoped instance from a new scope.
                    var scopedDbContext = await _dbContextFactory.CreateDbContextAsync();

                    // Re-attach the entity if needed
                    var trackedDownload = await scopedDbContext.Downloads.FindAsync(downloadRecord.Id);
                    if (trackedDownload != null)
                    {
                        scopedDbContext.Downloads.Remove(trackedDownload);
                        await scopedDbContext.SaveChangesAsync();

                        _logger.LogInformation("Removed download record from database: {DownloadId} (Title: {Title})",
                            trackedDownload.Id, trackedDownload.Title);
                    }
                }

                return removedFromClient;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error removing from queue: {DownloadId}", downloadId);
                return false;
            }
        }

        private async Task<List<QueueItem>> GetQBittorrentQueueAsync(DownloadClientConfiguration client)
        {
            var baseUrl = DownloadClientUriBuilder.BuildAuthority(client);
            var items = new List<QueueItem>();

            try
            {
                // Use local HttpClient with CookieContainer so login session is preserved
                // Note: qBittorrent requires cookies for session management (SID cookie)
                // so we create a custom HttpClient instance with CookieContainer
                var cookieJar = new CookieContainer();
                var handler = new HttpClientHandler
                {
                    CookieContainer = cookieJar,
                    UseCookies = true,
                    AutomaticDecompression = DecompressionMethods.All
                };

                string torrentsJson;
                using (var httpClient = new HttpClient(handler))
                {
                    // Try to login first
                    using var loginData = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("username", client.Username ?? string.Empty),
                        new KeyValuePair<string, string>("password", client.Password ?? string.Empty)
                    });

                    var loginResponse = await httpClient.PostAsync($"{baseUrl}/api/v2/auth/login", loginData);

                    // Check if authentication is disabled (403 Forbidden) or login succeeded
                    if (loginResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        // Test if API is accessible without authentication to distinguish between
                        // "auth disabled" vs "wrong credentials"
                        var testResponse = await httpClient.GetAsync($"{baseUrl}/api/v2/app/version");
                        if (testResponse.IsSuccessStatusCode)
                        {
                            _logger.LogInformation("qBittorrent authentication appears to be disabled (403 Forbidden on login, but API accessible without auth)");
                        }
                        else
                        {
                            _logger.LogWarning("qBittorrent login failed with 403 Forbidden and API is not accessible without authentication - credentials may be incorrect");
                            return items;
                        }
                    }
                    else if (!loginResponse.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("qBittorrent login failed with status {Status}, cannot retrieve queue", loginResponse.StatusCode);
                        return items;
                    }

                    // Get torrents (with or without authentication)
                    var categoryFilter3 = QBittorrentHelpers.BuildCategoryParameter(client.Settings, "?");
                    var torrentsResponse = await httpClient.GetAsync($"{baseUrl}/api/v2/torrents/info{categoryFilter3}");
                    if (!torrentsResponse.IsSuccessStatusCode) return items;

                    torrentsJson = await torrentsResponse.Content.ReadAsStringAsync();
                }

                if (string.IsNullOrWhiteSpace(torrentsJson))
                {
                    _logger.LogWarning("qBittorrent returned empty torrents/info response for client {ClientName} ({ClientId})", client.Name, client.Id);
                    return items;
                }

                var torrents = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(torrentsJson);

                if (torrents != null)
                {
                    foreach (var torrent in torrents)
                    {
                        var name = torrent.TryGetValue("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                        var progress = torrent.TryGetValue("progress", out var progressEl) ? progressEl.GetDouble() * 100 : 0;
                        var size = torrent.TryGetValue("size", out var sizeEl) ? sizeEl.GetInt64() : 0;
                        var downloaded = torrent.TryGetValue("downloaded", out var downloadedEl) ? downloadedEl.GetInt64() : 0;
                        var dlspeed = torrent.TryGetValue("dlspeed", out var dlspeedEl) ? dlspeedEl.GetDouble() : 0;
                        var eta = torrent.TryGetValue("eta", out var etaEl) ? (int?)etaEl.GetInt32() : null;
                        var state = torrent.TryGetValue("state", out var stateEl) ? stateEl.GetString() ?? "unknown" : "unknown";
                        var hash = torrent.TryGetValue("hash", out var hashEl) ? hashEl.GetString() ?? "" : "";
                        var addedOn = torrent.TryGetValue("added_on", out var addedOnEl) ? addedOnEl.GetInt64() : 0;
                        var numSeeds = torrent.TryGetValue("num_seeds", out var numSeedsEl) ? (int?)numSeedsEl.GetInt32() : null;
                        var numLeechs = torrent.TryGetValue("num_leechs", out var numLeechsEl) ? (int?)numLeechsEl.GetInt32() : null;
                        var ratio = torrent.TryGetValue("ratio", out var ratioEl) ? (double?)ratioEl.GetDouble() : null;
                        var savePath = torrent.TryGetValue("save_path", out var savePathEl) ? savePathEl.GetString() ?? "" : "";
                        var contentPath = torrent.TryGetValue("content_path", out var contentPathEl) ? contentPathEl.GetString() ?? "" : "";

                        // Apply remote path mapping for Docker scenarios
                        var localPath = !string.IsNullOrEmpty(savePath)
                            ? await _pathMappingService.TranslatePathAsync(client.Id, savePath)
                            : savePath;

                        // Also map the content path (the actual file/folder path)
                        var localContentPath = !string.IsNullOrEmpty(contentPath)
                            ? await _pathMappingService.TranslatePathAsync(client.Id, contentPath)
                            : contentPath;

                        // If qBittorrent doesn't return content_path, fall back to save path + torrent name
                        // to avoid scanning the entire download root.
                        if (string.IsNullOrWhiteSpace(localContentPath))
                        {
                            if (!string.IsNullOrWhiteSpace(localPath) && !string.IsNullOrWhiteSpace(name))
                            {
                                var normalizedName = name.Trim();
                                if (Path.IsPathRooted(normalizedName))
                                {
                                    localContentPath = normalizedName;
                                }
                                else
                                {
                                    var relativeName = normalizedName.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                                    localContentPath = Path.IsPathRooted(relativeName)
                                        ? relativeName
                                        : localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                            + Path.DirectorySeparatorChar
                                            + relativeName;
                                }
                            }
                            else if (!string.IsNullOrWhiteSpace(localPath))
                            {
                                localContentPath = localPath;
                            }
                        }

                        // Map qBittorrent states to unified status
                        // Note: qBittorrent doesn't have explicit "completed" states
                        // Completion is determined by progress >= 100% + uploading/seeding state
                        var status = state switch
                        {
                            // Active downloading states
                            "downloading" => "downloading",
                            "metaDL" => "downloading",              // downloading metadata
                            "forcedDL" => "downloading",            // forced downloading
                            "forcedMetaDL" => "downloading",        // forced metadata downloading
                            "stalledDL" => "downloading",           // stalled downloading
                            "checkingDL" => "downloading",          // checking downloading

                            // Paused/Stopped states
                            "stoppedDL" => "paused",                // paused downloading (was "pausedDL")
                            "stoppedUP" => "paused",                // paused uploading

                            // Queued states  
                            "queuedDL" => "queued",                 // queued downloading
                            "queuedUP" => "queued",                 // queued uploading

                            // Seeding/Uploading states
                            "uploading" => "seeding",               // actively uploading
                            "stalledUP" => "seeding",               // stalled uploading
                            "checkingUP" => "seeding",              // checking uploading
                            "forcedUP" => "seeding",                // forced uploading

                            // Processing states
                            "checkingResumeData" => "downloading",  // checking resume data
                            "moving" => "downloading",              // moving files

                            // Error states
                            "error" => "failed",
                            "missingFiles" => "failed",

                            _ => "unknown"
                        };

                        // Determine completion: any torrent at 100% progress is complete
                        // regardless of whether it's seeding, paused, or in any other state
                        if (progress >= 100.0)
                        {
                            status = "completed";
                        }

                        items.Add(new QueueItem
                        {
                            Id = hash,
                            Title = name,
                            Quality = "Unknown",
                            Status = status,
                            Progress = progress,
                            Size = size,
                            Downloaded = downloaded,
                            DownloadSpeed = dlspeed,
                            Eta = eta >= 8640000 ? null : eta, // Filter out invalid ETAs
                            DownloadClient = client.Name,
                            DownloadClientId = client.Id,
                            DownloadClientType = "qbittorrent",
                            AddedAt = DateTimeOffset.FromUnixTimeSeconds(addedOn).DateTime,
                            Seeders = numSeeds,
                            Leechers = numLeechs,
                            Ratio = ratio,
                            CanPause = status == "downloading" || status == "queued",
                            CanRemove = true,
                            RemotePath = savePath,
                            LocalPath = localPath,
                            ContentPath = localContentPath
                        });
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogWarning(ex, "Error getting qBittorrent queue - client may be unreachable");
            }

            return items;
        }

        /// <summary>
        /// Get qBittorrent queue using efficient sync API (incremental updates)
        /// This implementation currently falls back to the full fetch logic while
        /// the incremental sync refactor is completed. Keeping a dedicated method
        /// preserves the intended structure and allows future optimization.
        /// </summary>
        /// <param name="client">Download client configuration</param>
        private async Task<List<QueueItem>> GetQBittorrentQueueSyncAsync(DownloadClientConfiguration client)
        {
            try
            {
                // Temporary fallback: call the full fetch implementation.
                // The original incremental sync implementation will be reinstated
                // or replaced with a more maintainable version in a subsequent change.
                return await GetQBittorrentQueueAsync(client);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogWarning(ex, "Incremental qBittorrent sync failed, falling back to full fetch");
                try
                {
                    return await GetQBittorrentQueueAsync(client);
                }
                catch (Exception inner) when (inner is not OperationCanceledException && inner is not OutOfMemoryException && inner is not StackOverflowException) {
                    _logger.LogWarning(inner, "Fallback full fetch also failed for qBittorrent client {ClientName}", client.Name);
                    return new List<QueueItem>();
                }
            }
        }

        //
        // Helper stubs added to satisfy callers while refactor completes.
        // These are conservative, safe no-op / simple implementations.
        //

        private async Task<string> DownloadDirectlyAsync(SearchResult searchResult, int? audiobookId)
        {
            // Create a Download record in the database so it's tracked like other downloads.
            try
            {
                var downloadClient = new DownloadClientConfiguration
                {
                    Id = "DDL"
                };
                var download = new Download(searchResult, downloadClient)
                {
                    AudiobookId = audiobookId
                };
                download.Metadata["Protocol"] = DownloadProtocol.DirectDownload;

                var ctx = await _dbContextFactory.CreateDbContextAsync();
                ctx.Downloads.Add(download);
                await ctx.SaveChangesAsync();
                return download.Id;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogWarning(ex, "DownloadDirectlyAsync: failed to create DDL download record");
                return Guid.NewGuid().ToString();
            }
        }

        private async Task LogDownloadHistory(Audiobook audiobook, string source, SearchResult result)
        {
            // Placeholder: log to internal logger for visibility; actual history persistence is elsewhere
            try
            {
                _logger.LogInformation("LogDownloadHistory: audiobook={Title}, source={Source}, result={ResultTitle}", audiobook?.Title, source, result?.Title);
            }
            catch (Exception caughtEx_13) when (caughtEx_13 is not OperationCanceledException && caughtEx_13 is not OutOfMemoryException && caughtEx_13 is not StackOverflowException) { 
                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }
            await Task.CompletedTask;
        }

        private static string? TryResolveClientSpecificIdFallback(DownloadClientConfiguration client, SearchResult searchResult)
        {
            if (client == null || searchResult == null || !IsTorrentResult(searchResult))
            {
                return null;
            }

            var magnetHash = TryExtractMagnetHash(searchResult.MagnetLink);
            if (!string.IsNullOrWhiteSpace(magnetHash))
            {
                return magnetHash;
            }

            return null;
        }

        private static string? TryExtractMagnetHash(string? magnetLink)
        {
            if (string.IsNullOrWhiteSpace(magnetLink))
            {
                return null;
            }

            var match = Regex.Match(magnetLink, @"xt=urn:btih:([^&]+)", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return null;
            }

            var rawHash = Uri.UnescapeDataString(match.Groups[1].Value).Trim();
            return string.IsNullOrWhiteSpace(rawHash) ? null : rawHash;
        }

        private Download? FindBestMatchingDownload(
            QueueItem queueItem,
            DownloadClientConfiguration client,
            IEnumerable<Download> candidateDownloads)
        {
            if (queueItem == null || client == null || candidateDownloads == null)
            {
                return null;
            }

            var matches = candidateDownloads
                .Where(download => download.DownloadClientId == client.Id)
                .Select(download => new
                {
                    Download = download,
                    Score = GetQueueMatchScore(download, queueItem)
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Download.StartedAt)
                .ToList();

            if (matches.Count == 0)
            {
                return null;
            }

            var bestMatch = matches[0];
            if (bestMatch.Score == 1 && matches.Skip(1).Any(x => x.Score == bestMatch.Score))
            {
                _logger.LogDebug(
                    "Queue item {QueueId} '{QueueTitle}' had ambiguous title-only matches on client {ClientId}; leaving unmatched",
                    queueItem.Id,
                    queueItem.Title,
                    client.Id);
                return null;
            }

            if (bestMatch.Score >= 3)
            {
                _logger.LogDebug(
                    "Matched download {DownloadId} to queue item {QueueId} via client identifier",
                    bestMatch.Download.Id,
                    queueItem.Id);
            }
            else if (bestMatch.Score > 0)
            {
                _logger.LogDebug(
                    "Matched download {DownloadId} to queue item {QueueId} via title reconciliation: '{DownloadTitle}' <-> '{QueueTitle}'",
                    bestMatch.Download.Id,
                    queueItem.Id,
                    bestMatch.Download.Title,
                    queueItem.Title);
            }

            return bestMatch.Download;
        }

        private int GetQueueMatchScore(Download download, QueueItem queueItem)
        {
            if (download == null || queueItem == null)
            {
                return 0;
            }

            if (!string.IsNullOrWhiteSpace(download.Id) &&
                !string.IsNullOrWhiteSpace(queueItem.Id) &&
                string.Equals(download.Id, queueItem.Id, StringComparison.OrdinalIgnoreCase))
            {
                return 4;
            }

            var clientDownloadId = GetMetadataString(download.Metadata, "ClientDownloadId");
            if (!string.IsNullOrWhiteSpace(clientDownloadId) &&
                string.Equals(clientDownloadId, queueItem.Id, StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }

            var torrentHash = GetMetadataString(download.Metadata, "TorrentHash");
            if (!string.IsNullOrWhiteSpace(torrentHash) &&
                string.Equals(torrentHash, queueItem.Id, StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }

            if (TitlesExactlyMatch(download.Title, queueItem.Title))
            {
                return 2;
            }

            if (IsMatchingTitle(download.Title, queueItem.Title))
            {
                if (QueueTitleContainsArtist(download, queueItem))
                {
                    return 2;
                }

                if (IsStrongStandaloneTitleMatch(download.Title, queueItem.Title))
                {
                    return 1;
                }
            }

            return 0;
        }

        private bool TitlesExactlyMatch(string titleA, string titleB)
        {
            var normalizedA = NormalizeTitle(titleA);
            var normalizedB = NormalizeTitle(titleB);

            return !string.IsNullOrWhiteSpace(normalizedA) &&
                   string.Equals(normalizedA, normalizedB, StringComparison.OrdinalIgnoreCase);
        }

        private bool QueueTitleContainsArtist(Download download, QueueItem queueItem)
        {
            var normalizedArtist = NormalizeTitle(download?.Artist ?? string.Empty);
            if (string.IsNullOrWhiteSpace(normalizedArtist) || normalizedArtist.Length < 4)
            {
                return false;
            }

            var normalizedQueueTitle = NormalizeTitle(queueItem?.Title ?? string.Empty);
            return normalizedQueueTitle.Contains(normalizedArtist, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsStrongStandaloneTitleMatch(string titleA, string titleB)
        {
            var normalizedA = NormalizeTitle(titleA);
            var normalizedB = NormalizeTitle(titleB);
            if (string.IsNullOrWhiteSpace(normalizedA) || string.IsNullOrWhiteSpace(normalizedB))
            {
                return false;
            }

            var shorter = normalizedA.Length <= normalizedB.Length ? normalizedA : normalizedB;
            var longer = normalizedA.Length <= normalizedB.Length ? normalizedB : normalizedA;
            var shorterTokenCount = shorter.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

            if (shorter.Length >= 15 || shorterTokenCount >= 3)
            {
                return true;
            }

            if (shorter.Length >= 10 &&
                (longer.StartsWith(shorter + " ", StringComparison.OrdinalIgnoreCase) ||
                 longer.EndsWith(" " + shorter, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return false;
        }

        private bool IsMatchingTitle(string titleA, string titleB)
        {
            try
            {
                return AreTitlesSimilar(titleA ?? string.Empty, titleB ?? string.Empty);
            }
            catch (Exception caughtEx_14) when (caughtEx_14 is not OperationCanceledException && caughtEx_14 is not OutOfMemoryException && caughtEx_14 is not StackOverflowException) {
                return false;
            }
        }

        private bool AreTitlesSimilar(string a, string b)
        {
            try
            {
                var An = NormalizeTitle(a);
                var Bn = NormalizeTitle(b);

                if (string.IsNullOrWhiteSpace(An) || string.IsNullOrWhiteSpace(Bn))
                {
                    return false;
                }

                if (An == Bn) return true;

                var shorter = An.Length <= Bn.Length ? An : Bn;
                var longer = An.Length <= Bn.Length ? Bn : An;
                var shorterTokens = shorter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var longerTokens = longer.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (shorterTokens.Length == 0 || longerTokens.Length == 0)
                {
                    return false;
                }

                if (shorterTokens.Length == 1)
                {
                    var token = shorterTokens[0];
                    return token.Length >= 6 &&
                           longerTokens.Contains(token, StringComparer.OrdinalIgnoreCase);
                }

                if (longer.Contains(shorter, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return TokensAppearInOrder(shorterTokens, longerTokens);
            }
            catch (Exception caughtEx_15) when (caughtEx_15 is not OperationCanceledException && caughtEx_15 is not OutOfMemoryException && caughtEx_15 is not StackOverflowException) { return false; }
        }

        private string NormalizeTitle(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var lower = s.ToLowerInvariant();
            lower = Regex.Replace(lower, @"[\[\]\(\)\{\}_\.-]+", " ");
            lower = Regex.Replace(lower, @"\b\d{2,4}\s*kbps\b", " ");
            lower = Regex.Replace(lower, @"\b(mp3|m4a|m4b|flac|aac|ogg|opus|audiobook|unabridged|abridged)\b", " ");
            lower = Regex.Replace(lower, @"\b(19|20)\d{2}\b", " ");
            var cleaned = new string(lower.Where(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch)).ToArray());
            return string.Join(' ', cleaned.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static bool TokensAppearInOrder(string[] shorterTokens, string[] longerTokens)
        {
            if (shorterTokens == null || longerTokens == null || shorterTokens.Length == 0 || longerTokens.Length == 0)
            {
                return false;
            }

            var longerIndex = 0;
            foreach (var token in shorterTokens)
            {
                var found = false;
                while (longerIndex < longerTokens.Length)
                {
                    if (string.Equals(token, longerTokens[longerIndex], StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        longerIndex++;
                        break;
                    }

                    longerIndex++;
                }

                if (!found)
                {
                    return false;
                }
            }

            return true;
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

        // Standard Levenshtein distance implementation (copied from SearchService for local use)
        private static int LevenshteinDistance(string s, string t)
        {
            if (s == t) return 0;
            if (string.IsNullOrEmpty(s)) return t.Length;
            if (string.IsNullOrEmpty(t)) return s.Length;

            var n = s.Length;
            var m = t.Length;
            var d = new int[n + 1, m + 1];

            for (int i = 0; i <= n; d[i, 0] = i++) { }
            for (int j = 0; j <= m; d[0, j] = j++) { }

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }
            return d[n, m];
        }

        private async Task<bool> RemoveFromClientAsync(DownloadClientConfiguration client, Download download)
        {
            try
            {
                if (client == null) return false;

                if (_clientGateway != null)
                {
                    try
                    {
                        var removed = await _clientGateway.RemoveAsync(client, download, false);
                        if (removed)
                        {
                            _logger.LogInformation("Successfully removed {DownloadId} from client {ClientName}", download.Id, client.Name ?? client.Id);
                            return true;
                        }

                        // If removal returned false, verify if the item is still in the client's queue
                        // If it's not in the queue, consider removal successful (item already gone)
                        _logger.LogWarning("Client reported removal failed for {DownloadId}, checking if item still exists in queue", download.Id);
                        try
                        {
                            var queue = await _clientGateway.GetQueueAsync(client);
                            var stillExists = queue.Any(q => q.Id.Equals(download.Id, StringComparison.OrdinalIgnoreCase));
                            
                            if (!stillExists)
                            {
                                _logger.LogInformation("Item {DownloadId} no longer in {ClientName} queue, treating removal as successful", download.Id, client.Name ?? client.Id);
                                return true;
                            }
                            
                            _logger.LogWarning("Item {DownloadId} still exists in {ClientName} queue after removal attempt", download.Id, client.Name ?? client.Id);
                            return false;
                        }
                        catch (Exception queueEx) when (queueEx is not OperationCanceledException && queueEx is not OutOfMemoryException && queueEx is not StackOverflowException) {
                            _logger.LogWarning(queueEx, "Failed to verify queue status for {DownloadId} on {ClientName}, assuming removal failed", download.Id, client.Name ?? client.Id);
                            return false;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                        _logger.LogWarning(ex, "RemoveFromClientAsync: Exception removing {DownloadId} from {Client}: {Message}", 
                            LogRedaction.SanitizeText(download.Id), LogRedaction.SanitizeText(client.Name ?? client.Id), ex.Message);
                        
                        // Check if item still exists in queue - if not, consider removal successful
                        try
                        {
                            var queue = await _clientGateway.GetQueueAsync(client);
                            var stillExists = queue.Any(q => q.Id.Equals(download.Id, StringComparison.OrdinalIgnoreCase));
                            
                            if (!stillExists)
                            {
                                _logger.LogInformation("After exception, item {DownloadId} not found in {ClientName} queue, treating as successfully removed", 
                                    download.Id, client.Name ?? client.Id);
                                return true;
                            }
                        }
                        catch (Exception queueEx) when (queueEx is not OperationCanceledException && queueEx is not OutOfMemoryException && queueEx is not StackOverflowException) {
                            _logger.LogDebug(queueEx, "Failed to verify queue after exception for {DownloadId}", download.Id);
                        }
                        
                        return false;
                    }
                }

                // Fallback conservative behavior when no gateway is available
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogWarning(ex, "RemoveFromClientAsync fallback failed for client {Client}", client.Name ?? client.Id);
                return false;
            }
        }

        private Task<List<QueueItem>> GetQueueFallbackAsync(DownloadClientConfiguration client)
        {
            if (client == null || string.IsNullOrWhiteSpace(client.Type))
            {
                return Task.FromResult(new List<QueueItem>());
            }

            switch (client.Type.ToLowerInvariant())
            {
                case "qbittorrent":
                    return GetQBittorrentQueueSyncAsync(client);
                case "transmission":
                    return GetTransmissionQueueOptimizedAsync(client);
                case "sabnzbd":
                    return GetSABnzbdQueueOptimizedAsync(client);
                case "nzbget":
                    return GetNZBGetQueueOptimizedAsync(client);
                default:
                    return Task.FromResult(new List<QueueItem>());
            }
        }

        private Task<List<QueueItem>> GetTransmissionQueueOptimizedAsync(DownloadClientConfiguration client)
        {
            if (_clientGateway != null)
            {
                return _clientGateway.GetQueueAsync(client);
            }

            return Task.FromResult(new List<QueueItem>());
        }

        private Task<List<QueueItem>> GetSABnzbdQueueOptimizedAsync(DownloadClientConfiguration client)
        {
            if (_clientGateway != null)
            {
                return _clientGateway.GetQueueAsync(client);
            }
            return Task.FromResult(new List<QueueItem>());
        }

        private Task<List<QueueItem>> GetNZBGetQueueOptimizedAsync(DownloadClientConfiguration client)
        {
            if (_clientGateway != null)
            {
                return _clientGateway.GetQueueAsync(client);
            }
            return Task.FromResult(new List<QueueItem>());
        }

        // Temp files cleanup method required by TempFileCleanupService
        public void CleanupOldTempFiles()
        {
            // Conservative no-op implementation. Real cleanup lives elsewhere.
            try
            {
                _logger.LogDebug("CleanupOldTempFiles called (noop)");
            }
            catch (Exception caughtEx_16) when (caughtEx_16 is not OperationCanceledException && caughtEx_16 is not OutOfMemoryException && caughtEx_16 is not StackOverflowException) { 
                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }
        }

        // Overload used by TempFileCleanupService to specify retention window in hours
        public void CleanupOldTempFiles(int hours)
        {
            // Conservative no-op implementation that accepts an hours parameter.
            // Real cleanup logic should delete temp files older than 'hours'.
            try
            {
                _logger.LogDebug("CleanupOldTempFiles called with hours={Hours} (noop)", hours);
            }
            catch (Exception caughtEx_17) when (caughtEx_17 is not OperationCanceledException && caughtEx_17 is not OutOfMemoryException && caughtEx_17 is not StackOverflowException) { 
                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }
        }
    }
}
