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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Application.Search.Core
{
    public partial class SearchService : ISearchService
    {
        private readonly IConfigurationService _configurationService;
        private readonly ILogger<SearchService> _logger;
        private readonly SearchProgressReporter _searchProgressReporter;
        private readonly AsinCandidateCollector _asinCandidateCollector;
        private readonly AsinEnricher _asinEnricher;
        private readonly SearchResultScorerService _searchResultScorer;
        private readonly SearchResultSortingService _searchResultSorting;
        private readonly AsinSearchHandler _asinSearchHandler;
        private readonly IndexerSearchWorkflow _indexerSearchWorkflow;
        private readonly MetadataSourceCatalog _metadataSourceCatalog;
        private readonly AudibleSimpleLookupWorkflow _audibleSimpleLookupWorkflow;
        private readonly AudibleAuthorSearchWorkflow _audibleAuthorSearchWorkflow;
        private readonly SearchFinalDispositionLogger _finalDispositionLogger;

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
            MetadataSourceCatalog? metadataSourceCatalog = null,
            AudibleAuthorPageCollector? audibleAuthorPageCollector = null,
            AudibleSimpleLookupWorkflow? audibleSimpleLookupWorkflow = null,
            AudibleAuthorSearchWorkflow? audibleAuthorSearchWorkflow = null,
            SearchFinalDispositionLogger? finalDispositionLogger = null)
        {
            _configurationService = configurationService;
            _logger = logger;
            _searchProgressReporter = searchProgressReporter;
            _asinCandidateCollector = asinCandidateCollector;
            _asinEnricher = asinEnricher;
            var resolvedSearchProviders = searchProviders ?? Enumerable.Empty<IIndexerSearchProvider>();
            _searchResultScorer = searchResultScorer;
            _searchResultSorting = searchResultSorting;
            _asinSearchHandler = asinSearchHandler;
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
            var resolvedAudibleAuthorPageCollector = audibleAuthorPageCollector ?? new AudibleAuthorPageCollector(
                audibleService,
                NullLogger<AudibleAuthorPageCollector>.Instance);
            _audibleSimpleLookupWorkflow = audibleSimpleLookupWorkflow ?? new AudibleSimpleLookupWorkflow(
                audibleService,
                metadataConverters);
            _audibleAuthorSearchWorkflow = audibleAuthorSearchWorkflow ?? new AudibleAuthorSearchWorkflow(
                audibleService,
                resolvedAudibleAuthorPageCollector,
                metadataConverters,
                NullLogger<AudibleAuthorSearchWorkflow>.Instance);
            _finalDispositionLogger = finalDispositionLogger ?? new SearchFinalDispositionLogger(
                NullLogger<SearchFinalDispositionLogger>.Instance);
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

    }
}
