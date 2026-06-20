/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Search.Core
{
    public partial class SearchService : ISearchService
    {
        public async Task<List<MetadataSearchResult>> IntelligentSearchAsync(string query, int candidateLimit = 200, int returnLimit = 100, string containmentMode = "Relaxed", bool requireAuthorAndPublisher = false, double fuzzyThreshold = 0.2, string region = "us", string? language = null, CancellationToken ct = default)
        {
            var results = new List<MetadataSearchResult>();

            try
            {
                _logger.LogInformation("Starting intelligent search for: {Query}", query);

                var parsedQuery = SearchQueryParser.Parse(query);
                var searchType = parsedQuery.SearchType;
                var actualQuery = parsedQuery.ActualQuery;
                var asinVal = parsedQuery.Asin;
                var isbnVal = parsedQuery.Isbn;
                var authorVal = parsedQuery.Author;
                var titleVal = parsedQuery.Title;

                try { _logger.LogInformation("Parsed prefixes: ASIN={Asin}, ISBN={Isbn}, AUTHOR={Author}, TITLE={Title}", asinVal, isbnVal, authorVal, titleVal); }
                catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException)
                {
                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                }

                try { _logger.LogInformation("[DBG] Determined searchType='{SearchType}'", searchType); }
                catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException)
                {
                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                }

                // Try Audible-first for various search types. If Audible returns results,
                // convert them to SearchResult and return immediately to avoid scraping.
                try
                {
                    // ASIN case is handled separately above via ASIN handler

                    var simpleAudibleResults = await _audibleSimpleLookupWorkflow.TrySearchAsync(
                        searchType,
                        isbnVal,
                        titleVal,
                        actualQuery,
                        region,
                        language);
                    if (simpleAudibleResults?.Any() == true)
                    {
                        return simpleAudibleResults;
                    }

                    var authorAudibleResults = await _audibleAuthorSearchWorkflow.TrySearchAsync(
                        searchType,
                        authorVal,
                        titleVal,
                        isbnVal,
                        candidateLimit,
                        region,
                        language);
                    if (authorAudibleResults?.Any() == true)
                    {
                        return authorAudibleResults;
                    }

                }
                catch (Exception exAudibleFirst) when (exAudibleFirst is not OperationCanceledException && exAudibleFirst is not OutOfMemoryException && exAudibleFirst is not StackOverflowException)
                {
                    _logger.LogWarning(exAudibleFirst, "Audible-first attempt failed; falling back to provider searches for query: {Query}", query);
                }

                // Flags controlling provider calls (enabled by default) - declare at outer scope
                var skipOpenLibrary = false;

                // Handle ASIN queries immediately with metadata-first approach
                if (searchType == "ASIN" && !string.IsNullOrEmpty(asinVal))
                {
                    var asinMetadataSources = await GetEnabledMetadataSourcesAsync();
                    var asinSearchResults = await _asinSearchHandler.SearchByAsinAsync(asinVal, asinMetadataSources);
                    return asinSearchResults.Select(r => SearchResultConverters.ToMetadata(r)).ToList();
                }

                // Regular search flow for non-ASIN queries (ISBN, AUTHOR, TITLE, or normal text)
                _logger.LogInformation("Searching for: {Query}", actualQuery);
                await _searchProgressReporter.BroadcastAsync($"Searching for {actualQuery}", null);

                // Apply application-level search settings (if configured)
                try
                {
                    var appSettings = await _configurationService.GetApplicationSettingsAsync();
                    if (appSettings != null)
                    {
                        skipOpenLibrary = !appSettings.EnableOpenLibrarySearch;
                    }
                }
                catch (Exception exAppSettings) when (exAppSettings is not OperationCanceledException && exAppSettings is not OutOfMemoryException && exAppSettings is not StackOverflowException)
                {
                    _logger.LogDebug(exAppSettings, "Failed to load application search settings, falling back to defaults");
                }

                // Step 2: Collect candidates from OpenLibrary (and other non-scraping sources)
                var candidateCollection = await _asinCandidateCollector.CollectCandidatesAsync(
                    query, skipOpenLibrary, ct);

                var asinCandidates = candidateCollection.AsinCandidates;
                var asinToRawResult = candidateCollection.AsinToRawResult;
                var asinToSource = candidateCollection.AsinToSource;
                var asinToOpenLibrary = candidateCollection.AsinToOpenLibrary;
                var openLibraryDerivedResults = candidateCollection.OpenLibraryDerivedResults;

                // Deduplicate and enforce unified candidate cap
                asinCandidates = asinCandidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                // Enforce unified candidate cap (if specified) so we don't attempt enrichment on too many ASINs
                if (candidateLimit > 0 && asinCandidates.Count > candidateLimit)
                {
                    _logger.LogInformation("Trimming unified ASIN candidate list from {Before} to candidateLimit={Limit}", asinCandidates.Count, candidateLimit);
                    asinCandidates = asinCandidates.Take(candidateLimit).ToList();
                }
                _logger.LogInformation("Unified ASIN candidate list size: {Count}", asinCandidates.Count);
                await _searchProgressReporter.BroadcastAsync($"Found {asinCandidates.Count} ASIN candidates", null);

                // If we don't have any ASIN candidates, do not return early. Instead allow
                // OpenLibrary-derived candidates (if any) to be merged and processed later
                // so they are combined with ASIN-derived enriched results at the end.
                if (!asinCandidates.Any())
                {
                    _logger.LogInformation("No ASIN candidates found; will rely on OpenLibrary augmentation and later fallback processing if available");
                }

                // Step 3: Get enabled metadata sources ONCE before concurrent enrichment to avoid DbContext threading issues
                _logger.LogInformation("Fetching enabled metadata sources before concurrent enrichment...");
                var metadataSources = await GetEnabledMetadataSourcesAsync();
                _logger.LogInformation("Will use {Count} metadata source(s) for all ASINs", metadataSources.Count);

                // Step 4: Enrich each ASIN with detailed metadata concurrently (limit concurrency)
                var enrichmentResult = await _asinEnricher.EnrichAsinsAsync(
                    asinCandidates,
                    asinToRawResult,
                    asinToSource,
                    asinToOpenLibrary,
                    metadataSources,
                    query,
                    ct);

                var enrichedList = enrichmentResult.EnrichedResults;
                var candidateDropReasons = enrichmentResult.CandidateDropReasons;
                await _searchProgressReporter.BroadcastAsync($"Enrichment complete. Found {enrichedList.Count} enriched results", null);

                // Merge OpenLibrary-derived results (created earlier) into the enriched list so
                // OpenLibrary-only augmentation produces visible, scoreable items without calling Amazon.
                try
                {
                    // Only merge OpenLibrary-derived candidates when we did not obtain any enriched
                    // metadata from external sources. If we already have enriched metadata results
                    // (e.g. from Audible/Audnexus), prefer those authoritative results and avoid
                    // adding OpenLibrary fallbacks that could dilute the final ranked list.
                    if ((openLibraryDerivedResults != null && openLibraryDerivedResults.Any()) && !enrichedList.Any())
                    {
                        _logger.LogInformation("Merging {Count} OpenLibrary-derived candidate(s) into enriched results", openLibraryDerivedResults.Count);

                        foreach (var ol in openLibraryDerivedResults)
                        {
                            // Basic dedupe: avoid adding items with same Title+Artist
                            var duplicate = enrichedList.Any(e =>
                            {
                                // Prefer exact identifier matches (OpenLibrary ID or ASIN)
                                bool idMatch = !string.IsNullOrWhiteSpace(e.Id) && !string.IsNullOrWhiteSpace(ol.Id)
                                    && string.Equals(e.Id, ol.Id, StringComparison.OrdinalIgnoreCase);
                                bool asinMatch = !string.IsNullOrWhiteSpace(e.Asin) && !string.IsNullOrWhiteSpace(ol.Asin)
                                    && string.Equals(e.Asin, ol.Asin, StringComparison.OrdinalIgnoreCase);
                                // Fallback: Title+Artist equality (defensive)
                                bool titleArtistMatch = string.Equals(e.Title ?? string.Empty, ol.Title ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(e.Artist ?? string.Empty, ol.Artist ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                                return idMatch || asinMatch || titleArtistMatch;
                            });

                            if (!duplicate)
                            {
                                enrichedList.Add(ol);
                                try { candidateDropReasons[(!string.IsNullOrWhiteSpace(ol.Asin) ? ol.Asin : ol.Id)] = "enriched_from_openlibrary"; }
                                catch (Exception caughtEx_7) when (caughtEx_7 is not OperationCanceledException && caughtEx_7 is not OutOfMemoryException && caughtEx_7 is not StackOverflowException)
                                {
                                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                }
                                _logger.LogInformation("Added OpenLibrary-derived enriched result: Title='{Title}', Artist='{Artist}'", ol.Title, ol.Artist);
                            }
                            else
                            {
                                _logger.LogDebug("Skipping duplicate OpenLibrary candidate: Title='{Title}', Artist='{Artist}'", ol.Title, ol.Artist);
                            }
                        }

                        await _searchProgressReporter.BroadcastAsync($"OpenLibrary augmentation added {openLibraryDerivedResults.Count} candidate(s)", null);

                        // Diagnostic: dump enrichedList immediately after merging OpenLibrary-derived candidates
                        try
                        {
                            var enrichedDumpList = enrichedList.Select(e => string.Format("{0} :: {1} :: {2}", e.Title ?? "<no-title>", e.MetadataSource ?? "<no-md>", string.IsNullOrWhiteSpace(e.Id) ? (e.Asin ?? "<no-id>") : e.Id));
                            var enrichedDump = string.Join(" | ", enrichedDumpList);
                            _logger.LogInformation("Enriched list after OpenLibrary merge ({Count}): {Dump}", enrichedList.Count, enrichedDump);
                        }
                        catch (Exception exDump2) when (exDump2 is not OperationCanceledException && exDump2 is not OutOfMemoryException && exDump2 is not StackOverflowException)
                        {
                            _logger.LogDebug(exDump2, "Failed to create enrichedList dump after OpenLibrary merge");
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to merge OpenLibrary-derived results into enriched list");
                }

                // Compute scores and apply deferred filtering (containment, author/publisher, fuzzy)
                var scored = new List<ScoredSearchResult>();

                foreach (var r in enrichedList)
                {
                    double containmentScore = 0.0;
                    double fuzzyScore = 0.0;

                    // Compute containment and fuzzy similarity based on title/author/description
                    try
                    {
                        containmentScore = SearchResultMatchEvaluator.ComputeContainmentScore(r, query);
                        fuzzyScore = SearchResultMatchEvaluator.ComputeFuzzySimilarity((r.Title ?? string.Empty) + " " + (r.Artist ?? string.Empty), query);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogDebug(ex, "Failed to compute containment/fuzzy scores for ASIN {Asin}", r.Asin);
                    }

                    // Use the scorer to compute comprehensive relevance score
                    var scoredResult = _searchResultScorer.ScoreResult(r, query, containmentScore, fuzzyScore);

                    // Attach computed score to the SearchResult so callers / UI can inspect it
                    try { r.Score = (int)Math.Round(scoredResult.Score * 100.0); }
                    catch (Exception caughtEx_8) when (caughtEx_8 is not OperationCanceledException && caughtEx_8 is not OutOfMemoryException && caughtEx_8 is not StackOverflowException)
                    {
                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                    }

                    scored.Add(scoredResult);
                }

                var finalList = new List<SearchResult>();

                // Now apply filtering rules
                foreach (var s in scored.OrderByDescending(s => s.Score))
                {
                    var r = s.Result;

                    // Author/publisher requirement
                    if (requireAuthorAndPublisher && (string.IsNullOrWhiteSpace(r.Artist) || string.IsNullOrWhiteSpace(r.Publisher)))
                    {
                        _logger.LogInformation("Dropping ASIN {Asin} because missing author or publisher", r.Asin);
                        continue;
                    }

                    // Containment modes
                    var keep = true;
                    if (!string.Equals(containmentMode, "Off", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.Equals(containmentMode, "Strict", StringComparison.OrdinalIgnoreCase))
                        {
                            // Require direct containment (substring) in key fields
                            var hay = string.Join(" ", new[] { r.Title, r.Artist, r.Album, r.Description, r.Publisher, r.Narrator, r.Language, r.Series }.Where(s2 => !string.IsNullOrEmpty(s2))).ToLowerInvariant();
                            if (string.IsNullOrEmpty(hay) || hay.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                keep = false;
                                _logger.LogInformation("Dropping ASIN {Asin} (Strict containment failed). containmentScore={Score}, fuzzy={Fuzzy}", r.Asin, s.ContainmentScore, s.FuzzyScore);
                            }
                        }
                        else // Relaxed
                        {
                            // If this result came from an authoritative metadata source
                            // (e.g. Audible, Audnexus, Audible scrape, OpenLibrary),
                            // treat it as authoritative and bypass the containment check.
                            var mdLower = (r.MetadataSource ?? string.Empty).ToLowerInvariant();
                            var isAuthoritative = mdLower.Contains("audible") || mdLower.Contains("audnex") || mdLower.Contains("audnexus") || mdLower.Contains("audible") || mdLower.Contains("openlibrary");

                            if (isAuthoritative)
                            {
                                keep = true;
                            }
                            else
                            {
                                // Accept if containmentScore >= 0.4 OR fuzzySimilarity >= fuzzyThreshold
                                if (s.ContainmentScore >= 0.4 || s.FuzzyScore >= fuzzyThreshold)
                                {
                                    keep = true;
                                }
                                else
                                {
                                    keep = false;
                                    _logger.LogInformation("Dropping ASIN {Asin} (Relaxed containment failed). containmentScore={Score}, fuzzy={Fuzzy}", r.Asin, s.ContainmentScore, s.FuzzyScore);
                                }
                            }
                        }
                    }

                    if (keep)
                    {
                        finalList.Add(r);
                    }

                    if (finalList.Count >= returnLimit)
                        break;
                }

                results.AddRange(finalList.Select(r => SearchResultConverters.ToMetadata(r)));
                await _searchProgressReporter.BroadcastAsync($"Returning {results.Count} final results", null);

                // If still no enriched results, OpenLibrary-derived candidates are already merged above.

                // Final filter: Keep OpenLibrary-sourced results even if they look noisy;
                // otherwise remove results with problematic titles
                results = results.Where(r =>
                    // Preserve OpenLibrary results regardless of title heuristics
                    (string.Equals(r.MetadataSource, "OpenLibrary", StringComparison.OrdinalIgnoreCase))
                    // For all other results apply the usual title checks AND ensure it looks like an audiobook
                    || (!string.IsNullOrWhiteSpace(r.Title) && !SearchValidation.IsTitleNoise(r.Title) && r.Title.Length >= 3 && SearchValidation.IsLikelyAudiobook(SearchResultConverters.ToSearchResult(r)))
                ).ToList();

                // Apply progress broadcast for filtering/scoring phase
                if (!string.IsNullOrWhiteSpace(query))
                {
                    await _searchProgressReporter.BroadcastAsync($"Filtering and scoring {results.Count} results", null);
                }

                // Sort results primarily by computed relevance score, then by metadata source priority
                results = results
                    .OrderByDescending(r => r.Score)
                    .ThenByDescending(r =>
                    {
                        if (string.IsNullOrEmpty(r.MetadataSource)) return 0;
                        var md = r.MetadataSource.ToLowerInvariant();
                        if (md.Contains("audible") || md.Contains("audnex") || md.Contains("audnexus")) return 3;
                        if (string.Equals(md, "openlibrary", StringComparison.OrdinalIgnoreCase)) return 2;
                        return 1;
                    })
                    .ToList();

                // Ensure every unified ASIN candidate has a final disposition reason for diagnostics.
                _finalDispositionLogger.LogFinalAsinDispositions(
                    asinCandidates,
                    results,
                    enrichedList,
                    candidateDropReasons,
                    query,
                    requireAuthorAndPublisher,
                    containmentMode,
                    fuzzyThreshold);

                // Diagnostic: dump final results (title :: metadataSource :: id/asin) to help correlate
                try
                {
                    var dumpList = results.Select(r => string.Format("{0} :: {1} :: {2}", r.Title ?? "<no-title>", r.MetadataSource ?? "<no-md>", string.IsNullOrWhiteSpace(r.Id) ? (r.Asin ?? "<no-id>") : r.Id));
                    var dump = string.Join(" | ", dumpList);
                    _logger.LogInformation("Final results dump for query {Query}: {Dump}", query, dump);
                }
                catch (Exception exDump) when (exDump is not OperationCanceledException && exDump is not OutOfMemoryException && exDump is not StackOverflowException)
                {
                    _logger.LogDebug(exDump, "Failed to create final results dump for query: {Query}", query);
                }

                _logger.LogInformation("Intelligent search complete. Returning {Count} filtered and sorted results for query: {Query}", results.Count, query);
                return results;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Intelligent search cancelled by request for query: {Query}", query);
                return results;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error during intelligent search for query: {Query}", query);
                return results;
            }
        }

        public async Task<List<SearchResult>> SearchByApiAsync(string apiId, string query, string? category = null)
        {
            return await _indexerSearchWorkflow.SearchByApiAsync(apiId, query, category);
        }

        public async Task<List<IndexerSearchResult>> SearchIndexerResultsAsync(string apiId, string query, string? category = null, SearchRequest? request = null)
        {
            return await _indexerSearchWorkflow.SearchIndexerResultsAsync(apiId, query, category, request);
        }

        public async Task<bool> TestApiConnectionAsync(string apiId)
        {
            return await _indexerSearchWorkflow.TestApiConnectionAsync(apiId);
        }

        internal async Task<List<IndexerSearchResult>> ParseTorznabResponseAsync(string xmlContent, Indexer indexer)
        {
            return await _indexerSearchWorkflow.ParseTorznabResponseAsync(xmlContent, indexer);
        }

        public async Task<List<ApiConfiguration>> GetEnabledMetadataSourcesAsync()
        {
            return await _metadataSourceCatalog.GetEnabledMetadataSourcesAsync();
        }
    }
}
