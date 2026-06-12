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

using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models.Configurations;
using Listenarr.Domain.Models;
using Listenarr.Application.Interfaces.Repositories;
using Microsoft.Extensions.Logging;
using Listenarr.Application.Notification;
using Listenarr.Application.Metadata;
using Listenarr.Application.Security;

namespace Listenarr.Application.Search
{
    public class SearchService : ISearchService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfigurationService _configurationService;
        private readonly ILogger<SearchService> _logger;
        private readonly IIndexerRepository _indexerRepository;
        private readonly IApiConfigurationRepository _apiConfigRepository;
        private readonly AudibleService _audibleService;
        private readonly MetadataConverters _metadataConverters;
        private readonly SearchProgressReporter _searchProgressReporter;
        private readonly AsinCandidateCollector _asinCandidateCollector;
        private readonly AsinEnricher _asinEnricher;
        private readonly SearchResultScorerService _searchResultScorer;
        private readonly SearchResultSortingService _searchResultSorting;
        private readonly AsinSearchHandler _asinSearchHandler;
        private readonly IMemoryCache? _cache;
        private readonly IEnumerable<IIndexerSearchProvider> _searchProviders;
        private readonly ICoverImageProbe? _coverImageProbe;
        private readonly IHtmlTextExtractor? _htmlTextExtractor;

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
            IHtmlTextExtractor? htmlTextExtractor = null)
        {
            _httpClient = httpClient;
            _configurationService = configurationService;
            _logger = logger;
            _indexerRepository = indexerRepository;
            _apiConfigRepository = apiConfigRepository;
            _audibleService = audibleService;
            _metadataConverters = metadataConverters;
            _searchProgressReporter = searchProgressReporter;
            _asinCandidateCollector = asinCandidateCollector;
            _asinEnricher = asinEnricher;
            _searchProviders = searchProviders ?? Enumerable.Empty<IIndexerSearchProvider>();
            _searchResultScorer = searchResultScorer;
            _searchResultSorting = searchResultSorting;
            _asinSearchHandler = asinSearchHandler;
            _cache = cache;
            _coverImageProbe = coverImageProbe;
            _htmlTextExtractor = htmlTextExtractor;
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
            var results = new List<IndexerSearchResult>();
            var indexers = await _indexerRepository.GetEnabledAsync(isAutomaticSearch);

            _logger.LogInformation("Searching {Count} enabled indexers for query: {Query}", indexers.Count, query);

            // If no indexers are configured, return mock data for development
            if (!indexers.Any())
            {
                _logger.LogWarning("No indexers configured, returning mock results for query: {Query}", query);
                return GenerateMockIndexerResults(query);
            }

            // Search all enabled indexers in parallel
            var searchTasks = indexers.Select(async indexer =>
            {
                try
                {
                    _logger.LogInformation("Searching indexer {Name} ({Type}) for query: {Query}", indexer.Name, indexer.Type, query);
                    // Apply indexer-level MyAnonamouse options if not provided explicitly on the request
                    var perIndexerRequest = request;
                    if (perIndexerRequest?.MyAnonamouse == null)
                    {
                        var mam = ParseMamOptionsFromAdditionalSettings(indexer.AdditionalSettings);
                        if (mam != null)
                        {
                            perIndexerRequest ??= new SearchRequest();
                            perIndexerRequest.MyAnonamouse = mam;
                        }
                    }

                    var indexerResults = await SearchIndexerAsync(indexer, query, category, perIndexerRequest);
                    _logger.LogInformation("Found {Count} results from indexer {Name}", indexerResults.Count, indexer.Name);
                    return indexerResults;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Error searching indexer {Name} for query: {Query}", indexer.Name, query);
                    return new List<IndexerSearchResult>();
                }
            }).ToList();

            var indexerResults = await Task.WhenAll(searchTasks);

            // Flatten all results
            foreach (var indexerResult in indexerResults)
            {
                results.AddRange(indexerResult);
            }

            _logger.LogInformation("Total {Count} results from all indexers for query: {Query}", results.Count, query);

            // Sort by seeders (descending) then by date - treat missing/null seeders as 0 so usenet results sort consistently
            return results.OrderByDescending(r => r.Seeders ?? 0).ThenByDescending(r => r.PublishedDate).ToList();
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
            try
            {
                Indexer? indexer = null;

                // Try parsing apiId as numeric indexer ID first
                indexer = int.TryParse(apiId, out var indexerId)
                    ? await _indexerRepository.GetByIdAsync(indexerId)
                    : await _indexerRepository.GetByNameAsync(apiId);

                if (indexer == null)
                {
                    _logger.LogWarning("Indexer not found for apiId: {ApiId}", apiId);
                    return new List<SearchResult>();
                }

                if (!indexer.IsEnabled)
                {
                    _logger.LogWarning("Indexer {IndexerName} (apiId: {ApiId}) is not enabled", indexer.Name, apiId);
                    return new List<SearchResult>();
                }

                // By default, reuse existing SearchIndexerAsync for a SearchResult response
                var req = new SearchRequest();
                // If this indexer has MyAnonamouse options encoded in AdditionalSettings, apply them
                var mamOpts = ParseMamOptionsFromAdditionalSettings(indexer.AdditionalSettings);
                if (mamOpts != null) req.MyAnonamouse = mamOpts;

                var idxResults = await SearchIndexerAsync(indexer, query, category, req);
                return idxResults.Select(r => SearchResultConverters.ToSearchResult(r)).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, $"Error searching indexer {apiId} for query: {query}");
                return new List<SearchResult>();
            }
        }

        public async Task<List<IndexerSearchResult>> SearchIndexerResultsAsync(string apiId, string query, string? category = null, SearchRequest? request = null)
        {
            try
            {
                Indexer? indexer = null;

                indexer = int.TryParse(apiId, out var indexerId)
                    ? await _indexerRepository.GetByIdAsync(indexerId)
                    : await _indexerRepository.GetByNameAsync(apiId);

                if (indexer == null || !indexer.IsEnabled)
                {
                    _logger.LogWarning("Indexer not found or disabled for apiId: {ApiId}", apiId);
                    return new List<IndexerSearchResult>();
                }

                // Apply MyAnonamouse options from indexer if not provided explicitly
                if (request?.MyAnonamouse == null)
                {
                    var mam = ParseMamOptionsFromAdditionalSettings(indexer.AdditionalSettings);
                    if (mam != null)
                    {
                        request ??= new SearchRequest();
                        request.MyAnonamouse = mam;
                    }
                }

                var idxResults = await SearchIndexerAsync(indexer, query, category, request);
                return idxResults;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, $"Error searching indexer {apiId} for query: {query}");
                return new List<IndexerSearchResult>();
            }
        }

        private MyAnonamouseOptions? ParseMamOptionsFromAdditionalSettings(string? additional)
        {
            if (string.IsNullOrWhiteSpace(additional)) return null;
            try
            {
                using var doc = JsonDocument.Parse(additional);
                var root = doc.RootElement;
                // Expect either { mam_id: '...', mam_options: { ... } } or { mam_id: '...', ...flat options... }
                if (root.ValueKind != JsonValueKind.Object) return null;

                var opts = new MyAnonamouseOptions();
                if (root.TryGetProperty("mam_options", out var mo) && mo.ValueKind == JsonValueKind.Object)
                {
                    if (mo.TryGetProperty("searchInDescription", out var sid) && (sid.ValueKind == JsonValueKind.True || sid.ValueKind == JsonValueKind.False))
                        opts.SearchInDescription = sid.GetBoolean();
                    if (mo.TryGetProperty("searchInSeries", out var sis) && (sis.ValueKind == JsonValueKind.True || sis.ValueKind == JsonValueKind.False))
                        opts.SearchInSeries = sis.GetBoolean();
                    if (mo.TryGetProperty("searchInFilenames", out var sif) && (sif.ValueKind == JsonValueKind.True || sif.ValueKind == JsonValueKind.False))
                        opts.SearchInFilenames = sif.GetBoolean();
                    if (mo.TryGetProperty("language", out var lang) && lang.ValueKind == JsonValueKind.String)
                        opts.SearchLanguage = lang.GetString();
                    if (mo.TryGetProperty("filter", out var filter) &&
                        filter.ValueKind == JsonValueKind.String &&
                        Enum.TryParse<MamTorrentFilter>(filter.GetString() ?? string.Empty, true, out var f))
                        opts.Filter = f;
                    if (mo.TryGetProperty("freeleechWedge", out var wedge) &&
                        wedge.ValueKind == JsonValueKind.String &&
                        Enum.TryParse<MamFreeleechWedge>(wedge.GetString() ?? string.Empty, true, out var w))
                        opts.FreeleechWedge = w;
                    if (mo.TryGetProperty("enrichResults", out var enrich) && (enrich.ValueKind == JsonValueKind.True || enrich.ValueKind == JsonValueKind.False))
                        opts.EnrichResults = enrich.GetBoolean();
                    if (mo.TryGetProperty("enrichTopResults", out var enrichTop) && (enrichTop.ValueKind == JsonValueKind.Number || enrichTop.ValueKind == JsonValueKind.String))
                    {
                        if (enrichTop.ValueKind == JsonValueKind.Number) opts.EnrichTopResults = enrichTop.GetInt32();
                        else if (int.TryParse(enrichTop.GetString(), out var etmp)) opts.EnrichTopResults = etmp;
                    }
                    return opts;
                }

                // Fallback: check for flat properties directly on root
                if (root.TryGetProperty("searchInDescription", out var sid2) && (sid2.ValueKind == JsonValueKind.True || sid2.ValueKind == JsonValueKind.False))
                    opts.SearchInDescription = sid2.GetBoolean();
                if (root.TryGetProperty("searchInSeries", out var sis2) && (sis2.ValueKind == JsonValueKind.True || sis2.ValueKind == JsonValueKind.False))
                    opts.SearchInSeries = sis2.GetBoolean();
                if (root.TryGetProperty("searchInFilenames", out var sif2) && (sif2.ValueKind == JsonValueKind.True || sif2.ValueKind == JsonValueKind.False))
                    opts.SearchInFilenames = sif2.GetBoolean();
                if (root.TryGetProperty("language", out var lang2) && lang2.ValueKind == JsonValueKind.String)
                    opts.SearchLanguage = lang2.GetString();
                if (root.TryGetProperty("filter", out var filter2) &&
                    filter2.ValueKind == JsonValueKind.String &&
                    Enum.TryParse<MamTorrentFilter>(filter2.GetString() ?? string.Empty, true, out var f2))
                    opts.Filter = f2;
                if (root.TryGetProperty("freeleechWedge", out var wedge2) &&
                    wedge2.ValueKind == JsonValueKind.String &&
                    Enum.TryParse<MamFreeleechWedge>(wedge2.GetString() ?? string.Empty, true, out var w2))
                    opts.FreeleechWedge = w2;
                if (root.TryGetProperty("enrichResults", out var enrich2) && (enrich2.ValueKind == JsonValueKind.True || enrich2.ValueKind == JsonValueKind.False))
                    opts.EnrichResults = enrich2.GetBoolean();
                if (root.TryGetProperty("enrichTopResults", out var enrichTop2) && (enrichTop2.ValueKind == JsonValueKind.Number || enrichTop2.ValueKind == JsonValueKind.String))
                {
                    if (enrichTop2.ValueKind == JsonValueKind.Number) opts.EnrichTopResults = enrichTop2.GetInt32();
                    else if (int.TryParse(enrichTop2.GetString(), out var etmp2)) opts.EnrichTopResults = etmp2;
                }

                // If no properties were found, return null
                if (opts.SearchInDescription == null && opts.SearchInSeries == null && opts.SearchInFilenames == null && opts.SearchLanguage == null && opts.Filter == null && opts.FreeleechWedge == null && opts.EnrichResults == null && opts.EnrichTopResults == null)
                    return null;

                return opts;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse AdditionalSettings JSON for MAM options");
                return null;
            }
        }

        public async Task<bool> TestApiConnectionAsync(string apiId)
        {
            try
            {
                var apiConfig = await _configurationService.GetApiConfigurationAsync(apiId);
                if (apiConfig == null) return false;

                // Test connection to the API
                var response = await _httpClient.GetAsync(apiConfig.BaseUrl);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, $"Error testing API connection for {apiId}");
                return false;
            }
        }

        private async Task<List<IndexerSearchResult>> SearchIndexerAsync(Indexer indexer, string query, string? category = null, SearchRequest? request = null)
        {
            try
            {
                // Sanitize the query for indexer searches to remove illegal characters
                query = SanitizeIndexerQuery(query);
                _logger.LogInformation("Searching indexer {Name} ({Implementation}) for: {Query}", indexer.Name, indexer.Implementation, query);

                // Route to appropriate search method based on implementation

                // Compute a single fallback name to use when indexer.Name is empty
                string fallbackName;
                if (!string.IsNullOrWhiteSpace(indexer.Name))
                {
                    fallbackName = indexer.Name;
                }
                else if (!string.IsNullOrWhiteSpace(indexer.Implementation))
                {
                    fallbackName = indexer.Implementation;
                }
                else
                {
                    try
                    {
                        var baseUrl = indexer.Url?.TrimEnd('/') ?? string.Empty;
                        var baseUri = new Uri(baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? baseUrl : "https://" + baseUrl);
                        fallbackName = baseUri.Host;
                    }
                    catch (Exception caughtEx_18) when (caughtEx_18 is not OperationCanceledException && caughtEx_18 is not OutOfMemoryException && caughtEx_18 is not StackOverflowException)
                    {
                        fallbackName = "Indexer";
                    }
                }

                // Try to find a matching provider for this indexer type
                var provider = _searchProviders.FirstOrDefault(p =>
                    p.IndexerType.Equals(indexer.Implementation, StringComparison.OrdinalIgnoreCase) ||
                    (p.IndexerType.Equals("Torznab", StringComparison.OrdinalIgnoreCase) && indexer.Implementation.Equals("Newznab", StringComparison.OrdinalIgnoreCase)));

                if (provider != null)
                {
                    var providerResults = await provider.SearchAsync(indexer, query, category, request);
                    // Ensure Source is set for all results
                    foreach (var r in providerResults.Where(r => string.IsNullOrWhiteSpace(r.Source)))
                    {
                        r.Source = fallbackName;
                    }
                    return providerResults;
                }
                else
                {
                    // Default fallback if no provider matches
                    _logger.LogWarning("No provider found for indexer type: {Implementation}", indexer.Implementation);
                    return new List<IndexerSearchResult>();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error searching indexer {Name}", indexer.Name);
                return new List<IndexerSearchResult>();
            }
        }

        /// <summary>
        /// Remove illegal/unsupported characters from indexer search queries.
        /// Strips a curated set of punctuation/symbols, smart quotes, control
        /// and formatting Unicode categories, then collapses whitespace.
        /// </summary>
        private string SanitizeIndexerQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return string.Empty;

            // Characters explicitly requested to strip
            // Added parentheses to remove '(' and ')' from queries
            const string forbidden = "*/\\<>:?|^~`$#%&+={}[]'\"!()";

            var sb = new System.Text.StringBuilder(query.Length);
            foreach (var ch in query)
            {
                // Remove control and format characters
                var uc = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
                if (char.IsControl(ch) || uc == System.Globalization.UnicodeCategory.Format)
                    continue;

                // Remove explicit forbidden ASCII symbols
                if (forbidden.IndexOf(ch) >= 0)
                    continue;

                // Remove common smart quotes and other punctuation variants
                // Left/right single quotation mark, left/right double quotation mark
                if (ch == '\u2018' || ch == '\u2019' || ch == '\u201C' || ch == '\u201D')
                    continue;

                sb.Append(ch);
            }

            // Collapse runs of whitespace to single space and trim
            var cleaned = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "\\s+", " ").Trim();
            return cleaned;
        }

        internal async Task<List<IndexerSearchResult>> ParseTorznabResponseAsync(string xmlContent, Indexer indexer)
        {
            var results = new List<IndexerSearchResult>();

            try
            {
                // Log first 500 chars of XML for debugging
                var preview = xmlContent.Length > 500 ? xmlContent.Substring(0, 500) + "..." : xmlContent;
                _logger.LogDebug("Parsing XML from {IndexerName}: {Preview}", indexer.Name, preview);

                // Parse XML with settings that are more lenient
                var settings = new System.Xml.XmlReaderSettings
                {
                    DtdProcessing = System.Xml.DtdProcessing.Ignore,
                    XmlResolver = null,
                    IgnoreWhitespace = true,
                    IgnoreComments = true
                };

                System.Xml.Linq.XDocument doc;
                using (var reader = System.Xml.XmlReader.Create(new System.IO.StringReader(xmlContent), settings))
                {
                    doc = System.Xml.Linq.XDocument.Load(reader);
                }

                var channel = doc.Root?.Element("channel");
                if (channel == null)
                {
                    _logger.LogWarning("Invalid Torznab response: no channel element");
                    return results;
                }

                var items = channel.Elements("item");
                var isUsenet = indexer.Type.Equals("Usenet", StringComparison.OrdinalIgnoreCase);

                foreach (var item in items)
                {
                    try
                    {
                        var result = new IndexerSearchResult
                        {
                            Id = item.Element("guid")?.Value ?? Guid.NewGuid().ToString(),
                            Title = item.Element("title")?.Value ?? "Unknown",
                            Source = indexer.Name,
                            Category = item.Element("category")?.Value ?? "Audiobook"
                        };
                        result.IndexerId = indexer.Id;
                        result.IndexerImplementation = indexer.Implementation;

                        // Parse published date
                        var pubDateStr = item.Element("pubDate")?.Value;
                        result.PublishedDate = DateTime.TryParse(pubDateStr, out var pubDate)
                            ? pubDate.ToString("o")
                            : string.Empty;

                        // Parse Torznab/Newznab attributes (support both torznab and newznab namespaces)
                        var torznabNs = System.Xml.Linq.XNamespace.Get("http://torznab.com/schemas/2015/feed");
                        var newznabNs = System.Xml.Linq.XNamespace.Get("http://www.newznab.com/DTD/2010/feeds/attributes/");
                        var attributes = item.Elements(torznabNs + "attr").Concat(item.Elements(newznabNs + "attr")).ToList();

                        foreach (var attr in attributes)
                        {
                            var name = attr.Attribute("name")?.Value;
                            var value = attr.Attribute("value")?.Value;

                            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value))
                                continue;

                            switch (name.ToLower())
                            {
                                case "size":
                                    var parsedSize = ParseSizeString(value);
                                    if (parsedSize > 0)
                                    {
                                        result.Size = parsedSize;
                                        _logger.LogDebug("Parsed size for {Title}: {Size} bytes from indexer {Indexer}", result.Title, parsedSize, indexer.Name);
                                    }
                                    else
                                    {
                                        _logger.LogWarning("Failed to parse size value '{Value}' for result '{Title}' from indexer {Indexer}", value, result.Title, indexer.Name);
                                    }
                                    break;
                                case "seeders":
                                    if (int.TryParse(value, out var seeders))
                                        result.Seeders = seeders;
                                    break;
                                case "peers":
                                    if (int.TryParse(value, out var peers))
                                        result.Leechers = peers;
                                    break;
                                case "magneturl":
                                    result.MagnetLink = value;
                                    break;
                                case "filetype":
                                case "format":
                                    // Prefer explicit filetype/format attributes
                                    var normalizedFmt = value.ToLowerInvariant();
                                    if (normalizedFmt.Contains("m4b")) result.Format = "M4B";
                                    else if (normalizedFmt.Contains("flac")) result.Format = "FLAC";
                                    else if (normalizedFmt.Contains("opus")) result.Format = "OPUS";
                                    else if (normalizedFmt.Contains("aac")) result.Format = "AAC";
                                    else if (normalizedFmt.Contains("mp3")) result.Format = "MP3";

                                    // Also set Quality from format where possible
                                    if (string.IsNullOrEmpty(result.Quality))
                                    {
                                        if (normalizedFmt.Contains("320")) result.Quality = "MP3 320kbps";
                                        else if (normalizedFmt.Contains("256")) result.Quality = "MP3 256kbps";
                                        else if (normalizedFmt.Contains("192")) result.Quality = "MP3 192kbps";
                                        else if (normalizedFmt.Contains("128")) result.Quality = "MP3 128kbps";
                                        else if (normalizedFmt.Contains("m4b")) result.Quality = "M4B";
                                    }
                                    break;
                                case "lang_code":
                                case "language_code":
                                case "lang":
                                    // Standardized language codes (e.g., ENG, FR)
                                    try
                                    {
                                        var parsedLang = SearchResultAttributeParser.ParseLanguageFromText(value);
                                        if (!string.IsNullOrEmpty(parsedLang)) result.Language = parsedLang;
                                    }
                                    catch (Exception caughtEx_22) when (caughtEx_22 is not OperationCanceledException && caughtEx_22 is not OutOfMemoryException && caughtEx_22 is not StackOverflowException)
                                    {
                                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                    }
                                    break;
                                case "language":
                                    // Some indexers use numeric language IDs (e.g., 1 -> ENG)
                                    if (int.TryParse(value, out var langNum))
                                    {
                                        if (langNum == 1) result.Language = "English";
                                        // Add other mappings if required in the future
                                    }
                                    else
                                    {
                                        try
                                        {
                                            var pl = SearchResultAttributeParser.ParseLanguageFromText(value);
                                            if (!string.IsNullOrEmpty(pl)) result.Language = pl;
                                        }
                                        catch (Exception caughtEx_23) when (caughtEx_23 is not OperationCanceledException && caughtEx_23 is not OutOfMemoryException && caughtEx_23 is not StackOverflowException)
                                        {
                                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                        }
                                    }
                                    break;
                                case "grabs":
                                    if (int.TryParse(value, out var grabs))
                                        result.Grabs = grabs;
                                    break;
                                case "files":
                                    if (int.TryParse(value, out var files))
                                        result.Files = files;
                                    break;
                                case "usenetdate":
                                    // Some indexers expose a usenet-specific date attribute; prefer it if parseable
                                    if (long.TryParse(value, out var unixSec))
                                    {
                                        try
                                        {
                                            var dt = DateTimeOffset.FromUnixTimeSeconds(unixSec).UtcDateTime;
                                            result.PublishedDate = dt.ToString("o");
                                        }
                                        catch (Exception caughtEx_24) when (caughtEx_24 is not OperationCanceledException && caughtEx_24 is not OutOfMemoryException && caughtEx_24 is not StackOverflowException)
                                        {
                                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                        }
                                    }
                                    else if (DateTime.TryParse(value, out var udt))
                                    {
                                        result.PublishedDate = udt.ToString("o");
                                    }
                                    break;
                            }
                        }

                        // Fallback: some indexers don't expose "grabs" as a standard torznab/newznab attr.
                        // Attempt a few common alternate attribute names and elements (snatches, comments, etc.)
                        if (result.Grabs == 0)
                        {
                            var altNames = new[] { "snatches", "snatched", "numgrabs", "num_grabs", "grab_count" };
                            foreach (var alt in altNames)
                            {
                                var altAttr = attributes.FirstOrDefault(a => string.Equals(a.Attribute("name")?.Value, alt, System.StringComparison.OrdinalIgnoreCase));
                                if (altAttr != null)
                                {
                                    var av = altAttr.Attribute("value")?.Value ?? altAttr.Value;
                                    if (!string.IsNullOrEmpty(av) && int.TryParse(av, out var g2))
                                    {
                                        result.Grabs = g2;
                                        _logger.LogDebug("Set grabs from alternate attr '{Alt}' for {Title}: {Grabs}", alt, result.Title, g2);
                                        break;
                                    }
                                }
                            }

                            // If still zero, and a comments element points to a details URL (althub-style), attempt to scrape comment count
                            if (result.Grabs == 0)
                            {
                                var commentsVal = item.Element("comments")?.Value;
                                if (!string.IsNullOrEmpty(commentsVal))
                                {
                                    // If comments is a URL, try scraping the page for a numeric comments count (only for known indexers to avoid many extra requests)
                                    if (Uri.TryCreate(commentsVal, UriKind.Absolute, out var commentsUri) && indexer.Url != null && indexer.Url.Contains("althub", StringComparison.OrdinalIgnoreCase))
                                    {
                                        try
                                        {
                                            var commentsPageUrl = new Uri(commentsUri.GetLeftPart(UriPartial.Path));
                                            _logger.LogDebug("Fetching comments page to extract grabs for {Title}: {Url}", result.Title, commentsPageUrl);
                                            using var resp = await _httpClient.GetAsync(commentsPageUrl);
                                            if (resp.IsSuccessStatusCode)
                                            {
                                                var html = await resp.Content.ReadAsStringAsync();
                                                // Look for common comment count patterns in page text
                                                var text = _htmlTextExtractor?.ExtractText(html) ?? html;
                                                var m = System.Text.RegularExpressions.Regex.Match(text, "(\\d{1,6})\\s+comments?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                                if (!m.Success)
                                                {
                                                    m = System.Text.RegularExpressions.Regex.Match(text, "Comments\\s*[:\\(]?\\s*(\\d{1,6})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                                }

                                                if (m.Success && int.TryParse(m.Groups[1].Value, out var scrapedComments))
                                                {
                                                    result.Grabs = scrapedComments;
                                                    _logger.LogDebug("Scraped comments count for {Title}: {Grabs}", result.Title, scrapedComments);
                                                }
                                            }
                                        }
                                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                        {
                                            _logger.LogDebug(ex, "Failed to scrape comments page for {Title}", result.Title);
                                        }
                                    }
                                    else
                                    {
                                        // Some feeds put a numeric comments value directly; parse that
                                        if (int.TryParse(commentsVal, out var commVal))
                                        {
                                            result.Grabs = commVal;
                                            _logger.LogDebug("Set grabs from <comments> element for {Title}: {Grabs}", result.Title, commVal);
                                        }
                                    }
                                }
                            }
                        }

                        // Get enclosure/link for download URL
                        var enclosure = item.Element("enclosure");
                        if (enclosure != null)
                        {
                            var enclosureUrl = enclosure.Attribute("url")?.Value;
                            if (!string.IsNullOrEmpty(enclosureUrl))
                            {
                                if (isUsenet)
                                {
                                    result.NzbUrl = enclosureUrl;
                                }
                                else
                                {
                                    result.TorrentUrl = enclosureUrl;
                                }
                            }

                            // If the indexer provides an enclosure length, use it as a size fallback
                            var lengthStr = enclosure.Attribute("length")?.Value;
                            if (!string.IsNullOrEmpty(lengthStr) && result.Size == 0)
                            {
                                var parsedLen = ParseSizeString(lengthStr);
                                if (parsedLen > 0)
                                {
                                    result.Size = parsedLen;
                                    _logger.LogDebug("Set size from enclosure length for {Title}: {Size} bytes", result.Title, parsedLen);
                                }
                            }
                        }

                        // If no magnet link found in attributes, check link element
                        var linkElem = item.Element("link")?.Value;
                        if (!string.IsNullOrEmpty(linkElem))
                        {
                            if (linkElem.StartsWith("magnet:") && string.IsNullOrEmpty(result.MagnetLink) && !isUsenet)
                            {
                                result.MagnetLink = linkElem;
                            }
                            else
                            {
                                // Use the link element as the canonical indexer page when possible
                                if (Uri.IsWellFormedUriString(linkElem, UriKind.Absolute))
                                {
                                    result.ResultUrl = linkElem;
                                }

                                // If torrentUrl is empty, prefer the link
                                if (string.IsNullOrEmpty(result.TorrentUrl) && !linkElem.StartsWith("magnet:") && !isUsenet)
                                {
                                    result.TorrentUrl = linkElem;
                                }
                                else if (string.IsNullOrEmpty(result.NzbUrl) && isUsenet && !linkElem.StartsWith("magnet:"))
                                {
                                    result.NzbUrl = linkElem;
                                }
                            }
                        }

                        // Parse description for additional metadata
                        var description = item.Element("description")?.Value;
                        if (!string.IsNullOrEmpty(description))
                        {
                            result.Description = description;

                            // Try to extract quality/format from description or title
                            var titleAndDesc = $"{result.Title} {description}".ToLower();

                            if (titleAndDesc.Contains("flac"))
                                result.Quality = "FLAC";
                            else if (titleAndDesc.Contains("320") || titleAndDesc.Contains("320kbps"))
                                result.Quality = "MP3 320kbps";
                            else if (titleAndDesc.Contains("256") || titleAndDesc.Contains("256kbps"))
                                result.Quality = "MP3 256kbps";
                            else if (titleAndDesc.Contains("192") || titleAndDesc.Contains("192kbps"))
                                result.Quality = "MP3 192kbps";
                            else if (titleAndDesc.Contains("128") || titleAndDesc.Contains("128kbps"))
                                result.Quality = "MP3 128kbps";
                            else if (titleAndDesc.Contains("64") || titleAndDesc.Contains("64kbps"))
                                result.Quality = "MP3 64kbps";
                            else if (titleAndDesc.Contains("m4b"))
                                result.Quality = "M4B";
                            else
                                result.Quality = "Unknown";

                            // Detect format
                            if (titleAndDesc.Contains("m4b"))
                                result.Format = "M4B";
                            else if (titleAndDesc.Contains("flac"))
                                result.Format = "FLAC";
                            else if (titleAndDesc.Contains("mp3"))
                                result.Format = "MP3";
                            else if (titleAndDesc.Contains("opus"))
                                result.Format = "OPUS";
                            else if (titleAndDesc.Contains("aac"))
                                result.Format = "AAC";

                            // Detect language codes present in title or description (e.g. [ENG / M4B])
                            try
                            {
                                var lang = SearchResultAttributeParser.ParseLanguageFromText(result.Title + " " + description);
                                if (!string.IsNullOrEmpty(lang)) result.Language = lang;
                            }
                            catch (Exception caughtEx_25) when (caughtEx_25 is not OperationCanceledException && caughtEx_25 is not OutOfMemoryException && caughtEx_25 is not StackOverflowException)
                            { /* Non-critical */
                                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                            }
                        }

                        // Extract author from title if possible (common format: "Author - Title")
                        var titleParts = result.Title.Split(new[] { " - ", " â€“ " }, StringSplitOptions.RemoveEmptyEntries);
                        if (titleParts.Length >= 2)
                        {
                            result.Artist = titleParts[0].Trim();
                            result.Album = string.Join(" - ", titleParts.Skip(1)).Trim();
                        }
                        else
                        {
                            result.Artist = "Unknown Author";
                            result.Album = result.Title;
                        }

                        // Only add results that have a valid download link
                        if (!string.IsNullOrEmpty(result.MagnetLink) ||
                            !string.IsNullOrEmpty(result.TorrentUrl) ||
                            !string.IsNullOrEmpty(result.NzbUrl))
                        {
                            // Set download type based on what's available
                            if (!string.IsNullOrEmpty(result.NzbUrl))
                            {
                                result.DownloadType = "Usenet";
                            }
                            else if (!string.IsNullOrEmpty(result.MagnetLink) || !string.IsNullOrEmpty(result.TorrentUrl))
                            {
                                result.DownloadType = "Torrent";
                            }

                            results.Add(result);
                        }
                        else
                        {
                            _logger.LogWarning("Skipping result '{Title}' - no download link found", result.Title);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogError(ex, "Error parsing indexer result item");
                    }
                }
            }
            catch (System.Xml.XmlException xmlEx)
            {
                _logger.LogError(xmlEx, "XML parsing error from {IndexerName} at Line {Line}, Position {Position}: {Message}",
                    indexer.Name, xmlEx.LineNumber, xmlEx.LinePosition, xmlEx.Message);

                // Log the problematic XML content around the error
                if (!string.IsNullOrEmpty(xmlContent))
                {
                    var lines = xmlContent.Split('\n');
                    if (xmlEx.LineNumber > 0 && xmlEx.LineNumber <= lines.Length)
                    {
                        var startLine = Math.Max(0, xmlEx.LineNumber - 3);
                        var endLine = Math.Min(lines.Length - 1, xmlEx.LineNumber + 2);
                        var context = string.Join("\n", lines[startLine..(endLine + 1)]);
                        _logger.LogError("XML context around error:\n{Context}", context);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error parsing Torznab XML response from {IndexerName}", indexer.Name);
            }

            return results;
        }

        private List<IndexerSearchResult> GenerateMockIndexerResults(string query)
        {
            // Generate multiple mock results to simulate real indexer responses
            // Default to torrent for backwards compatibility
            return GenerateMockIndexerResults(query, "Mock Indexer", "Torrent");
        }

        private List<IndexerSearchResult> GenerateMockIndexerResults(string query, string indexerName)
        {
            // Default to torrent for backwards compatibility
            return GenerateMockIndexerResults(query, indexerName, "Torrent");
        }

        private List<IndexerSearchResult> GenerateMockIndexerResults(string query, string indexerName, string indexerType)
        {
            // Generate multiple mock results to simulate real indexer responses
            var random = new Random();
            var results = new List<IndexerSearchResult>();
            var isUsenet = indexerType.Equals("Usenet", StringComparison.OrdinalIgnoreCase);

            _logger.LogInformation("Generating {Count} mock {Type} results for indexer {IndexerName}", 5, indexerType, indexerName);

            for (int i = 0; i < 5; i++)
            {
                var result = new IndexerSearchResult
                {
                    Id = Guid.NewGuid().ToString(),
                    Title = $"{query} - Quality {i + 1}",
                    Artist = "Various Authors",
                    Album = $"{query} Series",
                    Category = "Audiobook",
                    Size = random.Next(200_000_000, 1_500_000_000), // 200 MB to 1.5 GB
                    Seeders = isUsenet ? 0 : random.Next(5, 100), // Usenet doesn't have seeders
                    Leechers = isUsenet ? 0 : random.Next(0, 20), // Usenet doesn't have leechers
                    Source = indexerName,
                    PublishedDate = DateTime.UtcNow.AddDays(-random.Next(1, 365)).ToString("o"),
                    Quality = i switch
                    {
                        0 => "MP3 64kbps",
                        1 => "MP3 128kbps",
                        2 => "MP3 192kbps",
                        3 => "M4B 128kbps",
                        _ => "FLAC"
                    },
                    Format = i >= 3 ? "M4B" : "MP3",
                    Language = "English"
                };

                // Set appropriate download link based on indexer type
                if (isUsenet)
                {
                    result.NzbUrl = $"https://{indexerName.ToLower()}.example.com/api/nzb/{Guid.NewGuid():N}";
                    result.MagnetLink = string.Empty;
                    result.TorrentUrl = string.Empty;
                }
                else
                {
                    result.MagnetLink = $"magnet:?xt=urn:btih:{Guid.NewGuid():N}";
                    result.NzbUrl = string.Empty;
                }

                results.Add(result);
            }

            return results;
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


        private long ParseSizeString(string sizeStr)
        {
            if (string.IsNullOrEmpty(sizeStr))
                return 0;

            // Remove any commas and extra spaces
            sizeStr = sizeStr.Replace(",", "").Trim();

            // Try to parse as direct bytes first
            if (long.TryParse(sizeStr, out var bytes))
                return bytes;

            // Handle formats like "500 MB", "1.2 GB", "1024 KB", "3.7 GiB", "279.0 MiB", etc.
            // Support both decimal (KB/MB/GB/TB) and binary (KiB/MiB/GiB/TiB) units
            var match = System.Text.RegularExpressions.Regex.Match(sizeStr, @"^([\d\.]+)\s*(KiB|MiB|GiB|TiB|KB|MB|GB|TB|B)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success &&
                double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                var unit = match.Groups[2].Value.ToUpper();
                return unit switch
                {
                    "B" => (long)value,
                    "KB" => (long)(value * 1000),
                    "MB" => (long)(value * 1000 * 1000),
                    "GB" => (long)(value * 1000 * 1000 * 1000),
                    "TB" => (long)(value * 1000 * 1000 * 1000 * 1000),
                    "KIB" => (long)(value * 1024),
                    "MIB" => (long)(value * 1024 * 1024),
                    "GIB" => (long)(value * 1024 * 1024 * 1024),
                    "TIB" => (long)(value * 1024 * 1024 * 1024 * 1024),
                    _ => (long)value
                };
            }

            _logger.LogWarning("Unable to parse size string: '{SizeStr}'", sizeStr);
            return 0;
        }

        // (Helper methods for containment and fuzzy scoring are implemented above.)

        public async Task<List<ApiConfiguration>> GetEnabledMetadataSourcesAsync()
        {
            try
            {
                _logger.LogDebug("Querying database for enabled metadata sources...");

                var allConfigs = await _apiConfigRepository.GetAllAsync();
                var metadataSources = allConfigs
                    .Where(api => api.IsEnabled && api.Type == "metadata")
                    .OrderBy(api => api.Priority)
                    .ToList();

                if (metadataSources.Count > 0)
                {
                    _logger.LogInformation("Retrieved {Count} enabled metadata sources: {Sources}",
                        metadataSources.Count,
                        string.Join(", ", metadataSources.Select(s => $"{s.Name} (Priority: {s.Priority}, BaseUrl: {s.BaseUrl})")));
                }
                else
                {
                    _logger.LogWarning("No enabled metadata sources found in database");
                }

                return metadataSources;
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Invalid operation error retrieving enabled metadata sources");
                return new List<ApiConfiguration>();
            }
        }
    }
}
