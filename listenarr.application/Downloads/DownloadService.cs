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

using Listenarr.Domain.Common;
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
        IDownloadQueueService downloadQueueService,
        INotificationService notificationService,
        IHubBroadcaster hubBroadcaster,
        IDownloadHistoryService downloadHistoryService,
        DownloadTypeResolver downloadTypeResolver,
        DownloadClientSelector downloadClientSelector,
        DownloadCachedTorrentStore cachedTorrentStore) : IDownloadService
    {
        // Cache expiration constants
        private const int QueueCacheExpirationSeconds = 10;
        private const int ClientStatusCacheExpirationSeconds = 30;
        private const int DirectDownloadTimeoutHours = 2;

        // Track qBittorrent sync state for incremental updates (clientId -> last rid)
        private readonly Dictionary<string, int> _qbittorrentSyncState = new();

        // Track qBittorrent torrent cache for merging incremental updates (clientId -> (torrentHash -> QueueItem))
        private readonly Dictionary<string, Dictionary<string, QueueItem>> _qbittorrentTorrentCache = new();
        private readonly DownloadClientIdFallbackResolver _clientIdFallbackResolver = new(downloadTypeResolver, logger);

        public async Task<string> StartDownloadAsync(SearchResult searchResult, string downloadClientId, int? audiobookId = null)
        {
            return await SendToDownloadClientAsync(searchResult, downloadClientId, audiobookId);
        }

        /// <summary>
        /// Retrieve cached torrent bytes and filename for a given download id if available
        /// </summary>
        public Task<(byte[]? Bytes, string? FileName)> GetCachedTorrentAsync(string downloadId)
        {
            return cachedTorrentStore.GetCachedTorrentAsync(downloadId);
        }

        /// <summary>
        /// Retrieve cached announce URLs for a given download id if available
        /// </summary>
        public Task<List<string>?> GetCachedAnnouncesAsync(string downloadId)
        {
            return cachedTorrentStore.GetCachedAnnouncesAsync(downloadId);
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
            var searchQuery = DownloadSearchQueryBuilder.Build(audiobook);
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

            var effectiveDownloadType = await downloadTypeResolver.ResolveAsync(topResult.SearchResult);
            topResult.SearchResult.DownloadType = DownloadTypeResolver.GetLabel(effectiveDownloadType);

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
            var downloadClientId = await downloadClientSelector.GetAppropriateDownloadClientAsync(isTorrent);

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

            var effectiveDownloadType = await downloadTypeResolver.ResolveAsync(searchResult);
            searchResult.DownloadType = DownloadTypeResolver.GetLabel(effectiveDownloadType);

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
                downloadClientId = await downloadClientSelector.GetAppropriateDownloadClientAsync(isTorrent);

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
            var download = DownloadRecordFactory.CreateQueuedDownload(
                downloadId,
                searchResult,
                downloadClient,
                downloadClientIdForModel,
                audiobookId);

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
            clientSpecificId ??= _clientIdFallbackResolver.TryResolve(downloadClient, searchResult);

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
            var preparationService = new MyAnonamouseTorrentPreparationService(
                indexerRepository,
                httpClientFactory,
                cachedTorrentStore,
                logger);
            await preparationService.PrepareAsync(searchResult, downloadId);
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
