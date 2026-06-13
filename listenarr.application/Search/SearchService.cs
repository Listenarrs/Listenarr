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
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models.Configurations;
using Listenarr.Domain.Models;
using Listenarr.Application.Interfaces.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Listenarr.Application.Notification;
using Listenarr.Application.Metadata;
using Listenarr.Application.Security;

namespace Listenarr.Application.Search
{
    public class SearchService : ISearchService
    {
        private readonly IConfigurationService _configurationService;
        private readonly ILogger<SearchService> _logger;
        private readonly AudibleService _audibleService;
        private readonly MetadataConverters _metadataConverters;
        private readonly SearchProgressReporter _searchProgressReporter;
        private readonly AsinCandidateCollector _asinCandidateCollector;
        private readonly AsinEnricher _asinEnricher;
        private readonly SearchResultScorerService _searchResultScorer;
        private readonly SearchResultSortingService _searchResultSorting;
        private readonly AsinSearchHandler _asinSearchHandler;
        private readonly IMemoryCache? _cache;
        private readonly ICoverImageProbe? _coverImageProbe;
        private readonly IndexerSearchWorkflow _indexerSearchWorkflow;
        private readonly MetadataSourceCatalog _metadataSourceCatalog;

        public SearchService(
            HttpClient httpClient,
            IConfigurationService configurationService,
            ILogger<SearchService> logger,
            IIndexerRepository indexerRepository,
            IApiConfigurationRepository apiConfigRepository,
            AudibleService audibleService,
            MetadataConverters metadataConverters,
            SearchProgressReporter searchProgressReporter,
            AsinCandidateCollector asinCandidateCollector,
            AsinEnricher asinEnricher,
            SearchResultScorerService searchResultScorer,
            SearchResultSortingService searchResultSorting,
            AsinSearchHandler asinSearchHandler,
            IEnumerable<IIndexerSearchProvider>? searchProviders = null,
            IMemoryCache? cache = null,
            ICoverImageProbe? coverImageProbe = null,
            IHtmlTextExtractor? htmlTextExtractor = null,
            IndexerSearchWorkflow? indexerSearchWorkflow = null,
            MetadataSourceCatalog? metadataSourceCatalog = null)
        {
            _configurationService = configurationService;
            _logger = logger;
            _audibleService = audibleService;
            _metadataConverters = metadataConverters;
            _searchProgressReporter = searchProgressReporter;
            _asinCandidateCollector = asinCandidateCollector;
            _asinEnricher = asinEnricher;
            var resolvedSearchProviders = searchProviders ?? Enumerable.Empty<IIndexerSearchProvider>();
            _searchResultScorer = searchResultScorer;
            _searchResultSorting = searchResultSorting;
            _asinSearchHandler = asinSearchHandler;
            _cache = cache;
            _coverImageProbe = coverImageProbe;
            _indexerSearchWorkflow = indexerSearchWorkflow ?? new IndexerSearchWorkflow(
                httpClient,
                configurationService,
                indexerRepository,
                resolvedSearchProviders,
                new IndexerAdditionalSettingsParser(NullLogger<IndexerAdditionalSettingsParser>.Instance),
                NullLogger<IndexerSearchWorkflow>.Instance,
                htmlTextExtractor);
            _metadataSourceCatalog = metadataSourceCatalog ?? new MetadataSourceCatalog(
                apiConfigRepository,
                NullLogger<MetadataSourceCatalog>.Instance);
        }

        public async Task<List<SearchResult>> SearchAsync(string query, string? category = null, List<string>? apiIds = null, SearchSortBy sortBy = SearchSortBy.Seeders, SearchSortDirection sortDirection = SearchSortDirection.Descending, bool isAutomaticSearch = false)
        {
            var results = new List<SearchResult>();

            // Diagnostic log to help trace which callers invoke automatic vs interactive searches
            _logger.LogInformation("SearchAsync called. Query='{Query}', isAutomaticSearch={IsAutomaticSearch}", LogRedaction.SanitizeText(query), isAutomaticSearch);

            // For automatic search, only search indexers - skip Amazon/Audible entirely
            if (isAutomaticSearch)
            {
                var automaticIndexerResults = await SearchIndexersAsync(query, category, sortBy, sortDirection, isAutomaticSearch);
                if (automaticIndexerResults.Any())
                {
                    results.AddRange(automaticIndexerResults.Select((IndexerSearchResult r) => SearchResultConverters.ToSearchResult(r)));
                    _logger.LogInformation("Found {Count} indexer results for automatic search query: {Query}", automaticIndexerResults.Count, LogRedaction.SanitizeText(query));
                }
                else
                {
                    _logger.LogInformation("No indexer results found for automatic search query: {Query}", LogRedaction.SanitizeText(query));
                }
                return await _searchResultSorting.ApplySortingAsync(results, sortBy, sortDirection);
            }

            // For manual/interactive search, use intelligent search (Audible/Audnexus/OpenLibrary) + indexers
            var intelligentResults = await IntelligentSearchAsync(query);
            if (intelligentResults.Any())
            {
                results.AddRange(intelligentResults.Select((MetadataSearchResult r) => SearchResultConverters.ToSearchResult(r)));
                _logger.LogInformation("Found {Count} valid metadata results using intelligent search for query: {Query}", intelligentResults.Count, LogRedaction.SanitizeText(query));
            }
            else
            {
                _logger.LogInformation("No metadata results found for query: {Query}", LogRedaction.SanitizeText(query));
            }

            // Also search configured indexers for additional results (including DDL downloads)
            var indexerResults = await SearchIndexersAsync(query, category, sortBy, sortDirection, isAutomaticSearch);
            if (indexerResults.Any())
            {
                results.AddRange(indexerResults.Select(r => SearchResultConverters.ToSearchResult(r)));
                _logger.LogInformation("Added {Count} indexer results (including DDL downloads) for query: {Query}", indexerResults.Count, LogRedaction.SanitizeText(query));
            }

            return await _searchResultSorting.ApplySortingAsync(results, sortBy, sortDirection);
        }

        // Prowlarr-style composite scoring helpers adapted for Listenarr
        internal double CalculateProwlarrStyleScore(SearchResult result, Indexer? indexer = null)
        {
            var composite = CompositeScorer.CalculateProwlarrStyleScore(result, indexer, _logger);
            return composite.Total;
        }

        public async Task<List<IndexerSearchResult>> SearchIndexersAsync(string query, string? category = null, SearchSortBy sortBy = SearchSortBy.Seeders, SearchSortDirection sortDirection = SearchSortDirection.Descending, bool isAutomaticSearch = false, SearchRequest? request = null)
        {
            return await _indexerSearchWorkflow.SearchIndexersAsync(query, category, sortBy, sortDirection, isAutomaticSearch, request);
        }

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

                    // ISBN
                    if (searchType == "ISBN" && !string.IsNullOrEmpty(isbnVal))
                    {
                        var amRes = await _audibleService.SearchByIsbnAsync(isbnVal, 1, 50, region, language);
                        if (amRes?.Results != null && amRes.Results.Any())
                        {
                            var converted = new List<SearchResult>();
                            var amFiltered = amRes.Results.AsEnumerable();
                            if (!string.IsNullOrWhiteSpace(language)) amFiltered = amFiltered.Where(b => !string.IsNullOrWhiteSpace(b.Language) && string.Equals(b.Language, language, StringComparison.OrdinalIgnoreCase));
                            foreach (var book in amFiltered.Where(book => !string.IsNullOrWhiteSpace(book.Asin)))
                            {
                                var bookResp = new AudibleBookResponse
                                {
                                    Asin = book.Asin,
                                    Title = book.Title,
                                    Subtitle = book.Subtitle,
                                    Authors = book.Authors,
                                    ImageUrl = book.ImageUrl,
                                    Language = book.Language,
                                    BookFormat = book.BookFormat,
                                    Genres = book.Genres,
                                    Series = book.Series,
                                    Publisher = book.Publisher,
                                    Narrators = book.Narrators,
                                    ReleaseDate = book.ReleaseDate,
                                    Isbn = book.Isbn
                                };
                                var meta = _metadataConverters.ConvertAudibleToMetadata(bookResp, book.Asin!, "Audible");
                                var sr = await _metadataConverters.ConvertMetadataToSearchResultAsync(meta, book.Asin!);
                                sr.IsEnriched = true;
                                sr.MetadataSource = "Audible";
                                converted.Add(sr);
                            }
                            if (converted.Any()) return SearchResultConverters.ToMetadataList(converted);
                        }
                    }

                    // AUTHOR-only
                    if (searchType == "AUTHOR" && !string.IsNullOrEmpty(authorVal))
                    {
                        // Aggregate multiple pages from Audible until we reach candidateLimit
                        var aggregated = new List<AudibleSearchResult>();
                        int page = 1;
                        int pageSize = Math.Min(50, Math.Max(10, candidateLimit));
                        // For Audible author listings, do not artificially cap aggregation
                        // by the Amazon candidateLimit. Instead, fetch pages until a
                        // page returns fewer than pageSize results (natural end).
                        int maxPages = int.MaxValue;
                        for (; page <= maxPages; page++)
                        {
                            try
                            {
                                var pageRes = await _audibleService.SearchByAuthorAsync(authorVal, page, pageSize, region, language);
                                var pageCount = pageRes?.Results?.Count ?? 0;
                                aggregated.AddRange(pageRes?.Results ?? Enumerable.Empty<AudibleSearchResult>());
                                _logger.LogInformation("Audible author page {Page} returned {PageCount} results (aggregated {AggregatedCount}) for author '{Author}'", page, pageCount, aggregated.Count, authorVal);
                                if (pageRes?.Results == null || pageCount == 0)
                                {
                                    _logger.LogInformation("Stopping aggregation: page {Page} returned no results for author '{Author}'", page, authorVal);
                                    break;
                                }
                                if (pageCount < pageSize)
                                {
                                    _logger.LogInformation("Stopping aggregation: page {Page} result count {PageCount} < pageSize {PageSize}", page, pageCount, pageSize);
                                    break; // last page
                                }
                                // Do not stop aggregating based on candidateLimit for audible
                            }
                            catch (Exception exPage) when (exPage is not OperationCanceledException && exPage is not OutOfMemoryException && exPage is not StackOverflowException)
                            {
                                _logger.LogDebug(exPage, "Failed fetching audible author page {Page} for author {Author}", page, authorVal);
                                break;
                            }
                        }

                        _logger.LogInformation("Finished aggregating author pages for '{Author}': total aggregated={AggregatedCount}, candidateLimit={CandidateLimit}, pageSize={PageSize}, maxPages={MaxPages}", authorVal, aggregated.Count, candidateLimit, pageSize, maxPages);
                        if (aggregated.Any())
                        {
                            // Deduplicate results based on ASIN to prevent repeated books across pages
                            var deduplicated = aggregated
                                .Where(b => !string.IsNullOrWhiteSpace(b.Asin))
                                .GroupBy(b => b.Asin, StringComparer.OrdinalIgnoreCase)
                                .Select(g => g.First())
                                .ToList();

                            _logger.LogInformation("Deduplicated author results for '{Author}': {OriginalCount} -> {DeduplicatedCount}", authorVal, aggregated.Count, deduplicated.Count);

                            var converted = new List<SearchResult>();
                            var authorFiltered = deduplicated.AsEnumerable();
                            if (!string.IsNullOrWhiteSpace(language)) authorFiltered = authorFiltered.Where(b => !string.IsNullOrWhiteSpace(b.Language) && string.Equals(b.Language, language, StringComparison.OrdinalIgnoreCase));
                            foreach (var book in authorFiltered.Where(book => !string.IsNullOrWhiteSpace(book.Asin)))
                            {
                                var bookResp = new AudibleBookResponse
                                {
                                    Asin = book.Asin,
                                    Title = book.Title,
                                    Subtitle = book.Subtitle,
                                    Authors = book.Authors,
                                    ImageUrl = book.ImageUrl,
                                    Language = book.Language,
                                    BookFormat = book.BookFormat,
                                    Genres = book.Genres,
                                    Series = book.Series,
                                    Publisher = book.Publisher,
                                    Narrators = book.Narrators,
                                    ReleaseDate = book.ReleaseDate
                                };
                                var meta = _metadataConverters.ConvertAudibleToMetadata(bookResp, book.Asin!, "Audible");
                                var sr = await _metadataConverters.ConvertMetadataToSearchResultAsync(meta, book.Asin!);
                                sr.IsEnriched = true;
                                sr.MetadataSource = "Audible";
                                converted.Add(sr);
                            }
                            if (converted.Any()) return SearchResultConverters.ToMetadataList(converted);
                        }
                    }

                    // AUTHOR + TITLE: prefer author endpoint then filter by title/isbn to ensure consistent Audible enrichment
                    if (searchType == "AUTHOR_TITLE" && !string.IsNullOrEmpty(authorVal))
                    {
                        try { _logger.LogInformation("Entering AUTHOR_TITLE branch: author='{Author}', title='{Title}', isbn='{Isbn}'", authorVal, titleVal, isbnVal); }
                        catch (Exception caughtEx_3) when (caughtEx_3 is not OperationCanceledException && caughtEx_3 is not OutOfMemoryException && caughtEx_3 is not StackOverflowException)
                        {
                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                        }
                        // Aggregate author pages up to candidateLimit to enrich matching
                        var aggregated = new List<AudibleSearchResult>();
                        int page = 1;
                        int pageSize = Math.Min(50, Math.Max(10, candidateLimit));
                        // For Audible author/title combined flows, allow full aggregation
                        // across available pages; we will narrow/return a bounded set later.
                        int maxPages = int.MaxValue;
                        for (; page <= maxPages; page++)
                        {
                            try
                            {
                                var pageRes = await _audibleService.SearchByAuthorAsync(authorVal, page, pageSize, region, language);
                                var pageCount = pageRes?.Results?.Count ?? 0;
                                aggregated.AddRange(pageRes?.Results ?? Enumerable.Empty<AudibleSearchResult>());
                                _logger.LogInformation("Audible AUTHOR_TITLE: page {Page} returned {PageCount} results (aggregated {AggregatedCount}) for author '{Author}'", page, pageCount, aggregated.Count, authorVal);
                                if (pageRes?.Results == null || pageCount == 0)
                                {
                                    _logger.LogInformation("Audible AUTHOR_TITLE: stopping aggregation — page {Page} returned no results", page);
                                    break;
                                }
                                if (pageCount < pageSize)
                                {
                                    _logger.LogInformation("Audible AUTHOR_TITLE: stopping aggregation — page {Page} count {PageCount} < pageSize {PageSize}", page, pageCount, pageSize);
                                    break;
                                }
                            }
                            catch (Exception exPage) when (exPage is not OperationCanceledException && exPage is not OutOfMemoryException && exPage is not StackOverflowException)
                            {
                                _logger.LogDebug(exPage, "Failed fetching audible author page {Page} for author {Author}", page, authorVal);
                                break;
                            }
                        }
                        _logger.LogInformation("Audible AUTHOR_TITLE: finished aggregating pages for '{Author}': aggregated={AggregatedCount}, pageSize={PageSize}, maxPages={MaxPages}", authorVal, aggregated.Count, pageSize, maxPages);
                        if (aggregated?.Any() == true)
                        {
                            // Deduplicate results based on ASIN to prevent repeated books across pages
                            var deduplicated = aggregated
                                .Where(b => !string.IsNullOrWhiteSpace(b.Asin))
                                .GroupBy(b => b.Asin, StringComparer.OrdinalIgnoreCase)
                                .Select(g => g.First())
                                .ToList();

                            _logger.LogInformation("Deduplicated AUTHOR_TITLE results for '{Author}': {OriginalCount} -> {DeduplicatedCount}", authorVal, aggregated.Count, deduplicated.Count);

                            try { _logger.LogInformation("Audible author lookup returned {Count} aggregated results for author '{Author}'", deduplicated.Count, authorVal); }
                            catch (Exception caughtEx_4) when (caughtEx_4 is not OperationCanceledException && caughtEx_4 is not OutOfMemoryException && caughtEx_4 is not StackOverflowException)
                            {
                                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                            }

                            // Use the lightweight author/books results to perform title filtering
                            // and avoid fetching detailed metadata for every ASIN. Only fetch
                            // detailed metadata when an ISBN lookup is explicitly required or
                            // when we need to enrich a small set of final matches.
                            var authorFiltered = deduplicated.AsEnumerable();
                            if (!string.IsNullOrWhiteSpace(language)) authorFiltered = authorFiltered.Where(b => !string.IsNullOrWhiteSpace(b.Language) && string.Equals(b.Language, language, StringComparison.OrdinalIgnoreCase));

                            // Title-based filtering can be done directly against the author results
                            if (!string.IsNullOrEmpty(titleVal))
                            {
                                authorFiltered = authorFiltered.Where(b =>
                                    (!string.IsNullOrWhiteSpace(b.Title) && b.Title.IndexOf(titleVal, StringComparison.OrdinalIgnoreCase) >= 0) ||
                                    (!string.IsNullOrWhiteSpace(b.Subtitle) && b.Subtitle.IndexOf(titleVal, StringComparison.OrdinalIgnoreCase) >= 0)
                                );
                            }

                            // If an ISBN was provided we must match against detailed metadata;
                            // instead of fetching metadata for every ASIN, scan a limited set
                            // of candidates and only fetch metadata until we find ISBN matches.
                            var detailedMetaByAsin = new Dictionary<string, AudibleBookResponse>(StringComparer.OrdinalIgnoreCase);
                            if (!string.IsNullOrEmpty(isbnVal))
                            {
                                // Limit how many author results to scan for ISBNs to avoid huge loads
                                var isbnScanLimit = Math.Min(200, Math.Max(50, candidateLimit));
                                var scanCandidates = aggregated.Where(r => !string.IsNullOrWhiteSpace(r.Asin)).Take(isbnScanLimit).ToList();
                                try { _logger.LogInformation("Scanning up to {Limit} author candidates for ISBN {Isbn}", scanCandidates.Count, isbnVal); }
                                catch (Exception caughtEx_5) when (caughtEx_5 is not OperationCanceledException && caughtEx_5 is not OutOfMemoryException && caughtEx_5 is not StackOverflowException)
                                {
                                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                }
                                foreach (var c in scanCandidates.Where(c => !string.IsNullOrWhiteSpace(c.Asin)))
                                {
                                    try
                                    {
                                        var meta = await _audibleService.GetBookMetadataAsync(c.Asin!, region, true, language);
                                        if (meta == null) continue;
                                        detailedMetaByAsin[c.Asin!] = meta;
                                        if (!string.IsNullOrWhiteSpace(meta.Isbn) && string.Equals(meta.Isbn.Trim(), isbnVal, StringComparison.OrdinalIgnoreCase))
                                        {
                                            // Narrow authorFiltered to only matching ASINs
                                            authorFiltered = authorFiltered.Where(r => !string.IsNullOrWhiteSpace(r.Asin) && string.Equals(r.Asin, c.Asin, StringComparison.OrdinalIgnoreCase));
                                            break; // stop scanning once we found the ISBN match
                                        }
                                    }
                                    catch (Exception exMeta) when (exMeta is not OperationCanceledException && exMeta is not OutOfMemoryException && exMeta is not StackOverflowException)
                                    {
                                        _logger.LogDebug(exMeta, "Failed fetching audible metadata for ASIN {Asin} while scanning for ISBN", c.Asin);
                                    }
                                }
                            }

                            try { _logger.LogInformation("[DBG] authorFiltered count after language/title/isbn filtering: {Count}", authorFiltered.Count()); }
                            catch (Exception caughtEx_6) when (caughtEx_6 is not OperationCanceledException && caughtEx_6 is not OutOfMemoryException && caughtEx_6 is not StackOverflowException)
                            {
                                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                            }

                            var converted = await AudibleSearchResultMapper.ConvertToSearchResultsAsync(
                                authorFiltered,
                                _metadataConverters,
                                detailedMetaByAsin,
                                _logger,
                                continueOnConversionError: true);

                            if (converted.Any()) return SearchResultConverters.ToMetadataList(converted);
                        }
                    }

                    // TITLE-only
                    if (searchType == "TITLE" && !string.IsNullOrEmpty(titleVal))
                    {
                        var titleRes = await _audibleService.SearchByTitleAsync(titleVal, 1, 50, region, language);
                        if (titleRes?.Results != null && titleRes.Results.Any())
                        {
                            var titleFiltered = titleRes.Results.AsEnumerable();
                            if (!string.IsNullOrWhiteSpace(language)) titleFiltered = titleFiltered.Where(b => string.IsNullOrWhiteSpace(b.Language) || string.Equals(b.Language, language, StringComparison.OrdinalIgnoreCase));
                            var converted = await AudibleSearchResultMapper.ConvertToSearchResultsAsync(
                                titleFiltered,
                                _metadataConverters);
                            if (converted.Any()) return SearchResultConverters.ToMetadataList(converted);
                        }

                    }

                    // General/simple query - try audible search endpoint first
                    if (string.IsNullOrWhiteSpace(searchType) && !string.IsNullOrWhiteSpace(actualQuery))
                    {
                        var simpleRes = await _audibleService.SearchBooksAsync(actualQuery, 1, 50, region, language);
                        if (simpleRes?.Results != null && simpleRes.Results.Any())
                        {
                            var simpleFiltered = simpleRes.Results.AsEnumerable();
                            if (!string.IsNullOrWhiteSpace(language)) simpleFiltered = simpleFiltered.Where(b => string.IsNullOrWhiteSpace(b.Language) || string.Equals(b.Language, language, StringComparison.OrdinalIgnoreCase));
                            var converted = await AudibleSearchResultMapper.ConvertToSearchResultsAsync(
                                simpleFiltered,
                                _metadataConverters);
                            if (converted.Any()) return SearchResultConverters.ToMetadataList(converted);
                        }
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
                try
                {
                    var finalAsinEntries = new List<string>();

                    foreach (var asin in asinCandidates.Where(asin => !string.IsNullOrWhiteSpace(asin)))
                    {
                        // If already accepted in the final results, mark as accepted
                        if (results.Any(r => string.Equals(r.Asin, asin, StringComparison.OrdinalIgnoreCase)))
                        {
                            try { candidateDropReasons[asin] = "accepted"; }
                            catch (Exception caughtEx_9) when (caughtEx_9 is not OperationCanceledException && caughtEx_9 is not OutOfMemoryException && caughtEx_9 is not StackOverflowException)
                            {
                                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                            }
                            finalAsinEntries.Add($"{asin}:accepted");
                            continue;
                        }

                        // If we have an enriched version but it didn't make the final list, try to compute a specific drop reason
                        var enrichedCandidate = enrichedList.FirstOrDefault(e => string.Equals(e.Asin, asin, StringComparison.OrdinalIgnoreCase));
                        if (enrichedCandidate != null)
                        {
                            // Author/publisher requirement
                            if (requireAuthorAndPublisher && (string.IsNullOrWhiteSpace(enrichedCandidate.Artist) || string.IsNullOrWhiteSpace(enrichedCandidate.Publisher)))
                            {
                                try { candidateDropReasons[asin] = "author_publisher_missing"; }
                                catch (Exception caughtEx_10) when (caughtEx_10 is not OperationCanceledException && caughtEx_10 is not OutOfMemoryException && caughtEx_10 is not StackOverflowException)
                                {
                                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                }
                                finalAsinEntries.Add($"{asin}:author_publisher_missing");
                                continue;
                            }

                            // Title noise or unlikely audiobook
                            if (SearchValidation.IsTitleNoise(enrichedCandidate.Title) || !SearchValidation.IsLikelyAudiobook(enrichedCandidate))
                            {
                                try { candidateDropReasons[asin] = "filtered_title_or_not_likely"; }
                                catch (Exception caughtEx_11) when (caughtEx_11 is not OperationCanceledException && caughtEx_11 is not OutOfMemoryException && caughtEx_11 is not StackOverflowException)
                                {
                                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                }
                                finalAsinEntries.Add($"{asin}:filtered_title_or_not_likely");
                                continue;
                            }

                            // Containment / fuzzy failure
                            var containment = 0.0;
                            var fuzzy = 0.0;
                            try
                            {
                                containment = SearchResultMatchEvaluator.ComputeContainmentScore(enrichedCandidate, query);
                                fuzzy = SearchResultMatchEvaluator.ComputeFuzzySimilarity(enrichedCandidate.Title + " " + enrichedCandidate.Artist, query);
                            }
                            catch (Exception caughtEx_12) when (caughtEx_12 is not OperationCanceledException && caughtEx_12 is not OutOfMemoryException && caughtEx_12 is not StackOverflowException)
                            {
                                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                            }

                            if (string.Equals(containmentMode, "Strict", StringComparison.OrdinalIgnoreCase))
                            {
                                // In strict mode we require direct containment
                                var hay = string.Join(" ", new[] { enrichedCandidate.Title, enrichedCandidate.Artist, enrichedCandidate.Album, enrichedCandidate.Description, enrichedCandidate.Publisher, enrichedCandidate.Narrator, enrichedCandidate.Language, enrichedCandidate.Series }.Where(s => !string.IsNullOrEmpty(s))).ToLowerInvariant();
                                if (string.IsNullOrEmpty(hay) || hay.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                                {
                                    try { candidateDropReasons[asin] = "containment_failed_strict"; }
                                    catch (Exception caughtEx_13) when (caughtEx_13 is not OperationCanceledException && caughtEx_13 is not OutOfMemoryException && caughtEx_13 is not StackOverflowException)
                                    {
                                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                    }
                                    finalAsinEntries.Add($"{asin}:containment_failed_strict");
                                    continue;
                                }
                            }
                            else
                            {
                                if (containment < 0.4 && fuzzy < fuzzyThreshold)
                                {
                                    try { candidateDropReasons[asin] = "containment_failed_relaxed"; }
                                    catch (Exception caughtEx_14) when (caughtEx_14 is not OperationCanceledException && caughtEx_14 is not OutOfMemoryException && caughtEx_14 is not StackOverflowException)
                                    {
                                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                    }
                                    finalAsinEntries.Add($"{asin}:containment_failed_relaxed");
                                    continue;
                                }
                            }

                            // If none of the above matched, mark as filtered by post-scoring rules
                            try { candidateDropReasons[asin] = "filtered_post_scoring"; }
                            catch (Exception caughtEx_15) when (caughtEx_15 is not OperationCanceledException && caughtEx_15 is not OutOfMemoryException && caughtEx_15 is not StackOverflowException)
                            {
                                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                            }
                            finalAsinEntries.Add($"{asin}:filtered_post_scoring");
                            continue;
                        }

                        // If we reached here, the ASIN never got enriched nor scraped successfully
                        if (!candidateDropReasons.ContainsKey(asin))
                        {
                            try { candidateDropReasons[asin] = "no_metadata_and_no_scrape"; }
                            catch (Exception caughtEx_16) when (caughtEx_16 is not OperationCanceledException && caughtEx_16 is not OutOfMemoryException && caughtEx_16 is not StackOverflowException)
                            {
                                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                            }
                        }
                        finalAsinEntries.Add($"{asin}:{candidateDropReasons.GetValueOrDefault(asin)}");
                    }

                    // Emit a consolidated diagnostic log with per-ASIN dispositions
                    if (finalAsinEntries.Any())
                    {
                        _logger.LogInformation("Final ASIN dispositions for query '{Query}': {Entries}", query, string.Join(", ", finalAsinEntries));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to compute final ASIN dispositions for query: {Query}", query);
                }

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

        private static bool isOpenLibraryResult(SearchResult r)
        {
            return string.Equals(r?.MetadataSource, "OpenLibrary", StringComparison.OrdinalIgnoreCase);
        }

        // Try to pick the best cover URL from a list of OpenLibrary cover IDs by measuring image aspect ratios.
        // Returns a full covers.openlibrary.org URL or null on failure.
        private async Task<string?> PickBestCoverUrlAsync(List<int> coverIds)
        {
            if (coverIds == null || !coverIds.Any()) return null;

            double bestDelta = double.MaxValue;
            string? bestUrl = null;

            foreach (var cid in coverIds)
            {
                try
                {
                    var url = $"https://covers.openlibrary.org/b/id/{cid}-L.jpg";
                    var dimensions = _coverImageProbe == null ? null : await _coverImageProbe.ProbeAsync(url);
                    if (dimensions == null || dimensions.Value.Height == 0) continue;

                    var ratio = (double)dimensions.Value.Width / dimensions.Value.Height;
                    var delta = Math.Abs(ratio - 1.0);
                    if (delta < bestDelta)
                    {
                        bestDelta = delta;
                        bestUrl = url;
                    }

                    if (Math.Abs(delta) < 0.01)
                        break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogDebug(ex, "Failed to fetch cover image for id {Id}", cid);
                    continue;
                }
            }

            return bestUrl;
        }


        private int? ParseDuration(string? duration)
        {
            if (string.IsNullOrEmpty(duration)) return null;

            try
            {
                // Try to extract hours and minutes from duration string
                var hoursMatch = System.Text.RegularExpressions.Regex.Match(duration, @"(\d+)\s*hrs?");
                var minutesMatch = System.Text.RegularExpressions.Regex.Match(duration, @"(\d+)\s*mins?");

                int totalMinutes = 0;

                if (hoursMatch.Success)
                {
                    totalMinutes += int.Parse(hoursMatch.Groups[1].Value) * 60;
                }

                if (minutesMatch.Success)
                {
                    totalMinutes += int.Parse(minutesMatch.Groups[1].Value);
                }

                return totalMinutes > 0 ? totalMinutes : null;
            }
            catch (Exception caughtEx_17) when (caughtEx_17 is not OperationCanceledException && caughtEx_17 is not OutOfMemoryException && caughtEx_17 is not StackOverflowException)
            {
                return null;
            }
        }

        private async Task<List<SearchResult>> TraditionalSearchAsync(string query, string? category = null, List<string>? apiIds = null)
        {
            var results = new List<SearchResult>();
            var apis = await _configurationService.GetApiConfigurationsAsync();

            if (apiIds != null && apiIds.Any())
            {
                apis = apis.Where(a => apiIds.Contains(a.Id)).ToList();
            }

            var enabledApis = apis.Where(a => a.IsEnabled).OrderBy(a => a.Priority).ToList();

            var searchTasks = enabledApis.Select(api => SearchByApiAsync(api.Id, query, category));
            var apiResults = await Task.WhenAll(searchTasks);

            foreach (var apiResult in apiResults)
            {
                foreach (var result in apiResult)
                {
                    results.Add(result);
                }
            }

            return results;
        }

        private string ExtractAsin(string magnetLink)
        {
            // TODO: Implement ASIN extraction logic from magnet/torrent/nzb or other property
            // For now, return empty string
            return string.Empty;
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

        private List<SearchResult> GenerateMockResults(string query, string source)
        {
            // This is mock data for development purposes
            return new List<SearchResult>
            {
                new SearchResult
                {
                    Id = Guid.NewGuid().ToString(),
                    Title = $"Sample Audiobook - {query}",
                    Artist = "Sample Author",
                    Album = "Sample Series Book 1",
                    Category = "Audiobook",
                    Size = 512_000_000, // 512 MB
                    Seeders = 25,
                    Leechers = 3,
                    MagnetLink = "magnet:?xt=urn:btih:sample",
                    Source = source,
                    PublishedDate = DateTime.UtcNow.AddDays(-Random.Shared.Next(1, 365)).ToString("o"),
                    Quality = "MP3 128kbps",
                    Format = "MP3"
                }
            };
        }


        public async Task<List<ApiConfiguration>> GetEnabledMetadataSourcesAsync()
        {
            return await _metadataSourceCatalog.GetEnabledMetadataSourcesAsync();
        }
    }
}
