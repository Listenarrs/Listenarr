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

namespace Listenarr.Application.Search.Core
{
    public sealed class SearchFinalDispositionLogger(ILogger<SearchFinalDispositionLogger> logger)
    {
        public void LogFinalAsinDispositions(
            IEnumerable<string> asinCandidates,
            List<MetadataSearchResult> results,
            List<SearchResult> enrichedList,
            IDictionary<string, string> candidateDropReasons,
            string query,
            bool requireAuthorAndPublisher,
            string containmentMode,
            double fuzzyThreshold)
        {
            try
            {
                var finalAsinEntries = new List<string>();

                foreach (var asin in asinCandidates.Where(asin => !string.IsNullOrWhiteSpace(asin)))
                {
                    if (results.Any(r => string.Equals(r.Asin, asin, StringComparison.OrdinalIgnoreCase)))
                    {
                        TrySetDropReason(candidateDropReasons, asin, "accepted");
                        finalAsinEntries.Add($"{asin}:accepted");
                        continue;
                    }

                    var enrichedCandidate = enrichedList.FirstOrDefault(e => string.Equals(e.Asin, asin, StringComparison.OrdinalIgnoreCase));
                    if (enrichedCandidate != null)
                    {
                        if (requireAuthorAndPublisher && (string.IsNullOrWhiteSpace(enrichedCandidate.Artist) || string.IsNullOrWhiteSpace(enrichedCandidate.Publisher)))
                        {
                            TrySetDropReason(candidateDropReasons, asin, "author_publisher_missing");
                            finalAsinEntries.Add($"{asin}:author_publisher_missing");
                            continue;
                        }

                        if (SearchValidation.IsTitleNoise(enrichedCandidate.Title) || !SearchValidation.IsLikelyAudiobook(enrichedCandidate))
                        {
                            TrySetDropReason(candidateDropReasons, asin, "filtered_title_or_not_likely");
                            finalAsinEntries.Add($"{asin}:filtered_title_or_not_likely");
                            continue;
                        }

                        var containment = 0.0;
                        var fuzzy = 0.0;
                        try
                        {
                            containment = SearchResultMatchEvaluator.ComputeContainmentScore(enrichedCandidate, query);
                            fuzzy = SearchResultMatchEvaluator.ComputeFuzzySimilarity(enrichedCandidate.Title + " " + enrichedCandidate.Artist, query);
                        }
                        catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
                        {
                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                        }

                        if (string.Equals(containmentMode, "Strict", StringComparison.OrdinalIgnoreCase))
                        {
                            var hay = string.Join(" ", new[] { enrichedCandidate.Title, enrichedCandidate.Artist, enrichedCandidate.Album, enrichedCandidate.Description, enrichedCandidate.Publisher, enrichedCandidate.Narrator, enrichedCandidate.Language, enrichedCandidate.Series }.Where(s => !string.IsNullOrEmpty(s))).ToLowerInvariant();
                            if (string.IsNullOrEmpty(hay) || hay.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                TrySetDropReason(candidateDropReasons, asin, "containment_failed_strict");
                                finalAsinEntries.Add($"{asin}:containment_failed_strict");
                                continue;
                            }
                        }
                        else if (containment < 0.4 && fuzzy < fuzzyThreshold)
                        {
                            TrySetDropReason(candidateDropReasons, asin, "containment_failed_relaxed");
                            finalAsinEntries.Add($"{asin}:containment_failed_relaxed");
                            continue;
                        }

                        TrySetDropReason(candidateDropReasons, asin, "filtered_post_scoring");
                        finalAsinEntries.Add($"{asin}:filtered_post_scoring");
                        continue;
                    }

                    if (!candidateDropReasons.ContainsKey(asin))
                    {
                        TrySetDropReason(candidateDropReasons, asin, "no_metadata_and_no_scrape");
                    }

                    candidateDropReasons.TryGetValue(asin, out var dropReason);
                    finalAsinEntries.Add($"{asin}:{dropReason}");
                }

                if (finalAsinEntries.Any())
                {
                    logger.LogInformation("Final ASIN dispositions for query '{Query}': {Entries}", query, string.Join(", ", finalAsinEntries));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Failed to compute final ASIN dispositions for query: {Query}", query);
            }
        }

        private static void TrySetDropReason(IDictionary<string, string> candidateDropReasons, string asin, string reason)
        {
            try
            {
                candidateDropReasons[asin] = reason;
            }
            catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
            {
                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }
        }
    }
}
