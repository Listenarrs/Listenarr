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

namespace Listenarr.Infrastructure.HostedServices.Search
{
    public class AutomaticSearchService(
        ILogger<AutomaticSearchService> logger,
        IAutomaticSearchProcessor processor,
        IWorkerCycleRunner cycleRunner) : BackgroundService
    {
        private static readonly TimeSpan SearchInterval = TimeSpan.FromHours(6);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("AutomaticSearchService started. Will search monitored audiobooks every {Hours} hours", SearchInterval.TotalHours);

            await cycleRunner.RunPeriodicAsync(
                nameof(AutomaticSearchService),
                initialDelay: TimeSpan.FromMinutes(5),
                intervalProvider: () => SearchInterval,
                runCycle: processor.RunCycleAsync,
                stoppingToken);

            logger.LogInformation("AutomaticSearchService stopped");
        }
    }

    public class AutomaticSearchProcessor : IAutomaticSearchProcessor
    {
        private readonly ILogger<AutomaticSearchProcessor> _logger;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly AutomaticSearchResultClassifier _resultClassifier;
        private readonly AutomaticSearchQualityEvaluator _qualityEvaluator;
        private readonly AutomaticSearchDownloadClientSelector _downloadClientSelector;

        public AutomaticSearchProcessor(
            ILogger<AutomaticSearchProcessor> logger,
            IServiceScopeFactory serviceScopeFactory)
        {
            _logger = logger;
            _serviceScopeFactory = serviceScopeFactory;
            _resultClassifier = new AutomaticSearchResultClassifier(_logger);
            _qualityEvaluator = new AutomaticSearchQualityEvaluator(_logger);
            _downloadClientSelector = new AutomaticSearchDownloadClientSelector(_serviceScopeFactory, _logger);
        }

        public Task RunCycleAsync(CancellationToken cancellationToken) => PerformAutomaticSearchesAsync(cancellationToken);

        private async Task PerformAutomaticSearchesAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Starting automatic search cycle for monitored audiobooks");

            using var scope = _serviceScopeFactory.CreateScope();
            var audiobookRepository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var downloadRepository = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
            var fileRepository = scope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
            var searchService = scope.ServiceProvider.GetRequiredService<ISearchService>();
            var qualityProfileService = scope.ServiceProvider.GetRequiredService<IQualityProfileService>();
            var downloadService = scope.ServiceProvider.GetRequiredService<IDownloadService>();

            // Get all monitored audiobooks that haven't been searched in the last 6 hours
            var cutoffTime = DateTime.UtcNow.AddHours(-6);
            var monitoredAudiobooks = await audiobookRepository.GetMonitoredAudiobooksForSearchAsync(cutoffTime, stoppingToken);

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
                        audiobook, searchService, qualityProfileService, downloadService, audiobookRepository, downloadRepository, fileRepository, stoppingToken);

                    downloadsQueued += downloadsQueuedForBook;
                    processedCount++;

                    // Update last search time
                    audiobook.LastSearchTime = DateTime.UtcNow;
                    await audiobookRepository.UpdateAsync(audiobook);

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
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
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
            IAudiobookRepository audiobookRepository,
            IDownloadRepository downloadRepository,
            IAudiobookFileRepository fileRepository,
            CancellationToken stoppingToken)
        {
            if (audiobook.QualityProfile == null)
            {
                _logger.LogWarning("Audiobook '{Title}' has no quality profile assigned", audiobook.Title);
                return 0;
            }

            // Check if there's already an active download for this audiobook
            var allDownloads = await downloadRepository.GetByAudiobookIdAsync(audiobook.Id, stoppingToken);
            var activeDownload = allDownloads.FirstOrDefault(d =>
                d.Status == DownloadStatus.Queued ||
                d.Status == DownloadStatus.Downloading ||
                d.Status == DownloadStatus.Paused ||
                d.Status == DownloadStatus.Processing ||
                d.Status == DownloadStatus.Ready ||
                d.Status == DownloadStatus.ImportPending);

            if (activeDownload != null)
            {
                _logger.LogInformation("Audiobook '{Title}' already has an active download (ID: {DownloadId}, Status: {Status}), skipping automatic search",
                    audiobook.Title, activeDownload.Id, activeDownload.Status);
                return 0;
            }

            // Check existing quality and decide whether to search
            var (cutoffMet, bestExistingQuality) = await _qualityEvaluator.GetExistingQualityAsync(audiobook, downloadRepository, fileRepository, stoppingToken);
            _logger.LogInformation("Audiobook '{Title}': cutoff met={CutoffMet}, best existing quality={BestQuality}",
                audiobook.Title, cutoffMet, bestExistingQuality ?? "none");

            // Skip automatic search if quality cutoff is already met
            if (cutoffMet)
            {
                _logger.LogInformation("Quality cutoff already met for audiobook '{Title}', skipping automatic search", audiobook.Title);
                return 0;
            }

            // Build search query
            var searchQuery = _resultClassifier.BuildSearchQuery(audiobook);
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
                var hub = scope.ServiceProvider.GetRequiredService<IHubContext<DownloadHub>>();
                // Send structured payload with type and audiobookId so the UI can ignore automatic messages by default
                await hub.Clients.All.SendCoreAsync("SearchProgress", new object[] { new { message = $"Automatic search query: {searchQuery}", details = new { rawCount = searchResults.Count, rawSamples = rawSummaries }, type = "automatic", audiobookId = audiobook.Id } });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
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
                var hub2 = scope2.ServiceProvider.GetRequiredService<IHubContext<DownloadHub>>();
                await hub2.Clients.All.SendCoreAsync("SearchProgress", new object[] { new { message = $"Scored results for '{audiobook.Title}'", details = new { scoredCount = scoredResults.Count, scoredSamples = scoredSummaries }, type = "automatic", audiobookId = audiobook.Id } });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
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

            // Drop releases already blocked for this book before picking a winner.
            //
            // This is the path that actually re-grabs. The blocklist filter was wired into
            // SearchAndDownloadAsync, which covers the manual search endpoint and the retry that
            // follows a failure, but this service does not go through it: it scores results here and
            // calls StartDownloadAsync directly. So a release could fail, be blocked, and be grabbed
            // again by the next automatic pass a minute later, which is what a live install saw.
            using var blocklistScope = _serviceScopeFactory.CreateScope();
            var blocklistService = blocklistScope.ServiceProvider.GetRequiredService<IBlocklistService>();
            var selectableResults = await BlockedReleaseFilter.ExcludeAsync(
                blocklistService, audiobook.Id, scoredResults, _logger);

            var topResult = selectableResults
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
                var resultIsBetter = _qualityEvaluator.IsQualityBetter(topResult.SearchResult.Quality, bestExistingQuality, audiobook.QualityProfile);
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
                var isTorrent = _resultClassifier.IsTorrentResult(topResult.SearchResult);
                var downloadClientId = await _downloadClientSelector.GetAppropriateDownloadClientAsync(topResult.SearchResult, isTorrent);

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
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to queue download for audiobook '{Title}': {ResultTitle}",
                    audiobook.Title, topResult.SearchResult.Title);
            }

            return downloadsQueued;
        }

    }
}
