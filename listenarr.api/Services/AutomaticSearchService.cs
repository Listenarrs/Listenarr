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
using Microsoft.EntityFrameworkCore;
using System.IO;

namespace Listenarr.Api.Services
{
    public class AutomaticSearchService : BackgroundService
    {
        private readonly ILogger<AutomaticSearchService> _logger;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly TimeSpan _searchInterval = TimeSpan.FromHours(6); // Search every 6 hours

        public AutomaticSearchService(
            ILogger<AutomaticSearchService> logger,
            IServiceScopeFactory serviceScopeFactory)
        {
            _logger = logger;
            _serviceScopeFactory = serviceScopeFactory;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("AutomaticSearchService started. Will search monitored audiobooks every {Hours} hours", _searchInterval.TotalHours);

            // Wait for application to be fully started before first search
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("AutomaticSearchService canceled before first search cycle");
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PerformAutomaticSearchesAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException ex)
                {
                    _logger.LogWarning(ex, "Automatic search cycle canceled/timed out; continuing");
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogError(ex, "Error during automatic search cycle");
                }

                // Wait for next search interval or cancellation
                try
                {
                    await Task.Delay(_searchInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // Expected when stopping
                    break;
                }
            }

            _logger.LogInformation("AutomaticSearchService stopped");
        }

        private async Task PerformAutomaticSearchesAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Starting automatic search cycle for monitored audiobooks");

            using var scope = _serviceScopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
            var searchService = scope.ServiceProvider.GetRequiredService<ISearchService>();
            var qualityProfileService = scope.ServiceProvider.GetRequiredService<IQualityProfileService>();
            var downloadService = scope.ServiceProvider.GetRequiredService<IDownloadService>();

            // Get all monitored audiobooks that haven't been searched in the last 6 hours
            var cutoffTime = DateTime.UtcNow.AddHours(-6);
            var monitoredAudiobooks = await dbContext.Audiobooks
                .Include(a => a.QualityProfile)
                .Where(a => a.Monitored &&
                           (a.LastSearchTime == null || a.LastSearchTime < cutoffTime) &&
                           a.QualityProfileId != null)
                .ToListAsync(stoppingToken);

            _logger.LogInformation("Found {Count} monitored audiobooks eligible for automatic search", monitoredAudiobooks.Count);

            if (!monitoredAudiobooks.Any())
            {
                _logger.LogInformation("No audiobooks require automatic search at this time");
                return;
            }

            var processedCount = 0;
            var downloadsQueued = 0;

            foreach (var audiobook in monitoredAudiobooks)
            {
                if (stoppingToken.IsCancellationRequested)
                    break;

                try
                {
                    var downloadsQueuedForBook = await ProcessAudiobookAsync(
                        audiobook, searchService, qualityProfileService, downloadService, dbContext, stoppingToken);

                    downloadsQueued += downloadsQueuedForBook;
                    processedCount++;

                    // Update last search time
                    audiobook.LastSearchTime = DateTime.UtcNow;
                    await dbContext.SaveChangesAsync(stoppingToken);

                    _logger.LogInformation("Processed audiobook '{Title}' - queued {QueuedCount} downloads",
                        audiobook.Title, downloadsQueuedForBook);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException ex)
                {
                    _logger.LogWarning(ex, "Automatic search processing canceled/timed out for audiobook '{Title}' (ID: {Id})", audiobook.Title, audiobook.Id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogError(ex, "Error processing audiobook '{Title}' (ID: {Id})", audiobook.Title, audiobook.Id);
                }
            }

            _logger.LogInformation("Automatic search cycle completed. Processed {ProcessedCount} audiobooks, queued {DownloadsQueued} total downloads",
                processedCount, downloadsQueued);
        }

        private async Task<int> ProcessAudiobookAsync(
            Audiobook audiobook,
            ISearchService searchService,
            IQualityProfileService qualityProfileService,
            IDownloadService downloadService,
            ListenArrDbContext dbContext,
            CancellationToken stoppingToken)
        {
            if (audiobook.QualityProfile == null)
            {
                _logger.LogWarning("Audiobook '{Title}' has no quality profile assigned", audiobook.Title);
                return 0;
            }

            // Check if there's already an active download for this audiobook
            var activeDownload = await dbContext.Downloads
                .Where(d => d.AudiobookId == audiobook.Id &&
                           (d.Status == DownloadStatus.Queued ||
                            d.Status == DownloadStatus.Downloading ||
                            d.Status == DownloadStatus.Paused ||
                            d.Status == DownloadStatus.Processing ||
                            d.Status == DownloadStatus.Ready ||
                            d.Status == DownloadStatus.ImportPending))
                .FirstOrDefaultAsync(stoppingToken);

            if (activeDownload != null)
            {
                _logger.LogInformation("Audiobook '{Title}' already has an active download (ID: {DownloadId}, Status: {Status}), skipping automatic search",
                    audiobook.Title, activeDownload.Id, activeDownload.Status);
                return 0;
            }

            // Check existing quality and decide whether to search
            var (cutoffMet, bestExistingQuality) = await GetExistingQualityAsync(audiobook, qualityProfileService, dbContext);
            _logger.LogInformation("Audiobook '{Title}': cutoff met={CutoffMet}, best existing quality={BestQuality}",
                audiobook.Title, cutoffMet, bestExistingQuality ?? "none");

            // Skip automatic search if quality cutoff is already met
            if (cutoffMet)
            {
                _logger.LogInformation("Quality cutoff already met for audiobook '{Title}', skipping automatic search", audiobook.Title);
                return 0;
            }

            // Build search query
            var searchQuery = BuildSearchQuery(audiobook);
            _logger.LogInformation("Searching for audiobook '{Title}' with query: {Query}", audiobook.Title, searchQuery);

            // Search for results
            var searchResults = await searchService.SearchAsync(searchQuery, isAutomaticSearch: true);
            _logger.LogInformation("Found {Count} raw search results for audiobook '{Title}'", searchResults.Count, audiobook.Title);

            // Broadcast detailed debug info about the raw search results to help diagnose automatic search failures
            try
            {
                // Build a concise summary of up to 10 raw results
                var rawSummaries = searchResults.Take(10).Select(r => new
                {
                    title = r.Title,
                    asin = r.Asin,
                    source = r.Source,
                    sizeMB = r.Size > 0 ? (r.Size / 1024 / 1024) : -1,
                    seeders = r.Seeders,
                    format = r.Format,
                    downloadType = r.DownloadType
                }).ToList();

                using var scope = _serviceScopeFactory.CreateScope();
                var hub = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<Listenarr.Api.Hubs.DownloadHub>>();
                // Send structured payload with type and audiobookId so the UI can ignore automatic messages by default
                await hub.Clients.All.SendCoreAsync("SearchProgress", new object[] { new { message = $"Automatic search query: {searchQuery}", details = new { rawCount = searchResults.Count, rawSamples = rawSummaries }, type = "automatic", audiobookId = audiobook.Id } });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogDebug(ex, "Failed to broadcast raw search results summary for audiobook {Id}", audiobook.Id);
            }

            if (!searchResults.Any())
            {
                _logger.LogInformation("No search results found for audiobook '{Title}'", audiobook.Title);
                return 0;
            }

            // Score results against quality profile
            var scoredResults = await qualityProfileService.ScoreSearchResults(searchResults, audiobook.QualityProfile);

            // Log all scored results for debugging
            _logger.LogInformation("Scored {Count} search results for audiobook '{Title}':", scoredResults.Count, audiobook.Title);

            // Broadcast scored result summaries (score + rejection reasons) to aid debugging
            try
            {
                var scoredSummaries = scoredResults.Select(s => new
                {
                    title = s.SearchResult.Title,
                    asin = s.SearchResult.Asin,
                    totalScore = s.TotalScore,
                    isRejected = s.IsRejected,
                    rejectionReasons = s.RejectionReasons,
                    source = s.SearchResult.Source,
                    sizeMB = s.SearchResult.Size > 0 ? (s.SearchResult.Size / 1024 / 1024) : -1,
                    seeders = s.SearchResult.Seeders,
                    format = s.SearchResult.Format
                }).ToList();

                using var scope2 = _serviceScopeFactory.CreateScope();
                var hub2 = scope2.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<Listenarr.Api.Hubs.DownloadHub>>();
                await hub2.Clients.All.SendCoreAsync("SearchProgress", new object[] { new { message = $"Scored results for '{audiobook.Title}'", details = new { scoredCount = scoredResults.Count, scoredSamples = scoredSummaries }, type = "automatic", audiobookId = audiobook.Id } });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogDebug(ex, "Failed to broadcast scored search results for audiobook {Id}", audiobook.Id);
            }
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

            var topResult = scoredResults
                .Where(s => !s.IsRejected) // Only non-rejected results
                .OrderByDescending(s => s.TotalScore)
                .FirstOrDefault(); // Pick only the top scoring result

            if (topResult == null)
            {
                _logger.LogInformation("No acceptable search results found for audiobook '{Title}' after quality filtering", audiobook.Title);
                return 0;
            }

            _logger.LogInformation("Found top result for audiobook '{Title}': {ResultTitle} (Score: {Score}, Quality: {Quality})",
                audiobook.Title, topResult.SearchResult.Title, topResult.TotalScore, topResult.SearchResult.Quality);

            // Check if the found result is better quality than what we already have
            if (!string.IsNullOrEmpty(bestExistingQuality))
            {
                var resultIsBetter = IsQualityBetter(topResult.SearchResult.Quality, bestExistingQuality, audiobook.QualityProfile);
                if (!resultIsBetter)
                {
                    _logger.LogInformation("Top result quality '{ResultQuality}' is not better than existing quality '{ExistingQuality}' for audiobook '{Title}', skipping download",
                        topResult.SearchResult.Quality, bestExistingQuality, audiobook.Title);
                    return 0;
                }
                _logger.LogInformation("Top result quality '{ResultQuality}' is better than existing quality '{ExistingQuality}', proceeding with download",
                    topResult.SearchResult.Quality, bestExistingQuality);
            }
            else
            {
                _logger.LogInformation("No existing files for audiobook '{Title}', proceeding with download", audiobook.Title);
            }

            // Add score to the search result for tracking
            topResult.SearchResult.Score = topResult.TotalScore;

            // Queue download for the top result
            var downloadsQueued = 0;
            try
            {
                // Determine appropriate download client for this result
                var isTorrent = IsTorrentResult(topResult.SearchResult);
                var downloadClientId = await GetAppropriateDownloadClientAsync(topResult.SearchResult, isTorrent);

                if (string.IsNullOrEmpty(downloadClientId))
                {
                    _logger.LogWarning("No suitable download client found for result type: {Type}", isTorrent ? "torrent" : "NZB");
                    return 0;
                }

                await downloadService.StartDownloadAsync(topResult.SearchResult, downloadClientId, audiobook.Id);
                downloadsQueued++;

                _logger.LogInformation("Queued download for audiobook '{Title}': {ResultTitle} (Score: {Score})",
                    audiobook.Title, topResult.SearchResult.Title, topResult.TotalScore);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Failed to queue download for audiobook '{Title}': {ResultTitle}",
                    audiobook.Title, topResult.SearchResult.Title);
            }

            return downloadsQueued;
        }

        private async Task<bool> IsQualityCutoffMetAsync(
            Audiobook audiobook,
            IQualityProfileService qualityProfileService,
            ListenArrDbContext dbContext)
        {
            if (audiobook.QualityProfile == null)
                return false;

            // Get existing downloads for this audiobook
            var existingDownloads = await dbContext.Downloads
                .Where(d => d.AudiobookId == audiobook.Id &&
                           (d.Status == DownloadStatus.Completed ||
                            d.Status == DownloadStatus.Downloading ||
                            d.Status == DownloadStatus.ImportPending))
                .ToListAsync();

            // Get existing files for this audiobook
            var existingFiles = await dbContext.AudiobookFiles
                .Where(f => f.AudiobookId == audiobook.Id)
                .ToListAsync();

            if (!existingDownloads.Any() && !existingFiles.Any())
                return false;

            // Check if any existing download meets or exceeds the cutoff quality
            var cutoffQuality = audiobook.QualityProfile.Qualities
                .FirstOrDefault(q => q.Quality == audiobook.QualityProfile.CutoffQuality);

            if (cutoffQuality == null)
                return false;

            // Check downloads first
            foreach (var download in existingDownloads)
            {
                // For completed downloads, check if the file quality meets cutoff
                if (download.Status == DownloadStatus.Completed && !string.IsNullOrEmpty(download.Metadata?.GetValueOrDefault("Quality")?.ToString()))
                {
                    var downloadQuality = download.Metadata["Quality"].ToString();
                    var downloadQualityDefinition = audiobook.QualityProfile.Qualities
                        .FirstOrDefault(q => q.Quality == downloadQuality);

                    if (downloadQualityDefinition != null && downloadQualityDefinition.Priority >= cutoffQuality.Priority)
                    {
                        _logger.LogDebug("Quality cutoff met for audiobook '{Title}' by completed download (Quality: {Quality})",
                            audiobook.Title, downloadQuality);
                        return true;
                    }
                }
                // For active downloads, assume they will meet quality requirements
                else if (download.Status == DownloadStatus.Downloading || download.Status == DownloadStatus.ImportPending)
                {
                    _logger.LogDebug("Quality cutoff assumed met for audiobook '{Title}' due to active download", audiobook.Title);
                    return true;
                }
            }

            // Check existing files
            foreach (var file in existingFiles)
            {
                var fileQuality = DetermineFileQuality(file);
                if (!string.IsNullOrEmpty(fileQuality))
                {
                    var fileQualityDefinition = audiobook.QualityProfile.Qualities
                        .FirstOrDefault(q => q.Quality == fileQuality);

                    if (fileQualityDefinition != null && fileQualityDefinition.Priority >= cutoffQuality.Priority)
                    {
                        _logger.LogDebug("Quality cutoff met for audiobook '{Title}' by existing file (Quality: {Quality}, File: {FileName})",
                            audiobook.Title, fileQuality, Path.GetFileName(file.Path));
                        return true;
                    }
                }
            }

            return false;
        }

        private string? DetermineFileQuality(AudiobookFile file)
        {
            // Determine quality based on file properties
            // This mirrors the logic in QualityProfileService.GetQualityScore but works with file metadata

            // Check format/container first
            if (!string.IsNullOrEmpty(file.Container))
            {
                var container = file.Container.ToLower();
                if (container.Contains("flac")) return "FLAC";
                if (container.Contains("m4b") || container.Contains("m4a")) return "M4B";
            }

            if (!string.IsNullOrEmpty(file.Format))
            {
                var format = file.Format.ToLower();
                if (format.Contains("flac")) return "FLAC";
                if (format.Contains("m4b") || format.Contains("m4a")) return "M4B";
                if (format.Contains("aac")) return "M4B"; // AAC in M4B container
            }

            // Check bitrate for MP3 quality determination
            if (file.Bitrate.HasValue)
            {
                var bitrate = file.Bitrate.Value;

                // Convert bits per second to kilobits per second for easier comparison
                var kbps = bitrate / 1000;

                if (kbps >= 320) return "MP3 320kbps";
                if (kbps >= 256) return "MP3 256kbps";
                if (kbps >= 192) return "MP3 192kbps";
                if (kbps >= 128) return "MP3 128kbps";
                if (kbps >= 64) return "MP3 64kbps";

                // For very low bitrates, still classify as MP3
                return "MP3 64kbps";
            }

            // Check codec
            if (!string.IsNullOrEmpty(file.Codec))
            {
                var codec = file.Codec.ToLower();
                if (codec.Contains("flac")) return "FLAC";
                if (codec.Contains("aac")) return "M4B";
                if (codec.Contains("mp3")) return "MP3 128kbps"; // Default MP3 quality if no bitrate info
                if (codec.Contains("opus")) return "M4B"; // Opus is often in M4B containers
            }

            // If we can't determine quality from metadata, try to infer from file extension
            if (!string.IsNullOrEmpty(file.Path))
            {
                var extension = Path.GetExtension(file.Path).ToLower();
                switch (extension)
                {
                    case ".flac":
                        return "FLAC";
                    case ".m4b":
                    case ".m4a":
                        return "M4B";
                    case ".mp3":
                        return "MP3 128kbps"; // Conservative default for MP3
                    case ".aac":
                        return "M4B";
                    case ".opus":
                        return "M4B";
                }
            }

            return null; // Unable to determine quality
        }

        private string BuildSearchQuery(Audiobook audiobook)
        {
            var parts = new List<string>();

            // Add title
            if (!string.IsNullOrEmpty(audiobook.Title))
                parts.Add(audiobook.Title);

            // Add primary author
            if (audiobook.Authors != null && audiobook.Authors.Any())
                parts.Add(audiobook.Authors.First());

            // Add series if available
            if (!string.IsNullOrEmpty(audiobook.Series))
                parts.Add(audiobook.Series);

            return string.Join(" ", parts);
        }

        private bool IsTorrentResult(SearchResult result)
        {
            // Check DownloadType first if it's set
            if (!string.IsNullOrEmpty(result.DownloadType))
            {
                if (result.DownloadType == "DDL")
                {
                    return false; // DDL is not a torrent
                }
                else if (result.DownloadType == "Torrent")
                {
                    return true;
                }
                else if (result.DownloadType == "Usenet")
                {
                    return false;
                }
            }

            // Fallback to legacy detection logic
            // Check for NZB first - if it has an NZB URL, it's a Usenet/NZB download
            if (!string.IsNullOrEmpty(result.NzbUrl))
            {
                return false;
            }

            // Check for torrent indicators - magnet link or torrent file
            if (!string.IsNullOrEmpty(result.MagnetLink) || !string.IsNullOrEmpty(result.TorrentUrl))
            {
                return true;
            }

            // If neither is set, we can't reliably determine the type
            // Log a warning and default to false (NZB) as a safer choice
            _logger.LogWarning("Unable to determine result type for '{Title}' from source '{Source}'. No MagnetLink, TorrentUrl, or NzbUrl found. Defaulting to NZB.",
                result.Title, result.Source);
            return false;
        }

        /// <summary>
        /// Determine whether the audiobook already meets the quality cutoff and return the best existing quality string (if any).
        /// </summary>
        private async Task<(bool cutoffMet, string? bestExistingQuality)> GetExistingQualityAsync(
            Audiobook audiobook,
            IQualityProfileService qualityProfileService,
            ListenArrDbContext dbContext)
        {
            // Reuse existing cutoff logic
            var cutoffMet = await IsQualityCutoffMetAsync(audiobook, qualityProfileService, dbContext);

            // Find the best quality among existing files and completed downloads (if any)
            string? bestQuality = null;

            var existingDownloads = await dbContext.Downloads
                .Where(d => d.AudiobookId == audiobook.Id && d.Status == DownloadStatus.Completed)
                .ToListAsync();

            foreach (var dl in existingDownloads)
            {
                if (dl.Metadata != null && dl.Metadata.TryGetValue("Quality", out var qobj) && qobj != null)
                {
                    var q = qobj.ToString();
                    if (!string.IsNullOrEmpty(q))
                    {
                        if (bestQuality == null) bestQuality = q;
                        else if (IsQualityBetter(q, bestQuality, audiobook.QualityProfile)) bestQuality = q;
                    }
                }
            }

            var existingFiles = await dbContext.AudiobookFiles
                .Where(f => f.AudiobookId == audiobook.Id)
                .ToListAsync();

            foreach (var f in existingFiles)
            {
                var fq = DetermineFileQuality(f);
                if (!string.IsNullOrEmpty(fq))
                {
                    if (bestQuality == null) bestQuality = fq;
                    else if (IsQualityBetter(fq, bestQuality, audiobook.QualityProfile)) bestQuality = fq;
                }
            }

            return (cutoffMet, bestQuality);
        }

        /// <summary>
        /// Compare two quality strings using the quality profile priorities.
        /// Returns true if candidateQuality is better (higher priority) than existingQuality.
        /// </summary>
        private bool IsQualityBetter(string? candidateQuality, string? existingQuality, QualityProfile? profile)
        {
            if (string.IsNullOrEmpty(candidateQuality)) return false;
            if (string.IsNullOrEmpty(existingQuality)) return true;
            if (profile == null) return false;

            var cand = profile.Qualities.FirstOrDefault(q => q.Quality == candidateQuality);
            var exist = profile.Qualities.FirstOrDefault(q => q.Quality == existingQuality);

            if (cand == null) return false;
            if (exist == null) return true; // unknown existing quality -> treat candidate as better

            return cand.Priority > exist.Priority;
        }

        private async Task<string> GetAppropriateDownloadClientAsync(SearchResult searchResult, bool isTorrent)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var configurationService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();

            // Special handling for DDL downloads - they don't use external clients
            if (searchResult.DownloadType?.Equals("DDL", StringComparison.OrdinalIgnoreCase) == true)
            {
                _logger.LogInformation("DDL download detected, using internal DDL client");
                return "DDL";
            }

            // Get all configured download clients
            var clients = await configurationService.GetDownloadClientConfigurationsAsync();
            var enabledClients = clients.Where(c => c.IsEnabled).ToList();

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

                return client?.Id ?? string.Empty;
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

                return client?.Id ?? string.Empty;
            }
        }
    }
}

