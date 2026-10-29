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

using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads.Submission
{
    public partial class DownloadService
    {
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
                return new SearchAndDownloadResult { Success = false, Message = "Audiobook has no quality profile assigned" };
            }

            // Check if an active download already exists before querying indexers (Issue #936)
            try
            {
                if (await DownloadDuplicateGuard.HasActiveDownloadAsync(audiobookId, configurationService, downloadRepository))
                {
                    logger.LogInformation("Skipping search for audiobook {AudiobookId} — active download exists. Title: '{Title}'", audiobookId, audiobook.Title);
                    return new SearchAndDownloadResult { Success = false, Message = "An active download already exists for this audiobook" };
                }
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                logger.LogWarning(ex, "Failed to check for duplicate downloads for audiobook {AudiobookId}", audiobookId);
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
                return new SearchAndDownloadResult { Success = false, Message = "No search results found" };
            }

            var assessment = await automaticScoring.ScoreAsync(audiobook, searchResults, audiobook.QualityProfile);
            var scoredResults = assessment.Candidates;

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
            var topResult = automaticScoring.SelectBest(assessment);

            if (topResult == null)
            {
                logger.LogWarning("No acceptable search results found for audiobook '{Title}' after quality filtering", audiobook.Title);
                return new SearchAndDownloadResult { Success = false, Message = "No acceptable search results found" };
            }

            // Assign score to SearchResult
            topResult.SearchResult.Score = topResult.TotalScore;

            var candidate = TrustedDownloadCandidateFactory.Create(topResult.SearchResult);
            var isTorrent = candidate.SourceDescriptor.Protocol == DownloadProtocol.Torrent;
            var downloadClientId = await downloadClientSelector.GetAppropriateDownloadClientAsync(isTorrent);

            if (downloadClientId == null)
            {
                logger.LogWarning("No suitable download client found for type: {Type}", isTorrent ? "Torrent" : "NZB");
                return new SearchAndDownloadResult { Success = false, Message = $"No suitable download client found for {(isTorrent ? "torrent" : "NZB")} results" };
            }

            // Send to download client with audiobookId for proper metadata linking
            if (!await automaticScoring.IsCurrentAsync(assessment, audiobookId))
                return new SearchAndDownloadResult { Success = false, Message = "Search context changed; retry search" };

            var downloadId2 = await SendToDownloadClientAsync(candidate, downloadClientId, audiobookId);

            if (string.IsNullOrEmpty(downloadId2))
            {
                logger.LogInformation("Download was skipped (duplicate or client rejected) for audiobook {AudiobookId}", audiobookId);
                return new SearchAndDownloadResult { Success = false, Message = "Download skipped — an active download already exists", DownloadClientUsed = downloadClientId, SearchResult = topResult.SearchResult };
            }

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
    }
}
