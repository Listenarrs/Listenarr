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
using System.Text.RegularExpressions;
using Listenarr.Domain.Common;
using Listenarr.Application.Common;
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models;
using Listenarr.Application.Interfaces.Repositories;
using Microsoft.Extensions.Logging;
using Listenarr.Application.Security;

namespace Listenarr.Application.Downloads
{
    public class DownloadService(
        IAudiobookRepository audiobookRepository,
        IConfigurationService configurationService,
        IDownloadRepository downloadRepository,
        IIndexerRepository indexerRepository,
        ILogger<DownloadService> logger,
        IHttpClientFactory httpClientFactory,
        IQualityProfileService qualityProfileService,
        ISearchService searchService,
        IDownloadClientGateway clientGateway,
        IMemoryCache cache,
        IDownloadQueueService downloadQueueService,
        INotificationService notificationService,
        IHubBroadcaster hubBroadcaster,
        IDownloadHistoryService downloadHistoryService) : IDownloadService
    {
        // Cache expiration constants
        private const int QueueCacheExpirationSeconds = 10;
        private const int ClientStatusCacheExpirationSeconds = 30;
        private const int DirectDownloadTimeoutHours = 2;

        private enum EffectiveDownloadType
        {
            Unknown,
            Torrent,
            Usenet,
            DirectDownload
        }

        // Track qBittorrent sync state for incremental updates (clientId -> last rid)
        private readonly Dictionary<string, int> _qbittorrentSyncState = new();

        // Track qBittorrent torrent cache for merging incremental updates (clientId -> (torrentHash -> QueueItem))
        private readonly Dictionary<string, Dictionary<string, QueueItem>> _qbittorrentTorrentCache = new();

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
            var bytes = cache.Get<byte[]>(cacheKey + ":bytes");
            var name = cache.Get<string>(cacheKey + ":name");
            return Task.FromResult((bytes, name));
        }

        /// <summary>
        /// Retrieve cached announce URLs for a given download id if available
        /// </summary>
        public Task<List<string>?> GetCachedAnnouncesAsync(string downloadId)
        {
            try
            {
                if (string.IsNullOrEmpty(downloadId)) return Task.FromResult<List<string>?>(null);
                var cacheKey = $"mam:cachedtorrent:{downloadId}:announces";
                var announces = cache.Get<List<string>>(cacheKey);
                if (announces != null && announces.Count > 0)
                {
                    return Task.FromResult<List<string>?>(announces);
                }

                // Fallback: if announces not cached, try to extract from cached bytes
                var bytes = cache.Get<byte[]>($"mam:cachedtorrent:{downloadId}:bytes");
                if (bytes != null)
                {
                    var extracted = MyAnonamouseHelper.ExtractAnnounceUrls(bytes);
                    if (extracted != null && extracted.Count > 0)
                    {
                        // cache for future retrievals
                        cache.Set($"mam:cachedtorrent:{downloadId}:announces", extracted, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(30) });
                        return Task.FromResult<List<string>?>(extracted);
                    }
                }

                return Task.FromResult<List<string>?>(null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogDebug(ex, "Failed to retrieve cached announces for download {DownloadId} (non-fatal)", downloadId);
                return Task.FromResult<List<string>?>(null);
            }
        }

        public async Task<(bool Success, string Message, DownloadClientConfiguration? Client)> TestDownloadClientAsync(DownloadClientConfiguration client)
        {
            if (client == null)
            {
                return (false, "Download client configuration not provided", null);
            }

            try
            {
                var (success, message) = await clientGateway.TestConnectionAsync(client);
                return (success, message, client);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Error during TestDownloadClientAsync for client {ClientId}", LogRedaction.SanitizeText(client.Id ?? client.Name ?? client.Type));
                return (false, ex.Message, client);
            }
        }

        public async Task<string?> ReprocessDownloadAsync(string downloadId)
        {
            logger.LogInformation("ReprocessDownloadAsync called for {DownloadId}", LogRedaction.SanitizeText(downloadId));

            // Placeholder: return null to indicate no job was created.
            // Concrete implementation should enqueue a reprocess job and return its ID.
            return await Task.FromResult<string?>(null);
        }

        public async Task<List<ReprocessResult>> ReprocessDownloadsAsync(List<string> downloadIds)
        {
            logger.LogInformation("ReprocessDownloadsAsync called for {Count} downloads", downloadIds?.Count ?? 0);

            // Placeholder implementation: return empty results list.
            // A full implementation should iterate downloadIds and invoke reprocessing,
            // collecting per-download results.
            return await Task.FromResult(new List<ReprocessResult>());
        }

        public async Task<List<ReprocessResult>> ReprocessAllCompletedDownloadsAsync(bool includeProcessed = false, TimeSpan? maxAge = null)
        {
            logger.LogInformation("ReprocessAllCompletedDownloadsAsync called includeProcessed={IncludeProcessed}, maxAge={MaxAge}", includeProcessed, maxAge);

            // Placeholder implementation: no-op and return empty list.
            // Full implementation should query completed downloads, apply filters and enqueue reprocess jobs.
            return await Task.FromResult(new List<ReprocessResult>());
        }

        public async Task<SearchAndDownloadResult> SearchAndDownloadAsync(int audiobookId)
        {
            // Get the audiobook
            var audiobook = await audiobookRepository.GetByIdAsync(audiobookId);
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
                logger.LogWarning("Audiobook '{Title}' has no quality profile assigned", audiobook.Title);
                return new SearchAndDownloadResult
                {
                    Success = false,
                    Message = "Audiobook has no quality profile assigned"
                };
            }

            // Build search query from audiobook metadata
            var searchQuery = BuildSearchQuery(audiobook);
            logger.LogInformation("Searching for audiobook '{Title}' with query: {Query}", LogRedaction.SanitizeText(audiobook.Title), LogRedaction.SanitizeText(searchQuery));

            // Search using the working search service. This is an automatic search (triggered
            // by the background/manual 'search-and-download' endpoint), so set isAutomaticSearch
            // to true to ensure only indexers are queried (no Amazon/Audible scraping).
            var searchResults = await searchService.SearchAsync(searchQuery, isAutomaticSearch: true);

            if (searchResults == null || !searchResults.Any())
            {
                return new SearchAndDownloadResult
                {
                    Success = false,
                    Message = "No search results found"
                };
            }

            // Score results against quality profile
            var scoredResults = await qualityProfileService.ScoreSearchResults(searchResults, audiobook.QualityProfile);

            // Log all scored results for debugging
            logger.LogInformation("Scored {Count} search results for audiobook '{Title}':", scoredResults.Count, LogRedaction.SanitizeText(audiobook.Title));
            foreach (var scoredResult in scoredResults.OrderByDescending(s => s.TotalScore))
            {
                var status = scoredResult.IsRejected ? "REJECTED" : (scoredResult.TotalScore > 0 ? "ACCEPTABLE" : "LOW SCORE");
                logger.LogInformation("  [{Status}] Score: {Score} | Title: {Title} | Source: {Source} | Size: {Size}MB | Seeders: {Seeders} | Quality: {Quality}",
                    status, scoredResult.TotalScore, LogRedaction.SanitizeText(scoredResult.SearchResult.Title), LogRedaction.SanitizeText(scoredResult.SearchResult.Source),
                    scoredResult.SearchResult.Size / 1024 / 1024, scoredResult.SearchResult.Seeders, scoredResult.SearchResult.Quality);
                if (scoredResult.IsRejected && scoredResult.RejectionReasons.Any())
                {
                    logger.LogInformation("    Rejection reasons: {Reasons}", string.Join(", ", scoredResult.RejectionReasons));
                }
            }

            // Only consider non-rejected, score > 0 results
            var topResult = scoredResults
                .Where(s => !s.IsRejected && s.TotalScore > 0)
                .OrderByDescending(s => s.TotalScore)
                .FirstOrDefault();

            if (topResult == null)
            {
                logger.LogWarning("No acceptable search results found for audiobook '{Title}' after quality filtering", audiobook.Title);
                return new SearchAndDownloadResult
                {
                    Success = false,
                    Message = "No acceptable search results found"
                };
            }

            // Assign score to SearchResult
            topResult.SearchResult.Score = topResult.TotalScore;

            var effectiveDownloadType = await ResolveEffectiveDownloadTypeAsync(topResult.SearchResult);
            topResult.SearchResult.DownloadType = GetDownloadTypeLabel(effectiveDownloadType);

            if (effectiveDownloadType == EffectiveDownloadType.Unknown)
            {
                logger.LogWarning(
                    "Top search result for audiobook '{Title}' could not be mapped to a trusted download type",
                    audiobook.Title);
                return new SearchAndDownloadResult
                {
                    Success = false,
                    Message = "Top search result could not be mapped to a valid download target"
                };
            }

            // Handle trusted direct-download results directly
            if (effectiveDownloadType == EffectiveDownloadType.DirectDownload)
            {
                logger.LogInformation("Top result is DDL, processing directly for: {Title}", topResult.SearchResult.Title);
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
            var isTorrent = effectiveDownloadType == EffectiveDownloadType.Torrent;
            var downloadClientId = await GetAppropriateDownloadClient(isTorrent);

            if (downloadClientId == null)
            {
                logger.LogWarning("No suitable download client found for type: {Type}", isTorrent ? "Torrent" : "NZB");
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
            logger.LogInformation("SendToDownloadClientAsync called - Title: {Title}, DownloadType: '{DownloadType}', TorrentUrl: {TorrentUrl}, AudiobookId: {AudiobookId}",
                searchResult.Title,
                searchResult.DownloadType ?? "(null)",
                searchResult.TorrentUrl ?? "(null)",
                audiobookId);

            var effectiveDownloadType = await ResolveEffectiveDownloadTypeAsync(searchResult);
            searchResult.DownloadType = GetDownloadTypeLabel(effectiveDownloadType);

            if (effectiveDownloadType == EffectiveDownloadType.Unknown)
            {
                throw new InvalidOperationException("Unable to determine a trusted download type from the selected search result.");
            }

            // Check if this is a trusted direct download and handle it differently
            if (effectiveDownloadType == EffectiveDownloadType.DirectDownload)
            {
                logger.LogInformation("Processing DDL for: {Title}, AudiobookId: {AudiobookId}", searchResult.Title, audiobookId);
                return await DownloadDirectlyAsync(searchResult, audiobookId);
            }

            var isTorrent = effectiveDownloadType == EffectiveDownloadType.Torrent;

            logger.LogInformation(
                "Processing as {DownloadType} after server-side validation for '{Title}'",
                searchResult.DownloadType,
                searchResult.Title);

            if (downloadClientId == null)
            {
                downloadClientId = await GetAppropriateDownloadClient(isTorrent);

                if (downloadClientId == null)
                {
                    var clientType = isTorrent ? "torrent" : "NZB";
                    var neededClients = isTorrent ? "qBittorrent or Transmission" : "SABnzbd or NZBGet";
                    throw new Exception($"No suitable download client found for {clientType}. Please configure and enable a {clientType} client ({neededClients}) in Settings.");
                }

                logger.LogInformation("Auto-selected download client {ClientId} for {ClientType}", downloadClientId, isTorrent ? "torrent" : "NZB");
            }

            var downloadClient = await configurationService.GetDownloadClientConfigurationAsync(downloadClientId);
            if (downloadClient == null || !downloadClient.IsEnabled)
            {
                throw new Exception("Download client not found or disabled");
            }

            logger.LogInformation("Sending to {ClientType} download client: {ClientName}", downloadClient.Type, downloadClient.Name);

            var downloadId = Guid.NewGuid().ToString();

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
                    var downloadClients = await configurationService.GetDownloadClientConfigurationsAsync();
                    var enabledClientIds = downloadClients
                        .Where(c => c.IsEnabled && !string.IsNullOrWhiteSpace(c.Id))
                        .Select(c => c.Id)
                        .ToHashSet();

                    var allDownloads = await downloadRepository.GetAllAsync();
                    var existingActive = allDownloads
                        .Any(d => d.AudiobookId == audiobookIdValue &&
                                  (d.Status == DownloadStatus.Queued ||
                                   d.Status == DownloadStatus.Downloading ||
                                   d.Status == DownloadStatus.ImportPending) &&
                                  (d.DownloadClientId == "DDL" ||
                                   (!string.IsNullOrEmpty(d.DownloadClientId) && enabledClientIds.Contains(d.DownloadClientId))));

                    if (existingActive)
                    {
                        logger.LogInformation(
                            "Skipping duplicate download for audiobook {AudiobookId} — an active download already exists. Title: '{Title}'",
                            audiobookIdValue, searchResult.Title);
                        return string.Empty;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    logger.LogDebug(ex, "Failed to check for duplicate downloads for audiobook {AudiobookId} (non-blocking)", audiobookIdValue);
                }
            }

            // Create Download record in database before sending to client
            var download = new Download
            {
                Id = downloadId,
                AudiobookId = audiobookId,
                Title = searchResult.Title ?? string.Empty,
                Artist = searchResult.Artist ?? string.Empty,
                Album = searchResult.Album ?? string.Empty,
                Language = searchResult.Language,
                OriginalUrl = !string.IsNullOrEmpty(searchResult.MagnetLink) ? searchResult.MagnetLink : (searchResult.TorrentUrl ?? searchResult.NzbUrl ?? string.Empty),
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
                    ["Language"] = searchResult.Language ?? string.Empty,
                    ["DownloadType"] = searchResult.DownloadType
                }
            };

            await downloadRepository.AddAsync(download);
            logger.LogInformation("Created download record in database: {DownloadId} for '{Title}'", downloadId, searchResult.Title);

            // Record in download history for idempotency tracking
            if (!string.IsNullOrEmpty(downloadClientIdForModel))
            {
                try
                {
                    var protocol = isTorrent ? DownloadProtocol.Torrent : DownloadProtocol.Usenet;
                    await downloadHistoryService.RecordGrabbedAsync(
                        downloadId,
                        downloadClientIdForModel,
                        searchResult.Title ?? "Unknown",
                        protocol);
                    logger.LogInformation("Recorded grabbed event in history for download {DownloadId}", downloadId);
                }
                catch (Exception histEx) when (histEx is not OperationCanceledException && histEx is not OutOfMemoryException && histEx is not StackOverflowException)
                {
                    logger.LogWarning(histEx, "Failed to record grabbed event in history for download {DownloadId} (non-critical)", downloadId);
                }
            }

            // Attempt to cache MyAnonamouse torrents ahead of handing off to qBittorrent
            await TryPrepareMyAnonamouseTorrentAsync(searchResult, downloadId);

            if (clientGateway == null)
            {
                throw new InvalidOperationException("Download client gateway is not registered. Ensure AddListenarrAdapters() is invoked during startup.");
            }

            // Route to appropriate client handler via adapter and capture client-specific IDs when provided
            string? clientSpecificId = await clientGateway.AddAsync(downloadClient, searchResult);
            clientSpecificId ??= TryResolveClientSpecificIdFallback(downloadClient, searchResult);

            // Update download record with client-specific ID if available
            if (!string.IsNullOrEmpty(clientSpecificId))
            {
                var downloadToUpdate = await downloadRepository.FindAsync(downloadId);
                if (downloadToUpdate != null)
                {
                    if (downloadToUpdate.Metadata == null)
                        downloadToUpdate.Metadata = new Dictionary<string, object>();

                    downloadToUpdate.Metadata["ClientDownloadId"] = clientSpecificId;

                    if (downloadClient.Type.Equals("qbittorrent", StringComparison.OrdinalIgnoreCase) ||
                        downloadClient.Type.Equals("transmission", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadToUpdate.Metadata["TorrentHash"] = clientSpecificId;
                    }

                    await UpdateAsync(downloadToUpdate);
                    logger.LogInformation("Updated download {DownloadId} with client-specific ID: {ClientId}", downloadId, clientSpecificId);
                }
            }

            var settings = await configurationService.GetApplicationSettingsAsync();

            // Fetch audiobook data if available for better notification content
            object notificationData;
            if (audiobookId.HasValue)
            {
                var audiobook = await audiobookRepository.GetByIdAsync(audiobookId.Value);
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
                        downloadId = downloadId,
                        source = searchResult.Source ?? "Unknown Source",
                        downloadClient = downloadClient.Name ?? "Unknown Client",
                        size = searchResult.Size
                    }
                    : new
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

            await notificationService.SendNotificationAsync("book-downloading", notificationData, settings.WebhookUrl, settings.EnabledNotificationTriggers);

            // Trigger an immediate realtime queue update so the UI shows the new download right away
            // Add a small delay to allow the download client to process and index the new download
            try
            {
                logger.LogInformation("Waiting briefly for download client to process new download...");
                await Task.Delay(1500); // Give qBittorrent/other clients time to index the torrent

                logger.LogInformation("Triggering immediate queue update after sending download to client");
                var currentQueueSnapshot = await downloadQueueService.GetQueueSnapshotAsync();
                await hubBroadcaster.BroadcastQueueUpdateAsync(currentQueueSnapshot);
                logger.LogInformation("Immediate queue update sent with {Count} items via IHubBroadcaster", currentQueueSnapshot?.Items?.Count ?? 0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Failed to trigger immediate queue update (non-fatal)");
            }

            return downloadId;
        }

        private async Task TryPrepareMyAnonamouseTorrentAsync(SearchResult searchResult, string? downloadId = null)
        {
            ArgumentNullException.ThrowIfNull(searchResult);

            logger.LogInformation("TryPrepareMyAnonamouseTorrentAsync called for '{Title}', IndexerId: {IndexerId}, TorrentUrl: '{TorrentUrl}'",
                searchResult.Title, searchResult.IndexerId, searchResult.TorrentUrl);

            // Security: Validate all preconditions before performing sensitive operations
            // This method downloads content using authenticated HTTP clients, so we must
            // ensure the request is legitimate and comes from a trusted, configured source.

            if (searchResult.IndexerId == null)
            {
                logger.LogWarning("TryPrepareMyAnonamouseTorrentAsync: No IndexerId for '{Title}' - skipping", searchResult.Title);
                // Reject: No database-backed indexer ID provided
                return;
            }

            if (string.IsNullOrEmpty(searchResult.TorrentUrl))
            {
                logger.LogDebug("Skipping MyAnonamouse cache: no TorrentUrl for '{Title}'", LogRedaction.SanitizeText(searchResult.Title));
                return;
            }

            if (searchResult.TorrentFileContent != null && searchResult.TorrentFileContent.Length > 0)
            {
                logger.LogDebug("MyAnonamouse torrent already cached for '{Title}'", searchResult.Title);
                return;
            }

            try
            {
                // Security: Fetch indexer from database using the validated ID
                // Only trusted, administrator-configured indexers can trigger authenticated requests
                var indexer = await indexerRepository.GetByIdAsync(searchResult.IndexerId.Value);

                // Security: Indexer must exist in database - reject if not found
                if (indexer == null)
                {
                    logger.LogWarning("Unable to cache MyAnonamouse torrent for '{Title}': indexer configuration not found", searchResult.Title);
                    return;
                }

                // Security: Validate against database-stored indexer configuration, not user-provided search result
                if (!string.Equals(indexer.Implementation, "MyAnonamouse", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogDebug("Skipping MyAnonamouse cache: indexer {IndexerName} is not MyAnonamouse (is {Implementation})",
                        indexer.Name, indexer.Implementation);
                    return;
                }

                // Parse and validate URLs
                if (!Uri.TryCreate(searchResult.TorrentUrl, UriKind.Absolute, out var torrentUri) ||
                    !Uri.TryCreate(indexer.Url, UriKind.Absolute, out var indexerUri))
                {
                    logger.LogWarning("Unable to cache MyAnonamouse torrent for '{Title}': invalid URL(s). Torrent={Url}, Indexer={IndexerUrl}", searchResult.Title, LogRedaction.SanitizeUrl(searchResult.TorrentUrl), indexer.Url);
                    return;
                }

                if (!string.Equals(torrentUri.Host, indexerUri.Host, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogDebug("MyAnonamouse torrent host {TorrentHost} differs from indexer host {IndexerHost}. Proceeding with explicit cookie header.", torrentUri.Host, indexerUri.Host);
                }

                var mamId = MyAnonamouseHelper.TryGetMamId(indexer.AdditionalSettings);
                if (string.IsNullOrEmpty(mamId))
                {
                    logger.LogWarning("Unable to cache MyAnonamouse torrent for '{Title}': mam_id missing from indexer {IndexerName}", searchResult.Title, indexer.Name);
                    return;
                }

                // Use factory client for the initial attempt (allows test injection).
                // If auto-redirect drops the Cookie header, a fallback retry with
                // CreateAuthenticatedHttpClient (AllowAutoRedirect=false) handles it below.
                var httpClientToUse = httpClientFactory.CreateClient(); // FIXME: Should use a named client

                logger.LogDebug("Downloading MyAnonamouse torrent for '{Title}' from {Url}", searchResult.Title, LogRedaction.SanitizeUrl(searchResult.TorrentUrl));

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

                    logger.LogDebug("Downloading MyAnonamouse torrent for '{Title}' from {Url} (attempt {Attempt})", searchResult.Title, LogRedaction.SanitizeUrl(currentUri.ToString()), redirectAttempt + 1);

                    response = await httpClientToUse.SendAsync(req);

                    // Persist mam_id from intermediate responses (Set-Cookie)
                    try
                    {
                        var newMam = MyAnonamouseHelper.TryExtractMamIdFromResponse(response);
                        if (!string.IsNullOrEmpty(newMam) && !string.Equals(newMam, mamId, StringComparison.Ordinal))
                        {
                            logger.LogInformation("MyAnonamouse: received updated mam_id from download redirect response for indexer {Name}", indexer.Name);
                            indexer.AdditionalSettings = MyAnonamouseHelper.UpdateMamIdInAdditionalSettings(indexer.AdditionalSettings, newMam);
                            await indexerRepository.UpdateAsync(indexer);

                            // Keep local copy in sync
                            indexer.AdditionalSettings = MyAnonamouseHelper.UpdateMamIdInAdditionalSettings(indexer.AdditionalSettings, newMam);
                            mamId = newMam;
                        }
                    }
                    catch (Exception exMam) when (exMam is not OperationCanceledException && exMam is not OutOfMemoryException && exMam is not StackOverflowException)
                    {
                        logger.LogDebug(exMam, "Failed to persist updated mam_id from MyAnonamouse redirect response");
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
                            logger.LogWarning("MyAnonamouse torrent download redirect without Location header for '{Title}'", searchResult.Title);
                            response.Dispose();
                            return;
                        }

                        var next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(currentUri, response.Headers.Location);
                        logger.LogDebug("Following MyAnonamouse redirect to {Next}", LogRedaction.SanitizeUrl(next.ToString()));
                        response.Dispose();
                        currentUri = next;
                        continue;
                    }

                    // Not a redirect - break to process the response
                    break;
                }

                if (response == null)
                {
                    logger.LogWarning("Failed to download MyAnonamouse torrent for '{Title}': no response", searchResult.Title);
                    return;
                }

                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("MyAnonamouse torrent download failed for '{Title}' with status {Status}", searchResult.Title, response.StatusCode);
                    response.Dispose();
                    return;
                }

                var torrentBytes = await response.Content.ReadAsByteArrayAsync();
                if (torrentBytes == null || torrentBytes.Length == 0)
                {
                    logger.LogWarning("MyAnonamouse torrent download for '{Title}' returned empty payload", searchResult.Title);
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
                    logger.LogDebug("Factory client returned non-torrent payload for '{Title}', retrying with authenticated MAM client", searchResult.Title);
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
                                logger.LogInformation("Authenticated MAM client successfully downloaded torrent for '{Title}' ({Bytes} bytes)", searchResult.Title, torrentBytes!.Length);
                        }
                    }
                    catch (Exception retryEx) when (retryEx is not OperationCanceledException && retryEx is not OutOfMemoryException && retryEx is not StackOverflowException)
                    {
                        logger.LogDebug(retryEx, "Retry with authenticated MAM client also failed (non-fatal)");
                    }
                }

                if (!looksLikeTorrent)
                {
                    var snippet = System.Text.Encoding.UTF8.GetString((torrentBytes ?? Array.Empty<byte>()).Take(Math.Min(512, torrentBytes?.Length ?? 0)).ToArray());
                    if (System.Text.RegularExpressions.Regex.IsMatch(snippet, "Unrecognized host|PassKey|Pass Key|Unrecognized", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        logger.LogWarning("MyAnonamouse torrent download for '{Title}' returned an authorization error page from tracker: {Snippet}", searchResult.Title, LogRedaction.RedactText(snippet, LogRedaction.GetSensitiveValuesFromEnvironment()));
                    }
                    else
                    {
                        logger.LogWarning("MyAnonamouse torrent download for '{Title}' returned unexpected non-torrent payload (first 200 chars): {Snippet}", searchResult.Title, LogRedaction.RedactText(snippet, LogRedaction.GetSensitiveValuesFromEnvironment()));
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
                logger.LogDebug("MyAnonamouse torrent payload debug: ContentType={ContentType}, FirstBytes={FirstBytesHex}, ContainsAnnounce={ContainsAnnounce}", contentType, firstBytesHex, containsAnnounce);

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
                                logger.LogInformation("Rewrote torrent tracker host from {OldHost} to {NewHost} for '{Title}'", torrentUri.Host, indexerUri.Host, searchResult.Title);
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
                                    logger.LogInformation("Rewrote torrent IP host {Ip} to indexer host {Host} for '{Title}'", ip, indexerUri.Host, searchResult.Title);
                                }
                            }
                        }
                        catch (Exception rex2) when (rex2 is not OperationCanceledException && rex2 is not OutOfMemoryException && rex2 is not StackOverflowException)
                        {
                            logger.LogDebug(rex2, "Failed to rewrite numeric IPs inside torrent (non-fatal)");
                        }

                        // 3) Log announce URLs for diagnostics — do NOT rewrite legitimate tracker subdomains
                        //    (e.g., t.myanonamouse.net is the actual tracker and must not be changed to www.myanonamouse.net)
                        try
                        {
                            var announces = MyAnonamouseHelper.ExtractAnnounceUrls(torrentBytes);
                            if (announces != null && announces.Count > 0)
                            {
                                logger.LogDebug("Torrent announce URLs for '{Title}': {Announces}", searchResult.Title, string.Join(", ", announces.Distinct()));
                            }
                        }
                        catch (Exception rex3) when (rex3 is not OperationCanceledException && rex3 is not OutOfMemoryException && rex3 is not StackOverflowException)
                        {
                            logger.LogDebug(rex3, "Failed to extract announce URLs from torrent (non-fatal)");
                        }
                    }
                }
                catch (Exception rex) when (rex is not OperationCanceledException && rex is not OutOfMemoryException && rex is not StackOverflowException)
                {
                    logger.LogDebug(rex, "Failed to rewrite torrent tracker hosts (non-fatal)");
                }

                // If we have a mam_id, attempt to append it to any announce URLs inside the torrent so trackers that rely on passkey in query will accept it.
                try
                {
                    if (!string.IsNullOrEmpty(mamId))
                    {
                        var normalizedMamId = MyAnonamouseHelper.NormalizeMamId(mamId);
                        logger.LogInformation("MyAnonamouse: normalizing mam_id from '{Raw}' to '{Normalized}' for '{Title}'", LogRedaction.RedactText(mamId, LogRedaction.GetSensitiveValuesFromEnvironment()), LogRedaction.RedactText(normalizedMamId, LogRedaction.GetSensitiveValuesFromEnvironment()), searchResult.Title);

                        var currentAnnounces = MyAnonamouseHelper.ExtractAnnounceUrls(torrentBytes);
                        var updatedAnnounces = new List<string>();
                        var modified = false;

                        foreach (var ann in (currentAnnounces ?? new List<string>())
                            .Where(ann => !string.IsNullOrWhiteSpace(ann))
                            .Distinct())
                        {
                            // Only append mam_id to actual tracker announce URLs, not file/web-seed URLs
                            if (!ann.Contains("/announce", StringComparison.OrdinalIgnoreCase) && !ann.Contains("/tracker", StringComparison.OrdinalIgnoreCase))
                            {
                                logger.LogDebug("Skipping non-tracker URL for mam_id append: {Url}", ann);
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
                            catch (Exception inner) when (inner is not OperationCanceledException && inner is not OutOfMemoryException && inner is not StackOverflowException)
                            {
                                logger.LogDebug(inner, "Non-fatal failure while attempting to append mam_id to announce {Ann} for '{Title}'", ann, searchResult.Title);
                                updatedAnnounces.Add(ann);
                            }
                        }

                        if (modified)
                            logger.LogInformation("Appended mam_id to MyAnonamouse announce URLs for '{Title}' - count={Count}", searchResult.Title, updatedAnnounces.Count);
                    }
                }
                catch (Exception exAppend) when (exAppend is not OperationCanceledException && exAppend is not OutOfMemoryException && exAppend is not StackOverflowException)
                {
                    logger.LogDebug(exAppend, "Failed to append mam_id to MyAnonamouse announces (non-fatal)");
                }

                searchResult.TorrentFileContent = torrentBytes;
                searchResult.TorrentFileName = response != null ? MyAnonamouseHelper.ResolveTorrentFileName(response, searchResult.TorrentUrl) : "myanonamouse.torrent";
                logger.LogInformation("Cached MyAnonamouse torrent for '{Title}' ({Bytes} bytes)", searchResult.Title, torrentBytes.Length);

                // If a downloadId was provided, store the cached torrent (bytes + filename) to the in-memory cache so it can be retrieved for diagnostics.
                if (!string.IsNullOrEmpty(downloadId))
                {
                    try
                    {
                        var cacheKey = $"mam:cachedtorrent:{downloadId}";
                        cache.Set(cacheKey + ":bytes", torrentBytes, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(30) });
                        cache.Set(cacheKey + ":name", searchResult.TorrentFileName ?? "download.torrent", new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(30) });
                        logger.LogInformation("Cached MyAnonamouse torrent bytes and filename to memory for download {DownloadId}", downloadId);
                    }
                    catch (Exception cex) when (cex is not OperationCanceledException && cex is not OutOfMemoryException && cex is not StackOverflowException)
                    {
                        logger.LogDebug(cex, "Failed to place cached MyAnonamouse torrent into memory cache (non-fatal)");
                    }
                }
                try
                {
                    var announces = MyAnonamouseHelper.ExtractAnnounceUrls(torrentBytes);
                    var count = announces?.Count ?? 0;
                    var unique = count > 0 ? string.Join(", ", announces?.Take(10) ?? Enumerable.Empty<string>()) : "(none)";
                    logger.LogInformation("Cached MyAnonamouse torrent announces for '{Title}' - count={Count}: {Announces}", searchResult.Title, count, LogRedaction.RedactText(unique, LogRedaction.GetSensitiveValuesFromEnvironment()));

                    // Also cache the extracted announce URLs for quick retrieval by diagnostics endpoints
                    if (!string.IsNullOrEmpty(downloadId) && announces != null && announces.Count > 0)
                    {
                        try
                        {
                            var cacheKey = $"mam:cachedtorrent:{downloadId}";
                            cache.Set(cacheKey + ":announces", announces, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(30) });
                            logger.LogInformation("Cached MyAnonamouse torrent announces to memory for download {DownloadId}", downloadId);
                        }
                        catch (Exception cexAnn) when (cexAnn is not OperationCanceledException && cexAnn is not OutOfMemoryException && cexAnn is not StackOverflowException)
                        {
                            logger.LogDebug(cexAnn, "Failed to place cached MyAnonamouse announces into memory cache (non-fatal)");
                        }
                    }
                }
                catch (Exception exAnn) when (exAnn is not OperationCanceledException && exAnn is not OutOfMemoryException && exAnn is not StackOverflowException)
                {
                    logger.LogDebug(exAnn, "Failed to extract announce URLs from cached torrent (non-fatal)");
                }
                response?.Dispose();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Failed to cache MyAnonamouse torrent for '{Title}'", searchResult.Title);
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

        private async Task<EffectiveDownloadType> ResolveEffectiveDownloadTypeAsync(SearchResult result)
        {
            ArgumentNullException.ThrowIfNull(result);

            if (!string.IsNullOrWhiteSpace(result.NzbUrl))
            {
                logger.LogDebug("Result identified as Usenet from NzbUrl: {Title}", result.Title);
                return EffectiveDownloadType.Usenet;
            }

            if (!string.IsNullOrWhiteSpace(result.MagnetLink))
            {
                logger.LogDebug("Result identified as Torrent from MagnetLink: {Title}", result.Title);
                return EffectiveDownloadType.Torrent;
            }

            if (result.TorrentFileContent != null && result.TorrentFileContent.Length > 0)
            {
                logger.LogDebug("Result identified as Torrent from cached torrent bytes: {Title}", result.Title);
                return EffectiveDownloadType.Torrent;
            }

            if (await IsTrustedDirectDownloadAsync(result))
            {
                logger.LogDebug("Result identified as trusted DDL from configured Internet Archive indexer: {Title}", result.Title);
                return EffectiveDownloadType.DirectDownload;
            }

            if (DownloadClientUriBuilder.TryParseHttpOrHttpsAbsoluteUri(result.TorrentUrl, out _))
            {
                logger.LogDebug("Result identified as Torrent from TorrentUrl: {Title}", result.Title);
                return EffectiveDownloadType.Torrent;
            }

            logger.LogWarning(
                "Unable to derive effective download type for '{Title}'. Incoming DownloadType '{DownloadType}' was ignored because no trusted download target was present.",
                result.Title,
                result.DownloadType ?? "(null)");

            return EffectiveDownloadType.Unknown;
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
                var indexer = await indexerRepository.GetByIdAsync(indexerId);

                if (indexer == null || !indexer.IsEnabled)
                {
                    logger.LogDebug(
                        "Direct-download validation rejected '{Title}': indexer {IndexerId} was missing or disabled",
                        result.Title,
                        indexerId);
                    return false;
                }

                if (!string.Equals(indexer.Implementation, "InternetArchive", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogDebug(
                        "Direct-download validation rejected '{Title}': indexer {IndexerId} implementation was {Implementation}",
                        result.Title,
                        indexerId,
                        indexer.Implementation);
                    return false;
                }

                if (!Uri.TryCreate(indexer.Url, UriKind.Absolute, out var indexerUri) ||
                    !IsTrustedArchiveOrgHost(indexerUri))
                {
                    logger.LogDebug(
                        "Direct-download validation rejected '{Title}': configured indexer URL '{IndexerUrl}' is not a trusted archive.org host",
                        result.Title,
                        indexer.Url);
                    return false;
                }

                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(
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

        private static string GetDownloadTypeLabel(EffectiveDownloadType effectiveDownloadType)
        {
            return effectiveDownloadType switch
            {
                EffectiveDownloadType.Torrent => "Torrent",
                EffectiveDownloadType.Usenet => "Usenet",
                EffectiveDownloadType.DirectDownload => "DDL",
                _ => string.Empty
            };
        }

        private bool IsTorrentResult(SearchResult result)
        {
            // Use transport indicators only. Do not trust caller-provided DownloadType.
            if (!string.IsNullOrEmpty(result.NzbUrl))
            {
                logger.LogDebug("Result identified as NZB (has NzbUrl): {Title}", result.Title);
                return false;
            }

            if (result.TorrentFileContent != null && result.TorrentFileContent.Length > 0)
            {
                logger.LogDebug("Result identified as Torrent (has cached torrent bytes): {Title}", result.Title);
                return true;
            }

            if (!string.IsNullOrEmpty(result.MagnetLink) ||
                DownloadClientUriBuilder.TryParseHttpOrHttpsAbsoluteUri(result.TorrentUrl, out _))
            {
                logger.LogDebug("Result identified as Torrent (has MagnetLink or TorrentUrl): {Title}", result.Title);
                return true;
            }

            // If neither is set, we can't reliably determine the type
            // Log a warning and default to false (NZB) as a safer choice
            logger.LogWarning("Unable to determine result type for '{Title}' from source '{Source}'. No MagnetLink, TorrentUrl, or NzbUrl found. Defaulting to NZB.",
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
            var downloadClients = await configurationService.GetDownloadClientConfigurationsAsync();
            var enabledClients = downloadClients.Where(c => c.IsEnabled).ToList();

            logger.LogInformation("Looking for {ClientType} client. Found {Count} enabled download clients: {Clients}",
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
                    logger.LogInformation("Selected torrent client: {ClientName} ({ClientType})", client.Name, client.Type);
                }
                else
                {
                    logger.LogWarning("No torrent client (qBittorrent or Transmission) found among enabled clients");
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
                    logger.LogInformation("Selected NZB client: {ClientName} ({ClientType})", client.Name, client.Type);
                }
                else
                {
                    logger.LogWarning("No NZB client (SABnzbd or NZBGet) found among enabled clients");
                }

                return client?.Id;
            }
        }

        public async Task<bool> RemoveFromQueueAsync(string downloadId, string? downloadClientId = null, bool force = false)
        {
            try
            {
                bool removedFromClient = false;
                Download? downloadRecord = null;

                // Try to find by direct ID match first
                downloadRecord = await downloadRepository.FindAsync(downloadId);

                // If not found, try to find by client-specific ID (e.g., torrent hash)
                if (downloadRecord == null)
                {
                    var allDownloads = await downloadRepository.GetAllAsync();
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
                    var client = await configurationService.GetDownloadClientConfigurationAsync(downloadClientId);
                    if (client != null)
                    {
                        var queue = await downloadQueueService.GetQueueAsync();
                        var queueItem = queue.FirstOrDefault(q => q.Id == downloadId && q.DownloadClientId == downloadClientId);

                        if (queueItem != null)
                        {
                            var clientDownloads = await downloadRepository.GetByClientAsync(downloadClientId);
                            downloadRecord = clientDownloads.FirstOrDefault(d => TitleUtils.IsMatchingTitle(d.Title, queueItem.Title));
                        }
                    }
                }

                // If force=true, skip client removal and just remove from database
                if (force)
                {
                    logger.LogWarning("Force removal requested for {DownloadId}, skipping client removal", downloadId);
                    removedFromClient = true;
                }
                else if (downloadClientId == null)
                {
                    // Try all clients to find and remove the item
                    var downloadClients = await configurationService.GetDownloadClientConfigurationsAsync();
                    var enabledClients = downloadClients.Where(c => c.IsEnabled).ToList();

                    foreach (var client in enabledClients)
                    {
                        removedFromClient = await RemoveFromClientAsync(client, downloadId, downloadRecord);
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
                    var client = await configurationService.GetDownloadClientConfigurationAsync(downloadClientId);
                    if (client != null && !client.IsEnabled)
                    {
                        logger.LogInformation("Skipping removal of {DownloadId} from disabled client {ClientName}", downloadId, client.Name);
                    }
                    else if (client != null)
                    {
                        removedFromClient = await RemoveFromClientAsync(client, downloadId, downloadRecord);
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
                                logger.LogInformation("Download {DownloadId} is DDL, skipping external client removal", downloadId);
                            }
                            else if (!string.IsNullOrEmpty(downloadRecord.DownloadClientId))
                            {
                                // Try with the download record's client ID
                                var recordClient = await configurationService.GetDownloadClientConfigurationAsync(downloadRecord.DownloadClientId);
                                if (recordClient != null && !recordClient.IsEnabled)
                                {
                                    logger.LogInformation("Skipping removal of {DownloadId} from disabled client {ClientName}", downloadId, recordClient.Name);
                                    removedFromClient = true; // Treat as success so DB record is cleaned up
                                }
                                else if (recordClient != null)
                                {
                                    removedFromClient = await RemoveFromClientAsync(recordClient, downloadId, downloadRecord);
                                    downloadClientId = recordClient.Id;
                                }
                                else
                                {
                                    // Client no longer exists, just remove from database
                                    removedFromClient = true;
                                    logger.LogWarning("Download client {ClientId} not found for download {DownloadId}, removing from database only",
                                        downloadRecord.DownloadClientId, downloadId);
                                }
                            }
                        }
                        else
                        {
                            // Download not in database and invalid client ID provided
                            // This could be an external queue item with a bad client ID reference
                            // Try all enabled clients to find and remove it
                            logger.LogWarning("Invalid client ID {ClientId} and download {DownloadId} not in database, trying all clients",
                                downloadClientId, downloadId);

                            var downloadClients = await configurationService.GetDownloadClientConfigurationsAsync();
                            var enabledClients = downloadClients.Where(c => c.IsEnabled).ToList();

                            foreach (var tryClient in enabledClients)
                            {
                                removedFromClient = await RemoveFromClientAsync(tryClient, downloadId, downloadRecord);
                                if (removedFromClient)
                                {
                                    downloadClientId = tryClient.Id;
                                    logger.LogInformation("Successfully removed {DownloadId} from client {ClientName}", downloadId, tryClient.Name);
                                    break;
                                }
                            }

                            // If still not removed but not in any queue, consider it success
                            if (!removedFromClient)
                            {
                                logger.LogInformation("Could not remove {DownloadId} from any client, verifying it's not in any queue", downloadId);
                                var currentQueue = await downloadQueueService.GetQueueAsync();
                                if (!currentQueue.Any(q => q.Id.Equals(downloadId, StringComparison.OrdinalIgnoreCase)))
                                {
                                    logger.LogInformation("Download {DownloadId} not found in any queue, treating as successfully removed", downloadId);
                                    removedFromClient = true;
                                }
                            }
                        }
                    }
                }

                // If successfully removed from client (or force=true), also remove from database
                if (removedFromClient && downloadRecord != null)
                {
                    await downloadRepository.RemoveAsync(downloadRecord.Id);
                    logger.LogInformation("Removed download record from database: {DownloadId} (Title: {Title})",
                        downloadRecord.Id, downloadRecord.Title);
                }

                return removedFromClient;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Error removing from queue: {DownloadId}", downloadId);
                return false;
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
                    Language = searchResult.Language,
                    OriginalUrl = searchResult.TorrentUrl ?? searchResult.NzbUrl ?? searchResult.MagnetLink ?? string.Empty,
                    Progress = 0,
                    TotalSize = searchResult.Size,
                    DownloadedSize = 0,
                    DownloadPath = string.Empty,
                    FinalPath = string.Empty,
                    StartedAt = DateTime.UtcNow,
                    DownloadClientId = "DDL",
                    Metadata = new Dictionary<string, object>
                    {
                        ["Source"] = searchResult.Source ?? string.Empty,
                        ["Quality"] = searchResult.Quality ?? string.Empty,
                        ["Language"] = searchResult.Language ?? string.Empty,
                        ["DownloadType"] = "DDL"
                    }
                };

                await downloadRepository.AddAsync(download);
                return id;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "DownloadDirectlyAsync: failed to create DDL download record");
                return Guid.NewGuid().ToString();
            }
        }

        private async Task LogDownloadHistory(Audiobook audiobook, string source, SearchResult result)
        {
            // Placeholder: log to internal logger for visibility; actual history persistence is elsewhere
            try
            {
                logger.LogInformation("LogDownloadHistory: audiobook={Title}, source={Source}, result={ResultTitle}", audiobook?.Title, source, result?.Title);
            }
            catch (Exception caughtEx_13) when (caughtEx_13 is not OperationCanceledException && caughtEx_13 is not OutOfMemoryException && caughtEx_13 is not StackOverflowException)
            {
                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }
            await Task.CompletedTask;
        }

        private string? TryResolveClientSpecificIdFallback(DownloadClientConfiguration client, SearchResult searchResult)
        {
            if (client == null || searchResult == null || !IsTorrentResult(searchResult))
            {
                return null;
            }

            var magnetHash = TryExtractMagnetHash(searchResult.MagnetLink);
            if (!string.IsNullOrWhiteSpace(magnetHash))
            {
                logger.LogInformation(
                    "Using magnet hash fallback for download '{Title}' on client {ClientName}",
                    LogRedaction.SanitizeText(searchResult.Title),
                    LogRedaction.SanitizeText(client.Name ?? client.Id));
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

        private async Task<bool> RemoveFromClientAsync(DownloadClientConfiguration client, string downloadId, Download? downloadRecord = null)
        {
            try
            {
                if (client == null) return false;

                // Resolve the client-specific ID (torrent hash, NZB ID, etc.) from the download record.
                // The download record's Metadata dictionary stores the mapping set during AddAsync.
                // Without this, Transmission/qBittorrent receive the Listenarr UUID which they don't recognise.
                var clientItemId = downloadId;
                if (downloadRecord?.Metadata != null)
                {
                    if ((string.Equals(client.Type, "qbittorrent", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(client.Type, "transmission", StringComparison.OrdinalIgnoreCase)) &&
                        downloadRecord.Metadata.TryGetValue("TorrentHash", out var hashObj))
                    {
                        var hash = hashObj?.ToString();
                        if (!string.IsNullOrEmpty(hash))
                        {
                            clientItemId = hash;
                            logger.LogDebug("RemoveFromClientAsync: Using torrent hash {Hash} instead of download ID for {ClientType} removal",
                                hash, client.Type);
                        }
                    }
                    else if (downloadRecord.Metadata.TryGetValue("ClientDownloadId", out var clientIdObj))
                    {
                        var resolvedId = clientIdObj?.ToString();
                        if (!string.IsNullOrEmpty(resolvedId))
                        {
                            clientItemId = resolvedId;
                            logger.LogDebug("RemoveFromClientAsync: Using client-specific ID {ClientId} for {ClientType} removal",
                                resolvedId, client.Type);
                        }
                    }
                }

                if (clientGateway != null)
                {
                    try
                    {
                        var removed = await clientGateway.RemoveAsync(client, clientItemId, false);
                        if (removed)
                        {
                            logger.LogInformation("Successfully removed {DownloadId} from client {ClientName}", downloadId, client.Name ?? client.Id);
                            return true;
                        }

                        // If removal returned false, verify if the item is still in the client's queue
                        // If it's not in the queue, consider removal successful (item already gone)
                        logger.LogWarning("Client reported removal failed for {DownloadId}, checking if item still exists in queue", downloadId);
                        try
                        {
                            var queue = await clientGateway.GetQueueAsync(client);
                            var stillExists = queue.Any(q => q.Id.Equals(downloadId, StringComparison.OrdinalIgnoreCase));

                            if (!stillExists)
                            {
                                logger.LogInformation("Item {DownloadId} no longer in {ClientName} queue, treating removal as successful", downloadId, client.Name ?? client.Id);
                                return true;
                            }

                            logger.LogWarning("Item {DownloadId} still exists in {ClientName} queue after removal attempt", downloadId, client.Name ?? client.Id);
                            return false;
                        }
                        catch (Exception queueEx) when (queueEx is not OperationCanceledException && queueEx is not OutOfMemoryException && queueEx is not StackOverflowException)
                        {
                            logger.LogWarning(queueEx, "Failed to verify queue status for {DownloadId} on {ClientName}, assuming removal failed", downloadId, client.Name ?? client.Id);
                            return false;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        logger.LogWarning(ex, "RemoveFromClientAsync: Exception removing {DownloadId} from {Client}: {Message}",
                            LogRedaction.SanitizeText(downloadId), LogRedaction.SanitizeText(client.Name ?? client.Id), ex.Message);

                        // Check if item still exists in queue - if not, consider removal successful
                        try
                        {
                            var queue = await clientGateway.GetQueueAsync(client);
                            var stillExists = queue.Any(q => q.Id.Equals(downloadId, StringComparison.OrdinalIgnoreCase));

                            if (!stillExists)
                            {
                                logger.LogInformation("After exception, item {DownloadId} not found in {ClientName} queue, treating as successfully removed",
                                    downloadId, client.Name ?? client.Id);
                                return true;
                            }
                        }
                        catch (Exception queueEx) when (queueEx is not OperationCanceledException && queueEx is not OutOfMemoryException && queueEx is not StackOverflowException)
                        {
                            logger.LogDebug(queueEx, "Failed to verify queue after exception for {DownloadId}", downloadId);
                        }

                        return false;
                    }
                }

                // Fallback conservative behavior when no gateway is available
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "RemoveFromClientAsync fallback failed for client {Client}", client.Name ?? client.Id);
                return false;
            }
        }

        public async Task UpdateAsync(Download download)
        {
            var previous = await downloadRepository.GetByIdAsync(download.Id);
            if (previous is null)
            {
                logger.LogWarning("Skipping update for unknown download {DownloadId}", LogRedaction.SanitizeText(download.Id));
                return;
            }

            await downloadRepository.UpdateAsync(download);

            switch (previous.Status, download.Status)
            {
                case var (old, next) when old == next:
                    return;
                case (_, DownloadStatus.Moved):
                    await notificationService.OnDownloadImportedAsync(download);
                    return;
                case (_, DownloadStatus.Failed):
                case (_, DownloadStatus.ImportBlocked):
                    await notificationService.OnDownloadFailedAsync(download);
                    return;
                default:
                    return;
            }
        }
    }
}
