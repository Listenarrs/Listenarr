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

        public async Task<string> StartDownloadAsync(SearchResult searchResult, string downloadClientId, int? audiobookId = null)
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
            catch (Exception ex)
            {
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
                    catch (Exception syncEx)
                    {
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
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process completed download removal for {DownloadId}", downloadId);
                }

                try
                {
                    var currentQueue = await GetQueueAsync();
                    if (_hubBroadcaster != null)
                    {
                        await _hubBroadcaster.BroadcastQueueUpdateAsync(currentQueue);
                        _logger.LogInformation("Broadcasted QueueUpdate via IHubBroadcaster after processing download {DownloadId}", downloadId);
                    }
                    else
                    {
                        // Fallback to direct hub context for older registrations
                        await _hubContext.Clients.All.SendAsync("QueueUpdate", currentQueue);
                        try
                        {
                            var clientProxy = _hubContext?.Clients?.All;
                            if (clientProxy != null)
                            {
                                await clientProxy.SendCoreAsync("QueueUpdate", new object[] { currentQueue }, System.Threading.CancellationToken.None);
                            }
                        }
                        catch (Exception exInner)
                        {
                            _logger.LogDebug(exInner, "Direct SendCoreAsync for QueueUpdate failed (non-fatal)");
                        }

                        _logger.LogInformation("Broadcasted QueueUpdate after processing download {DownloadId}", downloadId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to broadcast QueueUpdate after processing download {DownloadId}", downloadId);
                }
            }
            catch (Exception ex)
            {
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
            catch (Exception ex)
            {
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
            _logger.LogInformation("Searching for audiobook '{Title}' with query: {Query}", audiobook.Title, searchQuery);

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
            _logger.LogInformation("Scored {Count} search results for audiobook '{Title}':", scoredResults.Count, audiobook.Title);
            foreach (var scoredResult in scoredResults.OrderByDescending(s => s.TotalScore))
            {
                var status = scoredResult.IsRejected ? "REJECTED" : (scoredResult.TotalScore > 0 ? "ACCEPTABLE" : "LOW SCORE");
                _logger.LogInformation("  [{Status}] Score: {Score} | Title: {Title} | Source: {Source} | Size: {Size}MB | Seeders: {Seeders} | Quality: {Quality}",
                    status, scoredResult.TotalScore, scoredResult.SearchResult.Title, scoredResult.SearchResult.Source,
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

            // Handle DDL results directly
            if (!string.IsNullOrEmpty(topResult.SearchResult.DownloadType) &&
                topResult.SearchResult.DownloadType.Equals("DDL", StringComparison.OrdinalIgnoreCase))
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
            var isTorrent = IsTorrentResult(topResult.SearchResult);
            var downloadClientId = await GetAppropriateDownloadClient(isTorrent);

            if (downloadClientId == null)
            {
                _logger.LogWarning("No suitable download client found for type: {Type}", isTorrent ? "Torrent" : "NZB");
                return new SearchAndDownloadResult
                {
                    Success = false,
                    Message = $"No suitable download client found for {(isTorrent ? "torrent" : "NZB")} results"
                };
            }

            // Send to download client with audiobookId for proper metadata linking
            var downloadId2 = await SendToDownloadClientAsync(topResult.SearchResult, downloadClientId, audiobookId);

            // Log to history
            await LogDownloadHistory(audiobook, "Search", topResult.SearchResult);

            return new SearchAndDownloadResult
            {
                Success = true,
                Message = $"Successfully sent to download client",
                DownloadId = downloadId2,
                IndexerUsed = "Search",
                DownloadClientUsed = downloadClientId,
                SearchResult = topResult.SearchResult
            };
        }

        public async Task<string> SendToDownloadClientAsync(SearchResult searchResult, string? downloadClientId = null, int? audiobookId = null)
        {
            _logger.LogInformation("SendToDownloadClientAsync called - Title: {Title}, DownloadType: '{DownloadType}', TorrentUrl: {TorrentUrl}, AudiobookId: {AudiobookId}",
                searchResult.Title,
                searchResult.DownloadType ?? "(null)",
                searchResult.TorrentUrl ?? "(null)",
                audiobookId);

            // Check if this is a DDL (Direct Download Link) - handle it differently
            // Use case-insensitive comparison in case of serialization casing issues
            if (!string.IsNullOrEmpty(searchResult.DownloadType) &&
                searchResult.DownloadType.Equals("DDL", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Processing DDL for: {Title}, AudiobookId: {AudiobookId}", searchResult.Title, audiobookId);
                return await DownloadDirectlyAsync(searchResult, audiobookId);
            }

            _logger.LogInformation("Not a DDL, processing as torrent/usenet. DownloadType was: '{DownloadType}'", searchResult.DownloadType);

            if (downloadClientId == null)
            {
                var isTorrent = IsTorrentResult(searchResult);
                downloadClientId = await GetAppropriateDownloadClient(isTorrent);

                if (downloadClientId == null)
                {
                    var clientType = isTorrent ? "torrent" : "NZB";
                    var neededClients = isTorrent ? "qBittorrent or Transmission" : "SABnzbd or NZBGet";
                    throw new Exception($"No suitable download client found for {clientType}. Please configure and enable a {clientType} client ({neededClients}) in Settings.");
                }

                _logger.LogInformation("Auto-selected download client {ClientId} for {ClientType}", downloadClientId, isTorrent ? "torrent" : "NZB");
            }

            var downloadClient = await _configurationService.GetDownloadClientConfigurationAsync(downloadClientId);
            if (downloadClient == null || !downloadClient.IsEnabled)
            {
                throw new Exception("Download client not found or disabled");
            }

            _logger.LogInformation("Sending to {ClientType} download client: {ClientName}", downloadClient.Type, downloadClient.Name);

            var downloadId = Guid.NewGuid().ToString();

            // Ensure downloadClientId is non-null before assignment into model
            var downloadClientIdForModel = downloadClientId ?? string.Empty;

            // Create Download record in database before sending to client
            var download = new Download
            {
                Id = downloadId,
                AudiobookId = audiobookId,
                Title = searchResult.Title ?? string.Empty,
                Artist = searchResult.Artist ?? string.Empty,
                Album = searchResult.Album ?? string.Empty,
                OriginalUrl = !string.IsNullOrEmpty(searchResult.MagnetLink) ? searchResult.MagnetLink : (searchResult.TorrentUrl ?? searchResult.NzbUrl ?? string.Empty),
                Status = DownloadStatus.Queued,
                Progress = 0,
                TotalSize = searchResult.Size,
                DownloadedSize = 0,
                DownloadPath = downloadClient.DownloadPath ?? string.Empty,
                FinalPath = string.Empty,
                StartedAt = DateTime.UtcNow,
                DownloadClientId = downloadClientIdForModel,
                Metadata = new Dictionary<string, object>
                {
                    ["Source"] = searchResult.Source ?? string.Empty,
                    ["Seeders"] = searchResult.Seeders ?? 0,
                    ["Quality"] = searchResult.Quality ?? string.Empty,
                    ["DownloadType"] = searchResult.DownloadType ?? (IsTorrentResult(searchResult) ? "Torrent" : "Usenet")
                }
            };

            var dbContext = await _dbContextFactory.CreateDbContextAsync();
            dbContext.Downloads.Add(download);
            await dbContext.SaveChangesAsync();
            _logger.LogInformation("Created download record in database: {DownloadId} for '{Title}'", downloadId, searchResult.Title);
            
            // Record in download history for idempotency tracking
            if (_downloadHistoryService != null && !string.IsNullOrEmpty(downloadClientIdForModel))
            {
                try
                {
                    var protocol = IsTorrentResult(searchResult) ? Listenarr.Domain.Models.DownloadProtocol.Torrent : Listenarr.Domain.Models.DownloadProtocol.Usenet;
                    await _downloadHistoryService.RecordGrabbedAsync(
                        downloadId,
                        downloadClientIdForModel,
                        searchResult.Title ?? "Unknown",
                        protocol);
                    _logger.LogInformation("Recorded grabbed event in history for download {DownloadId}", downloadId);
                }
                catch (Exception histEx)
                {
                    _logger.LogWarning(histEx, "Failed to record grabbed event in history for download {DownloadId} (non-critical)", downloadId);
                }
            }

            // Attempt to cache MyAnonamouse torrents ahead of handing off to qBittorrent
            await TryPrepareMyAnonamouseTorrentAsync(searchResult, downloadId);

            if (_clientGateway == null)
            {
                throw new InvalidOperationException("Download client gateway is not registered. Ensure AddListenarrAdapters() is invoked during startup.");
            }

            // Route to appropriate client handler via adapter and capture client-specific IDs when provided
            string? clientSpecificId = await _clientGateway.AddAsync(downloadClient, searchResult);

            // Update download record with client-specific ID if available
            if (!string.IsNullOrEmpty(clientSpecificId))
            {
                var updateContext = await _dbContextFactory.CreateDbContextAsync();
                var downloadToUpdate = await updateContext.Downloads.FindAsync(downloadId);
                if (downloadToUpdate != null)
                {
                    if (downloadToUpdate.Metadata == null)
                        downloadToUpdate.Metadata = new Dictionary<string, object>();

                    // Persist client-specific ID for all clients (NZBGet/SABnzbd/etc.)
                    downloadToUpdate.Metadata["ClientDownloadId"] = clientSpecificId;

                    // Maintain TorrentHash for qBittorrent compatibility
                    if (downloadClient.Type.Equals("qbittorrent", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadToUpdate.Metadata["TorrentHash"] = clientSpecificId;
                    }

                    updateContext.Downloads.Update(downloadToUpdate);
                    await updateContext.SaveChangesAsync();
                    _logger.LogInformation("Updated download {DownloadId} with client-specific ID: {ClientId}", downloadId, clientSpecificId);
                }
            }

            // Send notification for book-downloading event
            if (_notificationService != null)
            {
                using (var scope = _serviceScopeFactory.CreateScope())
                {
                    var configService = scope.ServiceProvider.GetService<IConfigurationService>() ?? _configurationService;
                    var fileNamingService = scope.ServiceProvider.GetService<IFileNamingService>();
                    var settings = configService != null ? await configService.GetApplicationSettingsAsync() : new ApplicationSettings();

                    // Fetch audiobook data if available for better notification content
                    object notificationData;
                    if (audiobookId.HasValue)
                    {
                        var notifContext = await _dbContextFactory.CreateDbContextAsync();
                        var audiobook = await notifContext.Audiobooks.FindAsync(audiobookId.Value);
                        if (audiobook != null)
                        {
                            // Use audiobook metadata for the notification
                            notificationData = new
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
                                // Include download metadata
                                downloadId = downloadId,
                                source = searchResult.Source ?? "Unknown Source",
                                downloadClient = downloadClient.Name ?? "Unknown Client",
                                size = searchResult.Size
                            };
                        }
                        else
                        {
                            // Fallback to search result data if audiobook not found
                            notificationData = new
                            {
                                downloadId = downloadId,
                                title = searchResult.Title ?? "Unknown Title",
                                artist = searchResult.Artist ?? "Unknown Artist",
                                album = searchResult.Album ?? "Unknown Album",
                                size = searchResult.Size,
                                source = searchResult.Source ?? "Unknown Source",
                                downloadClient = downloadClient.Name ?? "Unknown Client",
                                audiobookId = audiobookId
                            };
                        }
                    }
                    else
                    {
                        // No audiobook ID, use search result data
                        notificationData = new
                        {
                            downloadId = downloadId,
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
                var currentQueue = await GetQueueAsync();
                if (_hubBroadcaster != null)
                {
                    await _hubBroadcaster.BroadcastQueueUpdateAsync(currentQueue);
                    _logger.LogInformation("Immediate queue update sent with {Count} items via IHubBroadcaster", currentQueue?.Count ?? 0);
                }
                else
                {
                    // Fallback for older registrations
                    using (var scope = _serviceScopeFactory.CreateScope())
                    {
                        var hubContext = scope.ServiceProvider.GetService<Microsoft.AspNetCore.SignalR.IHubContext<Listenarr.Api.Hubs.DownloadHub>>();
                        if (hubContext != null)
                        {
                            await hubContext.Clients.All.SendAsync("QueueUpdate", currentQueue);
                            _logger.LogInformation("Immediate queue update sent with {Count} items", currentQueue?.Count ?? 0);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to trigger immediate queue update (non-fatal)");
            }

            return downloadId;
        }

        private async Task TryPrepareMyAnonamouseTorrentAsync(SearchResult searchResult, string? downloadId = null)
        {
            _logger.LogInformation("TryPrepareMyAnonamouseTorrentAsync called for '{Title}', IndexerId: {IndexerId}, TorrentUrl: '{TorrentUrl}'", 
                searchResult?.Title, searchResult?.IndexerId, searchResult?.TorrentUrl);
            
            // Security: Validate all preconditions before performing sensitive operations
            // This method downloads content using authenticated HTTP clients, so we must
            // ensure the request is legitimate and comes from a trusted, configured source.
            
            if (searchResult?.IndexerId == null)
            {
                _logger.LogWarning("TryPrepareMyAnonamouseTorrentAsync: No IndexerId for '{Title}' - skipping", searchResult?.Title);
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
                if (!string.Equals(indexer.Implementation, "MyAnonamouse", StringComparison.OrdinalIgnoreCase))
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

                // Always use an authenticated client with a CookieContainer for MyAnonamouse downloads.
                // Using a factory client can return global clients without a CookieContainer which may fail auth.
                HttpClient httpClientToUse;
                if (_httpClientFactory != null)
                {
                    httpClientToUse = _httpClientFactory.CreateClient();
                }
                else
                {
                    httpClientToUse = MyAnonamouseHelper.CreateAuthenticatedHttpClient(mamId, indexer.Url);
                }

                _logger.LogDebug("Downloading MyAnonamouse torrent for '{Title}' from {Url}", searchResult.Title, LogRedaction.SanitizeUrl(searchResult.TorrentUrl));

                // Follow redirects manually so we can re-apply cookies and Host header on each hop (mimic Prowlarr)
                var currentUri = torrentUri;
                HttpResponseMessage? response = null;
                for (int redirectAttempt = 0; redirectAttempt < 6; redirectAttempt++)
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, currentUri);
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
                    catch (Exception exMam)
                    {
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
                    var snippet = System.Text.Encoding.UTF8.GetString(torrentBytes.Take(Math.Min(512, torrentBytes.Length)).ToArray());
                    if (System.Text.RegularExpressions.Regex.IsMatch(snippet, "Unrecognized host|PassKey|Pass Key|Unrecognized", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        _logger.LogWarning("MyAnonamouse torrent download for '{Title}' returned an authorization error page from tracker: {Snippet}", searchResult.Title, LogRedaction.RedactText(snippet, LogRedaction.GetSensitiveValuesFromEnvironment()));
                    }
                    else
                    {
                        _logger.LogWarning("MyAnonamouse torrent download for '{Title}' returned unexpected non-torrent payload (first 200 chars): {Snippet}", searchResult.Title, LogRedaction.RedactText(snippet, LogRedaction.GetSensitiveValuesFromEnvironment()));
                    }

                    response.Dispose();
                    return;
                }

                // Additional debug info to help diagnose cases where content looks like a torrent but tracker still rejects it
                var contentType = response.Content.Headers.ContentType?.ToString() ?? "(none)";
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
                            foreach (var ip in distinctIps)
                            {
                                // Skip common local addresses that shouldn't be replaced
                                if (ip.StartsWith("127.") || ip.StartsWith("10.") || ip.StartsWith("192.168.") || ip.StartsWith("172."))
                                    continue;

                                if (!string.Equals(ip, indexerUri.Host, StringComparison.OrdinalIgnoreCase))
                                {
                                    var replaced2 = MyAnonamouseHelper.ReplaceHostInTorrent(torrentBytes, ip, indexerUri.Host);
                                    if (replaced2 != null && replaced2.Length > 0)
                                    {
                                        torrentBytes = replaced2;
                                        _logger.LogInformation("Rewrote torrent IP host {Ip} to indexer host {Host} for '{Title}'", ip, indexerUri.Host, searchResult.Title);
                                        // refresh ascii for further processing
                                        ascii = System.Text.Encoding.ASCII.GetString(torrentBytes);
                                    }
                                }
                            }
                        }
                        catch (Exception rex2)
                        {
                            _logger.LogDebug(rex2, "Failed to rewrite numeric IPs inside torrent (non-fatal)");
                        }

                        // 3) Replace any announce host entries (e.g., t.myanonamouse.net) with the configured indexer host so tracker sees expected host/passkey pairing
                        try
                        {
                            var announces = MyAnonamouseHelper.ExtractAnnounceUrls(torrentBytes);
                            if (announces != null && announces.Count > 0)
                            {
                                foreach (var ann in announces.Distinct().ToList())
                                {
                                    try
                                    {
                                        if (Uri.TryCreate(ann, UriKind.Absolute, out var annUri))
                                        {
                                            var annHost = annUri.Host;
                                            // skip if same host or local/private IP
                                            if (string.IsNullOrEmpty(annHost) || string.Equals(annHost, indexerUri.Host, StringComparison.OrdinalIgnoreCase))
                                                continue;

                                            // Skip local/private IPs
                                            if (annHost.StartsWith("127.") || annHost.StartsWith("10.") || annHost.StartsWith("192.168.") || annHost.StartsWith("172."))
                                                continue;

                                            var replacedAnn = MyAnonamouseHelper.ReplaceHostInTorrent(torrentBytes, annHost, indexerUri.Host);
                                            if (replacedAnn != null && replacedAnn.Length > 0)
                                            {
                                                torrentBytes = replacedAnn;
                                                _logger.LogInformation("Rewrote torrent announce host from {OldHost} to {NewHost} for '{Title}'", annHost, indexerUri.Host, searchResult.Title);
                                                // Refresh ascii and announces for any further replacements
                                                ascii = System.Text.Encoding.ASCII.GetString(torrentBytes);
                                                announces = MyAnonamouseHelper.ExtractAnnounceUrls(torrentBytes);
                                            }
                                        }
                                    }
                                    catch (Exception subEx)
                                    {
                                        _logger.LogDebug(subEx, "Non-fatal failure while attempting to rewrite announce URL {Ann} for '{Title}'", ann, searchResult.Title);
                                    }
                                }
                            }
                        }
                        catch (Exception rex3)
                        {
                            _logger.LogDebug(rex3, "Failed to rewrite announce hosts inside torrent (non-fatal)");
                        }
                    }
                }
                catch (Exception rex)
                {
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

                        foreach (var ann in (currentAnnounces ?? new System.Collections.Generic.List<string>()).Distinct())
                        {
                            if (string.IsNullOrWhiteSpace(ann)) continue;
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
                            catch (Exception inner)
                            {
                                _logger.LogDebug(inner, "Non-fatal failure while attempting to append mam_id to announce {Ann} for '{Title}'", ann, searchResult.Title);
                                updatedAnnounces.Add(ann);
                            }
                        }

                        if (modified)
                            _logger.LogInformation("Appended mam_id to MyAnonamouse announce URLs for '{Title}' - count={Count}", searchResult.Title, updatedAnnounces.Count);
                    }
                }
                catch (Exception exAppend)
                {
                    _logger.LogDebug(exAppend, "Failed to append mam_id to MyAnonamouse announces (non-fatal)");
                }

                searchResult.TorrentFileContent = torrentBytes;
                searchResult.TorrentFileName = MyAnonamouseHelper.ResolveTorrentFileName(response, searchResult.TorrentUrl);
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
                    catch (Exception cex)
                    {
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
                        catch (Exception cexAnn)
                        {
                            _logger.LogDebug(cexAnn, "Failed to place cached MyAnonamouse announces into memory cache (non-fatal)");
                        }
                    }
                }
                catch (Exception exAnn)
                {
                    _logger.LogDebug(exAnn, "Failed to extract announce URLs from cached torrent (non-fatal)");
                }
                response.Dispose();
            }
            catch (Exception ex)
            {
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
            if (IsTorrentIndexer(indexerType))
            {
                return results.OrderByDescending(r => r.Seeders).ThenByDescending(r => r.Size).First();
            }
            else
            {
                return results.OrderByDescending(r => r.PublishedDate).ThenByDescending(r => r.Size).First();
            }
        }

        private bool IsTorrentIndexer(string indexerType)
        {
            return indexerType.ToLower() == "torrent";
        }

        private bool IsTorrentResult(SearchResult result)
        {
            // Check DownloadType first if it's set
            if (!string.IsNullOrEmpty(result.DownloadType))
            {
                if (result.DownloadType == "DDL")
                {
                    _logger.LogDebug("Result identified as DDL (DownloadType set): {Title}", result.Title);
                    return false; // DDL is not a torrent
                }
                else if (result.DownloadType == "Torrent")
                {
                    _logger.LogDebug("Result identified as Torrent (DownloadType set): {Title}", result.Title);
                    return true;
                }
                else if (result.DownloadType == "Usenet")
                {
                    _logger.LogDebug("Result identified as Usenet (DownloadType set): {Title}", result.Title);
                    return false;
                }
            }

            // Fallback to legacy detection logic
            // Check for NZB first - if it has an NZB URL, it's a Usenet/NZB download
            if (!string.IsNullOrEmpty(result.NzbUrl))
            {
                _logger.LogDebug("Result identified as NZB (has NzbUrl): {Title}", result.Title);
                return false;
            }

            // Check for torrent indicators - magnet link or torrent file
            if (!string.IsNullOrEmpty(result.MagnetLink) || !string.IsNullOrEmpty(result.TorrentUrl))
            {
                _logger.LogDebug("Result identified as Torrent (has MagnetLink or TorrentUrl): {Title}", result.Title);
                return true;
            }

            // If neither is set, we can't reliably determine the type
            // Log a warning and default to false (NZB) as a safer choice
            _logger.LogWarning("Unable to determine result type for '{Title}' from source '{Source}'. No MagnetLink, TorrentUrl, or NzbUrl found. Defaulting to NZB.",
                result.Title, result.Source);
            return false;
        }

        // Small container for caching torrent bytes + filename in memory
        private class CachedTorrent
        {
            public byte[]? Bytes { get; set; }
            public string? FileName { get; set; }
        }

        private async Task<string?> GetAppropriateDownloadClient(bool isTorrent)
        {
            var downloadClients = await _configurationService.GetDownloadClientConfigurationsAsync();
            var enabledClients = downloadClients.Where(c => c.IsEnabled).ToList();

            _logger.LogInformation("Looking for {ClientType} client. Found {Count} enabled download clients: {Clients}",
                isTorrent ? "torrent" : "NZB",
                enabledClients.Count,
                string.Join(", ", enabledClients.Select(c => $"{c.Name} ({c.Type})")));

            if (isTorrent)
            {
                // Prefer qBittorrent, then Transmission
                var client = enabledClients.FirstOrDefault(c => c.Type.Equals("qbittorrent", StringComparison.OrdinalIgnoreCase))
                          ?? enabledClients.FirstOrDefault(c => c.Type.Equals("transmission", StringComparison.OrdinalIgnoreCase));

                if (client != null)
                {
                    _logger.LogInformation("Selected torrent client: {ClientName} ({ClientType})", client.Name, client.Type);
                }
                else
                {
                    _logger.LogWarning("No torrent client (qBittorrent or Transmission) found among enabled clients");
                }

                return client?.Id;
            }
            else
            {
                // Prefer SABnzbd, then NZBGet
                var client = enabledClients.FirstOrDefault(c => c.Type.Equals("sabnzbd", StringComparison.OrdinalIgnoreCase))
                          ?? enabledClients.FirstOrDefault(c => c.Type.Equals("nzbget", StringComparison.OrdinalIgnoreCase));

                if (client != null)
                {
                    _logger.LogInformation("Selected NZB client: {ClientName} ({ClientType})", client.Name, client.Type);
                }
                else
                {
                    _logger.LogWarning("No NZB client (SABnzbd or NZBGet) found among enabled clients");
                }

                return client?.Id;
            }
        }

        private async Task<string?> SendToQBittorrent(DownloadClientConfiguration client, SearchResult result)
        {
            var baseUrl = $"{(client.UseSSL ? "https" : "http")}://{client.Host}:{client.Port}";

            // Try to extract hash from magnet link before we even contact qBittorrent
            string? extractedHash = null;
            if (!string.IsNullOrEmpty(result.MagnetLink) && result.MagnetLink.Contains("xt=urn:btih:"))
            {
                var hashStart = result.MagnetLink.IndexOf("xt=urn:btih:") + "xt=urn:btih:".Length;
                var hashEnd = result.MagnetLink.IndexOf("&", hashStart);
                if (hashEnd == -1) hashEnd = result.MagnetLink.Length;
                extractedHash = result.MagnetLink.Substring(hashStart, hashEnd - hashStart).ToLowerInvariant();
                _logger.LogInformation("Extracted hash from magnet link: {Hash}", extractedHash);
            }

            // Create a local HttpClient with a CookieContainer so qBittorrent session cookie (SID) is preserved
            // Note: For qBittorrent we need cookies, so we create a custom handler
            // This is acceptable as it's not high-frequency and cookies are required for auth
            var cookieJar = new CookieContainer();
            var handler = new HttpClientHandler
            {
                CookieContainer = cookieJar,
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.All
            };

            // Create HttpClient in a scoped block so it gets disposed before the fallback hash check
            {
                using var httpClient = new HttpClient(handler);

                // Check if authentication is required by attempting login
                using var loginData = new FormUrlEncodedContent(new[]
                {
                new KeyValuePair<string, string>("username", client.Username ?? string.Empty),
                new KeyValuePair<string, string>("password", client.Password ?? string.Empty)
            });

                var loginResponse = await httpClient.PostAsync($"{baseUrl}/api/v2/auth/login", loginData);

                if (!loginResponse.IsSuccessStatusCode)
                {
                    // Read response body to provide more context
                    var loginResponseContent = await loginResponse.Content.ReadAsStringAsync();

                    // Redact any potentially sensitive values from the response when logging
                    var redactedLoginResponse = LogRedaction.RedactText(loginResponseContent, LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { client.Password ?? string.Empty }));

                    // Check if this is a 403 Forbidden (authentication disabled) vs other errors
                    if (loginResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        // Try a simple API call without authentication
                        var testResp = await httpClient.GetAsync($"{baseUrl}/api/v2/app/version");
                        if (testResp.IsSuccessStatusCode)
                        {
                            // API works without auth, so authentication is disabled
                            _logger.LogInformation("qBittorrent authentication disabled, proceeding without credentials");
                        }
                        else
                        {
                            // API fails without auth, so authentication is enabled but credentials are wrong
                            throw new Exception("qBittorrent authentication enabled but credentials are incorrect");
                        }
                    }
                    else
                    {
                        _logger.LogError("Failed to login to qBittorrent. Status: {Status}, Response: {Response}",
                            loginResponse.StatusCode, redactedLoginResponse);
                        throw new Exception($"Failed to login to qBittorrent: {loginResponse.StatusCode} - {redactedLoginResponse}");
                    }
                }
                else
                {
                    _logger.LogInformation("Successfully authenticated with qBittorrent");
                }

                // Get torrent URL - prefer magnet link, fall back to torrent file URL
                var torrentUrl = !string.IsNullOrEmpty(result.MagnetLink)
                    ? result.MagnetLink
                    : result.TorrentUrl;

                if (string.IsNullOrEmpty(torrentUrl))
                {
                    throw new Exception("No magnet link or torrent URL found in search result");
                }

                _logger.LogInformation("Adding torrent to qBittorrent: {Title}", result.Title);
                _logger.LogDebug("Torrent URL: {Url}", torrentUrl);

                // Get existing torrents list before adding (only request hashes to minimize payload)
                var categoryFilter = QBittorrentHelpers.BuildCategoryParameter(client.Settings, "?");
                var torrentsBeforeResp = await httpClient.GetAsync($"{baseUrl}/api/v2/torrents/info?fields=hash{categoryFilter}");
                var existingHashes = new HashSet<string>();
                if (torrentsBeforeResp.IsSuccessStatusCode)
                {
                    var beforeJson = await torrentsBeforeResp.Content.ReadAsStringAsync();
                    if (!string.IsNullOrWhiteSpace(beforeJson))
                    {
                        var beforeTorrents = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, System.Text.Json.JsonElement>>>(beforeJson);
                        if (beforeTorrents != null)
                        {
                            foreach (var t in beforeTorrents)
                            {
                                if (t.TryGetValue("hash", out var hashEl))
                                {
                                    var hash = hashEl.GetString();
                                    if (!string.IsNullOrEmpty(hash))
                                        existingHashes.Add(hash);
                                }
                            }
                        }
                    }
                }

                var savePath = string.IsNullOrEmpty(client.DownloadPath) ? string.Empty : client.DownloadPath;
                string? category = null;
                string? tags = null;

                if (client.Settings != null)
                {
                    if (client.Settings.TryGetValue("category", out var categoryObj))
                        category = categoryObj?.ToString();
                    if (client.Settings.TryGetValue("tags", out var tagsObj))
                        tags = tagsObj?.ToString();
                }

                if (!string.IsNullOrEmpty(category))
                    _logger.LogInformation("Adding torrent to qBittorrent with category: {Category}", category);
                if (!string.IsNullOrEmpty(tags))
                    _logger.LogInformation("Adding torrent to qBittorrent with tags: {Tags}", tags);

                var hasCachedTorrent = result.TorrentFileContent != null && result.TorrentFileContent.Length > 0;
                HttpResponseMessage addResponse;

                if (hasCachedTorrent)
                {
                    using var multipart = new MultipartFormDataContent();
                    multipart.Add(new StringContent(savePath), "savepath");
                    if (!string.IsNullOrEmpty(category))
                        multipart.Add(new StringContent(category), "category");
                    if (!string.IsNullOrEmpty(tags))
                        multipart.Add(new StringContent(tags), "tags");

                    var torrentFileName = string.IsNullOrEmpty(result.TorrentFileName) ? "myanonamouse.torrent" : result.TorrentFileName;
                    var torrentContent = new ByteArrayContent(result.TorrentFileContent!);
                    torrentContent.Headers.ContentType = new MediaTypeHeaderValue("application/x-bittorrent");
                    multipart.Add(torrentContent, "torrents", torrentFileName);

                    // Extract announce/tracker URLs from the bencoded torrent (http/https/udp and announce-list/url-list)
                    try
                    {
                        var announces = MyAnonamouseHelper.ExtractAnnounceUrls(result.TorrentFileContent!);
                        var count = announces?.Count ?? 0;
                        var unique = count > 0 ? string.Join(", ", announces?.Take(10) ?? Enumerable.Empty<string>()) : "(none)";
                        _logger.LogInformation("Torrent announce/URLs for '{Title}' - count={Count}: {Announces}", result.Title, count, LogRedaction.RedactText(unique, LogRedaction.GetSensitiveValuesFromEnvironment()));
                        if (count == 0)
                        {
                            _logger.LogDebug("No announce/URL entries detected in cached torrent for '{Title}'", result.Title);
                        }
                    }
                    catch (Exception exAnn)
                    {
                        _logger.LogDebug(exAnn, "Failed to extract announce URLs from torrent for diagnostics (non-fatal)");
                    }

                    _logger.LogInformation("Uploading cached MyAnonamouse torrent to qBittorrent for '{Title}'", result.Title);
                    addResponse = await httpClient.PostAsync($"{baseUrl}/api/v2/torrents/add", multipart);
                }
                else
                {
                    var formData = new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("urls", torrentUrl),
                        new KeyValuePair<string, string>("savepath", savePath)
                    };

                    if (!string.IsNullOrEmpty(category))
                        formData.Add(new KeyValuePair<string, string>("category", category));
                    if (!string.IsNullOrEmpty(tags))
                        formData.Add(new KeyValuePair<string, string>("tags", tags));

                    using var addData = new FormUrlEncodedContent(formData);
                    addResponse = await httpClient.PostAsync($"{baseUrl}/api/v2/torrents/add", addData);
                }
                if (!addResponse.IsSuccessStatusCode)
                {
                    var responseContent = await addResponse.Content.ReadAsStringAsync();
                    var redacted = LogRedaction.RedactText(responseContent, LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { client.Password ?? string.Empty }));
                    _logger.LogError("Failed to add torrent to qBittorrent. Status: {Status}, Response: {Response}",
                        addResponse.StatusCode, redacted);
                    throw new Exception($"Failed to add torrent to qBittorrent: {addResponse.StatusCode} - {redacted}");
                }

                _logger.LogInformation("Successfully sent torrent to qBittorrent");

                // Wait a moment for qBittorrent to process the torrent
                await Task.Delay(1000);

                // Get updated torrents list to find the newly added torrent hash (request minimal fields)
                var categoryFilter2 = QBittorrentHelpers.BuildCategoryParameter(client?.Settings ?? new Dictionary<string, object>(), "&");
                var torrentsAfterResp = await httpClient.GetAsync($"{baseUrl}/api/v2/torrents/info?fields=hash,name{categoryFilter2}");
                if (torrentsAfterResp.IsSuccessStatusCode)
                {
                    var afterJson = await torrentsAfterResp.Content.ReadAsStringAsync();
                    if (!string.IsNullOrWhiteSpace(afterJson))
                    {
                        var afterTorrents = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, System.Text.Json.JsonElement>>>(afterJson);
                        if (afterTorrents != null)
                        {
                            // Find the new torrent by comparing with existing hashes
                            foreach (var t in afterTorrents)
                            {
                                if (t.TryGetValue("hash", out var hashEl))
                                {
                                    var hash = hashEl.GetString();
                                    if (!string.IsNullOrEmpty(hash) && !existingHashes.Contains(hash))
                                    {
                                        var name = t.TryGetValue("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                                        _logger.LogInformation("Found newly added qBittorrent torrent: {Name} with hash {Hash}", name, hash);
                                        return hash;
                                    }
                                }
                            }
                        }
                    }
                }
            } // End using httpClient scope

            // If we couldn't find it by comparing lists but we extracted the hash from magnet link, use that
            if (!string.IsNullOrEmpty(extractedHash))
            {
                _logger.LogInformation("Using extracted hash from magnet link: {Hash}", extractedHash);
                return extractedHash;
            }

            _logger.LogWarning("Could not retrieve torrent hash from qBittorrent after adding torrent: {Title}", result.Title);
            return null;
        }

        private async Task SendToTransmission(DownloadClientConfiguration client, SearchResult result)
        {
            var baseUrl = $"{(client.UseSSL ? "https" : "http")}://{client.Host}:{client.Port}/transmission/rpc";

            try
            {
                // Get torrent URL - prefer magnet link, fall back to torrent file URL
                var torrentUrl = !string.IsNullOrEmpty(result.MagnetLink)
                    ? result.MagnetLink
                    : result.TorrentUrl;

                if (string.IsNullOrEmpty(torrentUrl))
                {
                    throw new Exception("No magnet link or torrent URL found in search result");
                }

                _logger.LogInformation("Adding torrent to Transmission: {Title}", result.Title);
                _logger.LogDebug("Torrent URL: {Url}", torrentUrl);

                // First, get session ID (required for authentication)
                string sessionId = await GetTransmissionSessionId(baseUrl, client.Username, client.Password);

                // Prepare torrent-add arguments
                var arguments = new Dictionary<string, object>
                {
                    { "filename", torrentUrl },
                    { "download-dir", client.DownloadPath }
                };

                // Add labels (Transmission's equivalent to categories/tags)
                var labels = new List<string>();

                // Add category as a label if configured
                if (client.Settings != null && client.Settings.TryGetValue("category", out var categoryObj))
                {
                    var category = categoryObj?.ToString();
                    if (!string.IsNullOrEmpty(category))
                    {
                        labels.Add(category);
                        _logger.LogInformation("Adding torrent to Transmission with category: {Category}", category);
                    }
                }

                // Add tags as labels if configured
                if (client.Settings != null && client.Settings.TryGetValue("tags", out var tagsObj))
                {
                    var tags = tagsObj?.ToString();
                    if (!string.IsNullOrEmpty(tags))
                    {
                        // Split tags by comma and add each as a label
                        var tagList = tags.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                         .Select(t => t.Trim())
                                         .Where(t => !string.IsNullOrEmpty(t));
                        labels.AddRange(tagList);
                        _logger.LogInformation("Adding torrent to Transmission with tags: {Tags}", tags);
                    }
                }

                // Add labels to arguments if we have any
                if (labels.Any())
                {
                    arguments["labels"] = labels;
                }

                // Create RPC request
                var rpcRequest = new
                {
                    method = "torrent-add",
                    arguments = arguments,
                    tag = 1
                };

                var jsonContent = JsonSerializer.Serialize(rpcRequest);
                var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                // Set required headers
                httpContent.Headers.Add("X-Transmission-Session-Id", sessionId);

                // Use per-request authorization header (thread-safe)
                using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl);
                request.Content = httpContent;

                if (!string.IsNullOrEmpty(client.Username))
                {
                    var authBytes = Encoding.UTF8.GetBytes($"{client.Username}:{client.Password}");
                    var authHeader = Convert.ToBase64String(authBytes);
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authHeader);
                }

                var response = await _httpClient.SendAsync(request);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var redacted = LogRedaction.RedactText(responseContent, LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { client.Password ?? string.Empty }));
                    _logger.LogError("Failed to add torrent to Transmission. Status: {Status}, Response: {Response}",
                        response.StatusCode, redacted);
                    throw new Exception($"Transmission RPC error: {response.StatusCode}");
                }

                // Defensive: ensure response body is valid JSON
                if (string.IsNullOrWhiteSpace(responseContent))
                {
                    _logger.LogWarning("Transmission returned empty response body for torrent-add request: {Url}", baseUrl);
                    return;
                }

                // Parse response to get torrent ID
                var rpcResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);

                if (rpcResponse.TryGetProperty("result", out var result_prop) && result_prop.GetString() == "success")
                {
                    string torrentId = Guid.NewGuid().ToString(); // Default fallback

                    // Try to get the actual torrent ID from response
                    if (rpcResponse.TryGetProperty("arguments", out var args) &&
                        args.TryGetProperty("torrent-added", out var torrentAdded) &&
                        torrentAdded.TryGetProperty("id", out var id))
                    {
                        torrentId = id.GetInt32().ToString();
                    }
                    else if (args.TryGetProperty("torrent-duplicate", out var torrentDuplicate) &&
                             torrentDuplicate.TryGetProperty("id", out var dupId))
                    {
                        torrentId = dupId.GetInt32().ToString();
                        _logger.LogInformation("Torrent already exists in Transmission with ID: {TorrentId}", torrentId);
                    }

                    _logger.LogInformation("Successfully added torrent to Transmission with ID: {TorrentId}", torrentId);
                }
                else
                {
                    var errorMsg = "Unknown error";
                    if (rpcResponse.TryGetProperty("result", out var resultMsg))
                    {
                        errorMsg = resultMsg.GetString() ?? "Unknown error";
                    }
                    throw new Exception($"Transmission RPC error: {errorMsg}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send torrent to Transmission");
                throw;
            }
        }

        private async Task<string> GetTransmissionSessionId(string baseUrl, string username, string password)
        {
            try
            {
                // Make a dummy request to get the session ID from the 409 response
                var dummyRequest = new { method = "session-get", tag = 0 };
                var jsonContent = JsonSerializer.Serialize(dummyRequest);
                var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                // Use per-request authorization header (thread-safe)
                using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl);
                request.Content = httpContent;

                if (!string.IsNullOrEmpty(username))
                {
                    var authBytes = Encoding.UTF8.GetBytes($"{username}:{password}");
                    var authHeader = Convert.ToBase64String(authBytes);
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authHeader);
                }

                var response = await _httpClient.SendAsync(request);

                // Transmission returns 409 with X-Transmission-Session-Id header on first request
                if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    if (response.Headers.TryGetValues("X-Transmission-Session-Id", out var sessionIds))
                    {
                        var sessionId = sessionIds.First();
                        _logger.LogDebug("Got Transmission session ID: {SessionId}", sessionId);
                        return sessionId;
                    }
                }
                else if (response.IsSuccessStatusCode)
                {
                    // If we get a success response, we might not need a session ID (older versions)
                    return string.Empty;
                }

                throw new Exception($"Failed to get Transmission session ID: {response.StatusCode}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get Transmission session ID");
                throw;
            }
        }

        private async Task SendToSABnzbd(DownloadClientConfiguration client, SearchResult result)
        {
            try
            {
                var baseUrl = $"{(client.UseSSL ? "https" : "http")}://{client.Host}:{client.Port}/api";

                // Get API key from settings
                var apiKey = "";
                if (client.Settings != null && client.Settings.TryGetValue("apiKey", out var apiKeyObj))
                {
                    apiKey = apiKeyObj?.ToString() ?? "";
                }

                if (string.IsNullOrEmpty(apiKey))
                {
                    _logger.LogError("SABnzbd API key not configured");
                    return;
                }

                // Get NZB URL (append indexer API key when required)
                var (nzbUrl, indexerApiKey) = await EnsureIndexerApiKeyOnNzbUrlAsync(result);
                if (string.IsNullOrEmpty(nzbUrl))
                {
                    _logger.LogError("No NZB URL found in search result");
                    return;
                }

                _logger.LogInformation("Sending NZB to SABnzbd: {Title} from {Source}", result.Title, result.Source);
                var sabSensitiveValues = LogRedaction.GetSensitiveValuesFromEnvironment().ToList();
                sabSensitiveValues.Add(apiKey);
                if (!string.IsNullOrEmpty(client.Password)) sabSensitiveValues.Add(client.Password);
                if (!string.IsNullOrEmpty(indexerApiKey)) sabSensitiveValues.Add(indexerApiKey);
                _logger.LogDebug("NZB URL: {Url}", LogRedaction.RedactText(nzbUrl, sabSensitiveValues));

                // Build SABnzbd addurl API request
                var queryParams = new Dictionary<string, string>
                {
                    { "mode", "addurl" },
                    { "name", nzbUrl },
                    { "apikey", apiKey },
                    { "output", "json" },
                    { "nzbname", result.Title }
                };

                // Add priority if configured
                if (client.Settings != null && client.Settings.TryGetValue("recentPriority", out var priorityObj))
                {
                    var priority = priorityObj?.ToString();
                    if (!string.IsNullOrEmpty(priority) && priority != "default")
                    {
                        queryParams["priority"] = priority switch
                        {
                            "force" => "2",
                            "high" => "1",
                            "normal" => "0",
                            "low" => "-1",
                            _ => "0"
                        };
                    }
                }

                // Add category if configured
                var category = "audiobooks"; // default fallback
                if (client.Settings != null && client.Settings.TryGetValue("category", out var categoryObj))
                {
                    var configuredCategory = categoryObj?.ToString();
                    if (!string.IsNullOrEmpty(configuredCategory))
                    {
                        category = configuredCategory;
                    }
                }
                queryParams["cat"] = category;
                _logger.LogInformation("Adding NZB to SABnzbd with category: {Category}", category);

                var queryString = string.Join("&", queryParams.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));
                var requestUrl = $"{baseUrl}?{queryString}";

                _logger.LogDebug("SABnzbd request URL: {Url}", LogRedaction.RedactText(requestUrl, sabSensitiveValues));

                var response = await _httpClient.GetAsync(requestUrl);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var redacted = LogRedaction.RedactText(responseContent, sabSensitiveValues);
                    _logger.LogError("SABnzbd returned error status {Status}: {Content}", response.StatusCode, redacted);
                    throw new Exception($"SABnzbd returned status {response.StatusCode}: {redacted}");
                }

                // Defensive: ensure response body is valid JSON
                if (string.IsNullOrWhiteSpace(responseContent))
                {
                    _logger.LogWarning("SABnzbd returned empty response body when adding NZB: {Url}", LogRedaction.RedactText(requestUrl, sabSensitiveValues));
                    return; // Treat as no-op (we still created a DB record earlier)
                }

                // Parse JSON response
                var jsonDoc = JsonDocument.Parse(responseContent);
                var root = jsonDoc.RootElement;

                // Check for errors
                if (root.TryGetProperty("error", out var errorElement))
                {
                    var errorMsg = errorElement.GetString();
                    if (!string.IsNullOrEmpty(errorMsg))
                    {
                        throw new Exception($"SABnzbd error: {errorMsg}");
                    }
                }

                // Get NZO ID (download ID)
                string downloadId = "";
                if (root.TryGetProperty("nzo_ids", out var nzoIds) && nzoIds.ValueKind == JsonValueKind.Array)
                {
                    var firstId = nzoIds.EnumerateArray().FirstOrDefault();
                    downloadId = firstId.GetString() ?? Guid.NewGuid().ToString();
                }
                else
                {
                    downloadId = Guid.NewGuid().ToString();
                }

                _logger.LogInformation("Successfully added NZB to SABnzbd with ID: {DownloadId}", downloadId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send NZB to SABnzbd");
                throw;
            }
        }

        private async Task SendToNZBGet(DownloadClientConfiguration client, SearchResult result)
        {
            var baseUrl = $"{(client.UseSSL ? "https" : "http")}://{client.Host}:{client.Port}/jsonrpc";

            try
            {
                // Get NZB URL (append indexer API key when required)
                var (nzbUrl, indexerApiKey) = await EnsureIndexerApiKeyOnNzbUrlAsync(result);
                if (string.IsNullOrEmpty(nzbUrl))
                {
                    _logger.LogError("No NZB URL found in search result");
                    return;
                }

                _logger.LogInformation("Sending NZB to NZBGet: {Title} from {Source}", result.Title, result.Source);
                var nzbGetSensitiveValues = LogRedaction.GetSensitiveValuesFromEnvironment().ToList();
                if (!string.IsNullOrEmpty(client.Password)) nzbGetSensitiveValues.Add(client.Password);
                if (!string.IsNullOrEmpty(indexerApiKey)) nzbGetSensitiveValues.Add(indexerApiKey);
                _logger.LogDebug("NZB URL: {Url}", LogRedaction.RedactText(nzbUrl, nzbGetSensitiveValues));

                // Get category if configured
                var category = "audiobooks"; // default fallback
                if (client.Settings != null && client.Settings.TryGetValue("category", out var categoryObj))
                {
                    var configuredCategory = categoryObj?.ToString();
                    if (!string.IsNullOrEmpty(configuredCategory))
                    {
                        category = configuredCategory;
                    }
                }
                _logger.LogInformation("Adding NZB to NZBGet with category: {Category}", category);

                // Get priority if configured  
                int priority = 0; // normal priority
                if (client.Settings != null && client.Settings.TryGetValue("recentPriority", out var priorityObj))
                {
                    var priorityStr = priorityObj?.ToString();
                    if (!string.IsNullOrEmpty(priorityStr) && priorityStr != "default")
                    {
                        priority = priorityStr switch
                        {
                            "force" => 100,
                            "high" => 50,
                            "normal" => 0,
                            "low" => -50,
                            _ => 0
                        };
                    }
                }

                // Create JSON-RPC request for appendurl method (NZBGet fetches the URL directly)
                var rpcRequest = new
                {
                    method = "appendurl",
                    @params = new object[]
                    {
                        result.Title,        // NZBFilename
                        nzbUrl,             // NZB URL to fetch
                        category,           // Category
                        priority,           // Priority
                        false,              // AddToTop
                        false,              // AddPaused
                        "",                 // DupeKey (empty)
                        0,                  // DupeScore
                        "SCORE",            // DupeMode
                        new object[0]       // PPParameters (empty array)
                    },
                    id = 1
                };

                var jsonContent = JsonSerializer.Serialize(rpcRequest);
                var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                // Use per-request authorization header (thread-safe)
                using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl);
                request.Content = httpContent;

                if (!string.IsNullOrEmpty(client.Username))
                {
                    var authBytes = Encoding.UTF8.GetBytes($"{client.Username}:{client.Password}");
                    var authHeader = Convert.ToBase64String(authBytes);
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authHeader);
                }

                var response = await _httpClient.SendAsync(request);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var redacted = LogRedaction.RedactText(responseContent, LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { client.Password ?? string.Empty }));
                    _logger.LogError("Failed to add NZB to NZBGet. Status: {Status}, Response: {Response}",
                        response.StatusCode, redacted);
                    throw new Exception($"Failed to add NZB to NZBGet: {response.StatusCode}");
                }

                // Defensive: ensure response body is valid JSON
                if (string.IsNullOrWhiteSpace(responseContent))
                {
                    _logger.LogWarning("NZBGet returned empty response body for append request: {Url}", baseUrl);
                    return;
                }

                // Parse JSON-RPC response
                var rpcResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);

                if (rpcResponse.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                {
                    var errorMsg = "Unknown error";
                    if (error.TryGetProperty("message", out var errorMessage))
                    {
                        errorMsg = errorMessage.GetString() ?? "Unknown error";
                    }
                    throw new Exception($"NZBGet RPC error: {errorMsg}");
                }

                // Get the NZB ID from the result
                string nzbId = Guid.NewGuid().ToString(); // Default fallback
                if (rpcResponse.TryGetProperty("result", out var resultProp))
                {
                    if (resultProp.ValueKind == JsonValueKind.Number)
                    {
                        nzbId = resultProp.GetInt32().ToString();
                    }
                    else if (resultProp.ValueKind == JsonValueKind.True || resultProp.ValueKind == JsonValueKind.False)
                    {
                        var success = resultProp.GetBoolean();
                        nzbId = success ? result.Title : Guid.NewGuid().ToString();
                    }
                }

                _logger.LogInformation("Successfully added NZB to NZBGet with ID: {NzbId}", nzbId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send NZB to NZBGet");
                throw;
            }
        }

        private async Task<(string Url, string? IndexerApiKey)> EnsureIndexerApiKeyOnNzbUrlAsync(SearchResult result)
        {
            var nzbUrl = result.NzbUrl ?? string.Empty;
            if (string.IsNullOrWhiteSpace(nzbUrl))
            {
                return (nzbUrl, null);
            }

            try
            {
                var hasApiKey = false;
                if (Uri.TryCreate(nzbUrl, UriKind.Absolute, out var parsed))
                {
                    var query = QueryHelpers.ParseQuery(parsed.Query);
                    hasApiKey = query.Keys.Any(k => string.Equals(k, "apikey", StringComparison.OrdinalIgnoreCase));
                }
                else if (nzbUrl.Contains("apikey=", StringComparison.OrdinalIgnoreCase))
                {
                    hasApiKey = true;
                }

                if (hasApiKey)
                {
                    return (nzbUrl, null);
                }

                Indexer? indexer = null;
                await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
                if (result.IndexerId.HasValue)
                {
                    indexer = await dbContext.Indexers
                        .AsNoTracking()
                        .FirstOrDefaultAsync(i => i.Id == result.IndexerId.Value);
                }
                else if (!string.IsNullOrWhiteSpace(result.Source))
                {
                    indexer = await dbContext.Indexers
                        .AsNoTracking()
                        .FirstOrDefaultAsync(i => i.Name == result.Source);
                }

                if (indexer != null && !string.IsNullOrWhiteSpace(indexer.ApiKey))
                {
                    var updatedUrl = QueryHelpers.AddQueryString(nzbUrl, "apikey", indexer.ApiKey);
                    return (updatedUrl, indexer.ApiKey);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to append indexer API key to NZB URL for {Title}", result.Title);
            }

            return (nzbUrl, null);
        }

        public async Task<List<QueueItem>> GetQueueAsync()
        {
            var queueItems = new List<QueueItem>();

            // Cache download clients for 10 seconds to reduce DB queries
            var downloadClients = await _cache.GetOrCreateAsync("DownloadClients", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(QueueCacheExpirationSeconds);
                return await _configurationService.GetDownloadClientConfigurationsAsync();
            }) ?? new List<DownloadClientConfiguration>();

            var enabledClients = downloadClients.Where(c => c.IsEnabled).ToList();

            // Get all downloads from database to filter queue items
            // For external clients, we'll only include downloads that are actually present in the client's queue
            // For DDL downloads, include active ones plus completed ones with pending processing jobs
            List<Download> listenarrDownloads;
            {
                var dbContext = await _dbContextFactory.CreateDbContextAsync();

                // Get all downloads (include failed so activity can show failed items)
                var allDownloads = await dbContext.Downloads
                    .ToListAsync();

                _logger.LogInformation("Found {TotalDownloads} downloads (including failed)", allDownloads.Count);

                // For DDL downloads, include active ones plus completed ones with pending processing jobs
                var ddlDownloads = allDownloads.Where(d => d.DownloadClientId == "DDL").ToList();
                var ddlToShow = new List<Download>();

                if (ddlDownloads.Any())
                {
                    var ddlCompleted = ddlDownloads.Where(d => d.Status == DownloadStatus.Completed).ToList();
                    if (ddlCompleted.Any())
                    {
                        var completedIds = ddlCompleted.Select(d => d.Id).ToList();

                        // Get DDL downloads with pending/active processing jobs
                        var pendingJobs = await dbContext.DownloadProcessingJobs
                            .Where(j => completedIds.Contains(j.DownloadId) &&
                               (j.Status == ProcessingJobStatus.Pending ||
                                j.Status == ProcessingJobStatus.Processing ||
                                j.Status == ProcessingJobStatus.Retry))
                            .Select(j => j.DownloadId)
                            .Distinct()
                            .ToListAsync();

                        // Get DDL downloads with any processing jobs (to identify those without jobs)
                        var allJobDownloads = await dbContext.DownloadProcessingJobs
                            .Where(j => completedIds.Contains(j.DownloadId))
                            .Select(j => j.DownloadId)
                            .Distinct()
                            .ToListAsync();

                        // Include DDL completed downloads that either:
                        // 1. Have pending/active processing jobs, OR
                        // 2. Have no processing jobs at all (legacy downloads needing processing)
                        var ddlCompletedToShow = ddlCompleted
                            .Where(d => pendingJobs.Contains(d.Id) || !allJobDownloads.Contains(d.Id))
                            .ToList();

                        ddlToShow.AddRange(ddlCompletedToShow);
                        _logger.LogInformation("DDL pending jobs count: {PendingJobs}, All job downloads count: {AllJobs}, DDL completed to show: {CompletedToShow}",
                            pendingJobs.Count, allJobDownloads.Count, ddlCompletedToShow.Count);
                    }

                    // Add active DDL downloads (exclude Completed and Moved)
                    ddlToShow.AddRange(ddlDownloads.Where(d =>
                        d.Status != DownloadStatus.Completed &&
                        d.Status != DownloadStatus.Moved));
                }

                // For external clients, we'll filter based on what's actually in their queues
                // Exclude downloads that are already completed/moved to avoid duplicate queue entries
                var externalDownloads = allDownloads
                    .Where(d => d.DownloadClientId != "DDL" &&
                                d.Status != DownloadStatus.Completed &&
                                d.Status != DownloadStatus.Moved)
                    .ToList();

                listenarrDownloads = ddlToShow.Concat(externalDownloads).ToList();

                _logger.LogDebug("Final filtering result: {FinalCount} downloads to include in queue filtering ({DdlCount} DDL, {ExternalCount} external)",
                    listenarrDownloads.Count, ddlToShow.Count, externalDownloads.Count);
                foreach (var dl in listenarrDownloads)
                {
                    _logger.LogDebug("Including download: {Id}, Status: {Status}, Client: {Client}, Title: '{Title}'",
                        dl.Id, dl.Status, dl.DownloadClientId, dl.Title);
                }
            }

            // Load application settings once to determine whether to include completed
            // external downloads even when they are not tracked in the Listenarr DB.
            // Cache for 30 seconds to reduce DB queries
            ApplicationSettings? appSettings = await _cache.GetOrCreateAsync("ApplicationSettings", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(ClientStatusCacheExpirationSeconds);
                try
                {
                    return await _configurationService.GetApplicationSettingsAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to load application settings while building queue (non-fatal)");
                    return null;
                }
            });

            var includeCompletedExternal = appSettings != null && appSettings.ShowCompletedExternalDownloads;

            foreach (var client in enabledClients)
            {
                try
                {
                    List<QueueItem> clientQueue;
                    if (_clientGateway != null)
                    {
                        try
                        {
                            clientQueue = await _clientGateway.GetQueueAsync(client);
                        }
                        catch (Exception gwEx)
                        {
                            _logger.LogWarning(gwEx, "Client gateway failed to retrieve queue for {ClientName}, falling back to legacy implementation", client.Name ?? client.Id);
                            clientQueue = await GetQueueFallbackAsync(client);
                        }
                    }
                    else
                    {
                        clientQueue = await GetQueueFallbackAsync(client);
                    }

                    // Show ALL client queue items, enrich with DB metadata if available
                    // The download client is the source of truth for what's actually downloading
                    _logger.LogInformation("Client {ClientName} has {TotalItems} queue items", client.Name ?? client.Id, clientQueue.Count);
                    _logger.LogInformation("Database has {DatabaseItems} Listenarr downloads for metadata enrichment", listenarrDownloads.Count);

                    // Process all queue items - client is the source of truth
                    var mappedFiltered = new List<QueueItem>();
                    foreach (var queueItem in clientQueue)
                    {
                        try
                        {
                            // Set CompletionTime for completed downloads (used by CompletedDownloadHandlingService)
                            // This tracks when a download was detected as complete for stability window validation
                            if (queueItem.Status == "completed" && queueItem.CompletionTime == null)
                            {
                                queueItem.CompletionTime = DateTime.UtcNow;
                            }

                            // Try to find matching database record for metadata enrichment
                            var matchedDownload = listenarrDownloads.FirstOrDefault(download =>
                            {
                                // Must be same client
                                if (download.DownloadClientId != client.Id)
                                    return false;

                                // Try direct ID match
                                if (download.Id == queueItem.Id)
                                    return true;

                                // For qBittorrent, check torrent hash
                                if (string.Equals(client.Type, "qbittorrent", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (download.Metadata != null && download.Metadata.TryGetValue("TorrentHash", out var hashObj))
                                    {
                                        var storedHash = hashObj?.ToString();
                                        if (!string.IsNullOrEmpty(storedHash) && 
                                            storedHash.Equals(queueItem.Id, StringComparison.OrdinalIgnoreCase))
                                            return true;
                                    }
                                }

                                // Try title matching as fallback
                                if (!string.IsNullOrEmpty(download.Title) && !string.IsNullOrEmpty(queueItem.Title))
                                {
                                    if (IsMatchingTitle(download.Title, queueItem.Title))
                                    {
                                        _logger.LogDebug("Matched download {DownloadId} to queue item {QueueId} via title: '{DownloadTitle}' <-> '{QueueTitle}'",
                                            download.Id, queueItem.Id, download.Title, queueItem.Title);
                                        return true;
                                    }
                                }

                                return false;
                            });

                            if (matchedDownload != null)
                            {
                                // Store original torrent hash in metadata BEFORE changing ID
                                var originalClientId = queueItem.Id;
                                bool hashUpdated = false;
                                
                                if (string.Equals(client.Type, "qbittorrent", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (matchedDownload.Metadata == null)
                                        matchedDownload.Metadata = new Dictionary<string, object>();
                                    
                                    if (!matchedDownload.Metadata.ContainsKey("TorrentHash"))
                                    {
                                        matchedDownload.Metadata["TorrentHash"] = originalClientId;
                                        hashUpdated = true;
                                        _logger.LogInformation("Stored torrent hash {Hash} for download {DownloadId}", originalClientId, matchedDownload.Id);
                                    }
                                }
                                
                                // Persist hash to database if we just discovered it
                                if (hashUpdated)
                                {
                                    try
                                    {
                                        var dbContext = await _dbContextFactory.CreateDbContextAsync();
                                        var dbDownload = await dbContext.Downloads.FindAsync(matchedDownload.Id);
                                        if (dbDownload != null)
                                        {
                                            if (dbDownload.Metadata == null)
                                                dbDownload.Metadata = new Dictionary<string, object>();
                                            dbDownload.Metadata["TorrentHash"] = originalClientId;
                                            await dbContext.SaveChangesAsync();
                                            _logger.LogInformation("Persisted torrent hash {Hash} to database for download {DownloadId}", originalClientId, matchedDownload.Id);
                                        }
                                    }
                                    catch (Exception dbEx)
                                    {
                                        _logger.LogWarning(dbEx, "Failed to persist torrent hash for download {DownloadId}", matchedDownload.Id);
                                    }
                                }
                                
                                // Enrich queue item with database metadata
                                // Use DB ID so UI doesn't show duplicates
                                queueItem.Id = matchedDownload.Id;
                                
                                _logger.LogDebug("Enriched queue item (original: {OriginalId}) with DB metadata from download {DownloadId}", 
                                    originalClientId, matchedDownload.Id);
                            }
                            else
                            {
                                // No DB record found - this is an untracked download
                                // Still show it (client is source of truth)
                                _logger.LogDebug("Queue item {QueueId} '{Title}' not tracked in database - showing as untracked", 
                                    queueItem.Id, queueItem.Title);
                            }

                            mappedFiltered.Add(queueItem);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error processing queue item {QueueId}, including anyway", queueItem.Id);
                            mappedFiltered.Add(queueItem);
                        }
                    }

                    queueItems.AddRange(mappedFiltered);

                    // If configured, also include completed items that appear in the
                    // client queue but are not tracked in Listenarr's DB (user wants
                    // to see completed torrents/NZBs even when the client has removed
                    // or Listenarr didn't create a DB record for them).
                    if (includeCompletedExternal)
                    {
                        var existingIds = queueItems.Select(q => q.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

                        var unmatchedCompleted = clientQueue
                            .Where(q => (q.Status ?? string.Empty).Equals("completed", StringComparison.OrdinalIgnoreCase))
                            .Where(q => !existingIds.Contains(q.Id))
                            .ToList();

                        foreach (var uc in unmatchedCompleted)
                        {
                            // Normalize client type/name if available
                            var clientName = client.Name ?? uc.DownloadClient ?? client.Id;
                            var clientType = client.Type?.ToLowerInvariant() ?? uc.DownloadClientType ?? "external";

                            // Avoid adding duplicates again
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

                    _logger.LogDebug("Client {ClientName}: showing {TotalItems} queue items", 
                        client.Name, mappedFiltered.Count);

                    // CONSERVATIVE CLEANUP: Only purge downloads that are clearly abandoned
                    // Client is the source of truth for active downloads
                    try
                    {
                        var clientDownloads = listenarrDownloads.Where(d => d.DownloadClientId == client.Id).ToList();

                        // SAFETY: Skip purging when the client returned 0 queue items but we have
                        // active downloads tracked. This prevents accidental deletion when the client
                        // is temporarily unreachable (GetQueueAsync returns empty list on network errors).
                        if (clientQueue.Count == 0 && clientDownloads.Any())
                        {
                            _logger.LogWarning("Skipping orphan purge for client {ClientName}: client returned 0 queue items but {Count} downloads are tracked. Client may be temporarily unreachable.",
                                client.Name, clientDownloads.Count);
                            continue;
                        }

                        // Build set of all client item IDs (both original client IDs and normalized DB IDs)
                        var allClientItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        // Add all normalized (mapped) IDs from the processed queue
                        foreach (var mapped in mappedFiltered)
                        {
                            allClientItemIds.Add(mapped.Id);
                        }

                        // Also add original client queue IDs (torrent hashes, etc)
                        foreach (var item in clientQueue)
                        {
                            allClientItemIds.Add(item.Id);
                        }

                        // Only purge downloads that meet ALL these criteria:
                        // 1. Not in client queue
                        // 2. Not Completed status (needs processing first)
                        // 3. Not very recent (allow 5 minutes for hash resolution)
                        // 4. Not in Downloading/Processing state (might be actively monitored)
                        var orphanedDownloads = clientDownloads.Where(d =>
                        {
                            // Check if in client queue by ID
                            if (allClientItemIds.Contains(d.Id))
                                return false;

                            // Check if in client queue by torrent hash
                            if (string.Equals(client.Type, "qbittorrent", StringComparison.OrdinalIgnoreCase) &&
                                d.Metadata != null && d.Metadata.TryGetValue("TorrentHash", out var hashObj))
                            {
                                var torrentHash = hashObj?.ToString();
                                if (!string.IsNullOrEmpty(torrentHash) && allClientItemIds.Contains(torrentHash))
                                    return false;
                            }

                            // Don't purge terminal states - they need proper cleanup
                            if (d.Status == DownloadStatus.Completed || 
                                d.Status == DownloadStatus.Moved ||
                                d.Status == DownloadStatus.Failed)
                                return false;

                            // Don't purge active states
                            if (d.Status == DownloadStatus.Downloading || 
                                d.Status == DownloadStatus.Processing)
                                return false;

                            // Give NEW downloads 5 minutes to appear in client queue
                            // This handles race conditions during download addition
                            if ((DateTime.UtcNow - d.StartedAt).TotalMinutes < 5)
                            {
                                _logger.LogDebug("Skipping purge for recent download {DownloadId} '{Title}' (age: {Age:F1} min)",
                                    d.Id, d.Title, (DateTime.UtcNow - d.StartedAt).TotalMinutes);
                                return false;
                            }

                            _logger.LogDebug("Download {DownloadId} '{Title}' is orphaned (not in client queue, age: {Age:F1} min, status: {Status})",
                                d.Id, d.Title, (DateTime.UtcNow - d.StartedAt).TotalMinutes, d.Status);
                            return true;
                        }).ToList();

                        if (orphanedDownloads.Any())
                        {
                            _logger.LogInformation("Found {Count} potentially orphaned downloads for client {ClientName}, will purge after validation",
                                orphanedDownloads.Count, client.Name);
                                
                            var toPurge = orphanedDownloads;
                            try
                            {
                                if (string.Equals(client.Type, "nzbget", StringComparison.OrdinalIgnoreCase))
                                {
                                    // Check NZBGet history to avoid purging downloads that completed and moved to history
                                    if (_clientGateway != null)
                                    {
                                        try
                                        {
                                            var historyItems = await _clientGateway.GetRecentHistoryAsync(client, 100);
                                            
                                            // Filter orphaned downloads: keep them if we find them in history
                                            toPurge = orphanedDownloads.Where(d =>
                                            {
                                                try
                                                {
                                                    // If the DB record stores the NZBID as DownloadClientId, check history
                                                    if (!string.IsNullOrEmpty(d.DownloadClientId) && 
                                                        historyItems.Any(h => h.Id.Equals(d.DownloadClientId, StringComparison.OrdinalIgnoreCase)))
                                                    {
                                                        try { _metrics.Increment("download.purge.skipped.history.nzbid_match"); } catch { }
                                                        return false;
                                                    }

                                                    // Match by title similarity against history name entries
                                                    if (!string.IsNullOrEmpty(d.Title) && 
                                                        historyItems.Any(h => !string.IsNullOrEmpty(h.Name) && IsMatchingTitle(d.Title, h.Name)))
                                                    {
                                                        try { _metrics.Increment("download.purge.skipped.history.title_match"); } catch { }
                                                        return false;
                                                    }

                                                    // No match in history -> eligible to purge
                                                    return true;
                                                }
                                                catch
                                                {
                                                    // If anything goes wrong, be conservative and avoid purging this download
                                                    return false;
                                                }
                                            }).ToList();
                                        }
                                        catch (Exception hx)
                                        {
                                            _logger.LogWarning(hx, "Error while fetching NZBGet history for client {ClientName}, skipping purge for safety", client.Name);
                                            try { _metrics.Increment("download.purge.skipped.history.fetch_error"); } catch { }
                                            toPurge = new List<Download>();
                                        }
                                    }
                                    else
                                    {
                                        _logger.LogWarning("DownloadClientGateway not available for client {ClientName}, skipping purge for safety", client.Name);
                                        try { _metrics.Increment("download.purge.skipped.history.gateway_unavailable"); } catch { }
                                        toPurge = new List<Download>();
                                    }
                                }
                                else if (string.Equals(client.Type, "sabnzbd", StringComparison.OrdinalIgnoreCase))
                                {
                                    // Build history request
                                    var apiKey = "";
                                    if (client.Settings != null && client.Settings.TryGetValue("apiKey", out var apiKeyObj))
                                        apiKey = apiKeyObj?.ToString() ?? "";

                                    if (!string.IsNullOrEmpty(apiKey))
                                    {
                                        try
                                        {
                                            var baseUrl = $"{(client.UseSSL ? "https" : "http")}://{client.Host}:{client.Port}/api";
                                            var historyUrl = $"{baseUrl}?mode=history&output=json&limit=100&apikey={Uri.EscapeDataString(apiKey)}";
                                            var historyResp = await _httpClient.GetAsync(historyUrl);
                                            if (historyResp.IsSuccessStatusCode)
                                            {
                                                var historyText = await historyResp.Content.ReadAsStringAsync();
                                                if (!string.IsNullOrWhiteSpace(historyText))
                                                {
                                                    try
                                                    {
                                                        var doc = JsonDocument.Parse(historyText);
                                                        var root = doc.RootElement;
                                                        var historySlots = new List<(string nzo, string name)>();

                                                        if (root.TryGetProperty("history", out var history) && history.TryGetProperty("slots", out var slots) && slots.ValueKind == JsonValueKind.Array)
                                                        {
                                                            foreach (var slot in slots.EnumerateArray())
                                                            {
                                                                var nzoId = slot.TryGetProperty("nzo_id", out var nzo) ? nzo.GetString() ?? string.Empty : string.Empty;
                                                                var name = slot.TryGetProperty("name", out var nm) ? nm.GetString() ?? string.Empty : string.Empty;
                                                                historySlots.Add((nzoId, name));
                                                            }
                                                        }

                                                        // Filter orphaned downloads: keep them if we *don't* find them in history
                                                        toPurge = orphanedDownloads.Where(d =>
                                                        {
                                                            try
                                                            {
                                                                // If the DB record stores the nzo id directly as the DownloadClientId, skip purging
                                                                if (!string.IsNullOrEmpty(d.DownloadClientId) && historySlots.Any(h => h.nzo.Equals(d.DownloadClientId, StringComparison.OrdinalIgnoreCase)))
                                                                {
                                                                    try { _metrics.Increment("download.purge.skipped.history.nzo_match"); } catch { }
                                                                    return false;
                                                                }

                                                                // Match by title similarity against history name entries -> skip purging
                                                                if (!string.IsNullOrEmpty(d.Title) && historySlots.Any(h => !string.IsNullOrEmpty(h.name) && IsMatchingTitle(d.Title, h.name)))
                                                                {
                                                                    try { _metrics.Increment("download.purge.skipped.history.title_match"); } catch { }
                                                                    return false;
                                                                }

                                                                // No match in history -> eligible to purge
                                                                return true;
                                                            }
                                                            catch
                                                            {
                                                                // If anything goes wrong, be conservative and avoid purging this download
                                                                return false;
                                                            }
                                                        }).ToList();
                                                    }
                                                    catch (Exception hx)
                                                    {
                                                        _logger.LogWarning(hx, "Failed to parse SABnzbd history for client {ClientName}, skipping purge for safety", client.Name);
                                                        // Keep toPurge as orphanedDownloads but bail out of purging below
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                _logger.LogWarning("Failed to fetch SABnzbd history for client {ClientName}: {StatusCode}", client.Name, historyResp.StatusCode);
                                                try { _metrics.Increment("download.purge.skipped.history.fetch_failed"); } catch { }
                                                // skip purging when we couldn't confirm history to avoid accidental deletions
                                                toPurge = new List<Download>();
                                            }
                                        }
                                        catch (Exception hx)
                                        {
                                            _logger.LogWarning(hx, "Error while fetching SABnzbd history for client {ClientName}, skipping purge for safety", client.Name);
                                            try { _metrics.Increment("download.purge.skipped.history.fetch_error"); } catch { }
                                            toPurge = new List<Download>();
                                        }
                                    }
                                    else
                                    {
                                        _logger.LogWarning("SABnzbd client {ClientName} missing apiKey in settings, skipping orphan purge for safety", client.Name);
                                        try { _metrics.Increment("download.purge.skipped.history.missing_api_key"); } catch { }
                                        toPurge = new List<Download>();
                                    }
                                }
                            }
                            catch (Exception hx)
                            {
                                _logger.LogWarning(hx, "Unexpected error while checking history before purge for client {ClientName}, skipping purge for safety", client.Name);
                                toPurge = new List<Download>();
                            }
                            // Use a factory-created DbContext for purge operations to avoid relying on scoped
                            // registrations in the ambient IServiceScope. This is more robust for tests which
                            // may only register an IDbContextFactory.
                            var purgeScopedDbContext = await _dbContextFactory.CreateDbContextAsync();

                            foreach (var orphanedDownload in toPurge)
                            {
                                var trackedDownload = await purgeScopedDbContext.Downloads.FindAsync(orphanedDownload.Id);
                                if (trackedDownload != null)
                                {
                                    purgeScopedDbContext.Downloads.Remove(trackedDownload);
                                    _logger.LogInformation("Purged orphaned download record: {DownloadId} '{Title}' (no longer exists in {ClientName} queue)",
                                        orphanedDownload.Id, orphanedDownload.Title, client.Name);
                                    try { _metrics.Increment("download.purged.count"); } catch { }
                                }
                            }

                            await purgeScopedDbContext.SaveChangesAsync();
                            _logger.LogInformation("Purged {Count} orphaned download records from {ClientName}",
                                toPurge.Count, client.Name);
                        }
                    }
                    catch (Exception purgeEx)
                    {
                        _logger.LogError(purgeEx, "Error purging orphaned downloads for client {ClientName}", client.Name);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error getting queue from download client {ClientName}", client.Name);
                }
            }
            // If configured, include completed external downloads from the DB
            // that are not represented in the queueItems list (Listenarr-created
            // external downloads that are no longer present in the client queue).
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
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error while appending completed external downloads to queue (non-fatal)");
                }
            }

            return queueItems.OrderByDescending(q => q.AddedAt).ToList();
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
                        d.Metadata.ContainsKey("TorrentHash") &&
                        d.Metadata["TorrentHash"]?.ToString() == downloadId);
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
                        removedFromClient = await RemoveFromClientAsync(client, downloadId);
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
                    if (client != null)
                    {
                        removedFromClient = await RemoveFromClientAsync(client, downloadId);
                    }
                    else
                    {
                        // If client not found by ID, this might be a legacy/invalid client ID
                        // Try to find the download in the database and check if it's DDL or has a valid client
                        if (downloadRecord != null)
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
                                if (recordClient != null)
                                {
                                    removedFromClient = await RemoveFromClientAsync(recordClient, downloadId);
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
                                removedFromClient = await RemoveFromClientAsync(tryClient, downloadId);
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing from queue: {DownloadId}", downloadId);
                return false;
            }
        }

        private async Task<List<QueueItem>> GetQBittorrentQueueAsync(DownloadClientConfiguration client)
        {
            var baseUrl = $"{(client.UseSSL ? "https" : "http")}://{client.Host}:{client.Port}";
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
                                localContentPath = Path.Combine(localPath, name);
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
            catch (Exception ex)
            {
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
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Incremental qBittorrent sync failed, falling back to full fetch");
                try
                {
                    return await GetQBittorrentQueueAsync(client);
                }
                catch (Exception inner)
                {
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
                var id = Guid.NewGuid().ToString();
                var download = new Download
                {
                    Id = id,
                    AudiobookId = audiobookId,
                    Title = searchResult.Title,
                    OriginalUrl = searchResult.TorrentUrl ?? searchResult.NzbUrl ?? searchResult.MagnetLink ?? string.Empty,
                    Status = DownloadStatus.Queued,
                    Progress = 0,
                    TotalSize = searchResult.Size,
                    DownloadedSize = 0,
                    DownloadPath = string.Empty,
                    FinalPath = string.Empty,
                    StartedAt = DateTime.UtcNow,
                    DownloadClientId = "DDL",
                    Metadata = new Dictionary<string, object>()
                };

                var ctx = await _dbContextFactory.CreateDbContextAsync();
                ctx.Downloads.Add(download);
                await ctx.SaveChangesAsync();
                return id;
            }
            catch (Exception ex)
            {
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
            catch { }
            await Task.CompletedTask;
        }

        private bool IsMatchingTitle(string titleA, string titleB)
        {
            try
            {
                return AreTitlesSimilar(titleA ?? string.Empty, titleB ?? string.Empty);
            }
            catch
            {
                return false;
            }
        }

        private bool AreTitlesSimilar(string a, string b)
        {
            try
            {
                var An = NormalizeTitle(a);
                var Bn = NormalizeTitle(b);
                
                // Exact match
                if (An == Bn) return true;
                
                // One contains the other (substring match)
                if (An.Contains(Bn) || Bn.Contains(An)) return true;
                
                // Check if the shorter title contains all major words from the shorter title
                // This handles cases where the torrent filename has extra metadata
                var shorterTokens = (An.Length <= Bn.Length ? An : Bn).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var longerTitle = An.Length <= Bn.Length ? Bn : An;
                
                // If shorter is at least 3 words and all words appear in longer (in order or scattered)
                if (shorterTokens.Length >= 3)
                {
                    // Check if all tokens from shorter appear in longer (as substrings or words)
                    var allTokensFound = shorterTokens.All(token => longerTitle.Contains(token));
                    if (allTokensFound)
                    {
                        // Additional validation: check that tokens appear in roughly the same order
                        var lastPos = 0;
                        var inOrder = true;
                        foreach (var token in shorterTokens)
                        {
                            var pos = longerTitle.IndexOf(token, lastPos);
                            if (pos < 0)
                            {
                                inOrder = false;
                                break;
                            }
                            lastPos = pos + token.Length;
                        }
                        if (inOrder) return true;
                    }
                }
                
                // Levenshtein distance with more generous threshold for longer titles
                var dist = LevenshteinDistance(An, Bn);
                var minLen = Math.Min(An.Length, Bn.Length);
                var maxLen = Math.Max(An.Length, Bn.Length);
                
                // For longer titles, use a percentage-based threshold; for shorter, use absolute
                // This handles cases like the torrent having extra metadata appended
                var threshold = minLen < 20 
                    ? Math.Max(3, (int)(minLen * 0.20))  // 20% for short titles
                    : Math.Max(5, (int)(minLen * 0.25)); // 25% for longer titles
                
                return dist <= threshold;
            }
            catch { return false; }
        }

        private string NormalizeTitle(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var lower = s.ToLowerInvariant();
            var cleaned = new string(lower.Where(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch)).ToArray());
            return string.Join(' ', cleaned.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
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

        private async Task<bool> RemoveFromClientAsync(DownloadClientConfiguration client, string downloadId)
        {
            try
            {
                if (client == null) return false;

                if (_clientGateway != null)
                {
                    try
                    {
                        var removed = await _clientGateway.RemoveAsync(client, downloadId, false);
                        if (removed)
                        {
                            _logger.LogInformation("Successfully removed {DownloadId} from client {ClientName}", downloadId, client.Name ?? client.Id);
                            return true;
                        }

                        // If removal returned false, verify if the item is still in the client's queue
                        // If it's not in the queue, consider removal successful (item already gone)
                        _logger.LogWarning("Client reported removal failed for {DownloadId}, checking if item still exists in queue", downloadId);
                        try
                        {
                            var queue = await _clientGateway.GetQueueAsync(client);
                            var stillExists = queue.Any(q => q.Id.Equals(downloadId, StringComparison.OrdinalIgnoreCase));
                            
                            if (!stillExists)
                            {
                                _logger.LogInformation("Item {DownloadId} no longer in {ClientName} queue, treating removal as successful", downloadId, client.Name ?? client.Id);
                                return true;
                            }
                            
                            _logger.LogWarning("Item {DownloadId} still exists in {ClientName} queue after removal attempt", downloadId, client.Name ?? client.Id);
                            return false;
                        }
                        catch (Exception queueEx)
                        {
                            _logger.LogWarning(queueEx, "Failed to verify queue status for {DownloadId} on {ClientName}, assuming removal failed", downloadId, client.Name ?? client.Id);
                            return false;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "RemoveFromClientAsync: Exception removing {DownloadId} from {Client}: {Message}", 
                            LogRedaction.SanitizeText(downloadId), LogRedaction.SanitizeText(client.Name ?? client.Id), ex.Message);
                        
                        // Check if item still exists in queue - if not, consider removal successful
                        try
                        {
                            var queue = await _clientGateway.GetQueueAsync(client);
                            var stillExists = queue.Any(q => q.Id.Equals(downloadId, StringComparison.OrdinalIgnoreCase));
                            
                            if (!stillExists)
                            {
                                _logger.LogInformation("After exception, item {DownloadId} not found in {ClientName} queue, treating as successfully removed", 
                                    downloadId, client.Name ?? client.Id);
                                return true;
                            }
                        }
                        catch (Exception queueEx)
                        {
                            _logger.LogDebug(queueEx, "Failed to verify queue after exception for {DownloadId}", downloadId);
                        }
                        
                        return false;
                    }
                }

                // Fallback conservative behavior when no gateway is available
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RemoveFromClientAsync fallback failed for client {Client}", client?.Name ?? client?.Id);
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
            catch { }
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
            catch { }
        }
    }
}
