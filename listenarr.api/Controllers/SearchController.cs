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
using Microsoft.AspNetCore.Mvc;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Metadata;
using Listenarr.Application.Search;
using Listenarr.Domain.Models;
using Listenarr.Application.Security;

namespace Listenarr.Api.Controllers
{
    [ApiController]
    [Route("api/v{version:apiVersion}/search")]
    [Tags("Search")]
    public class SearchController : ControllerBase
    {
        private readonly ISearchService _searchService;
        private readonly Microsoft.Extensions.Logging.ILogger _logger;
        private readonly AudibleService _audibleService;
        private readonly IAudiobookMetadataService _metadataService;
        private readonly IImageCacheService? _imageCacheService;
        private readonly SearchResponseMapper _responseMapper;
        private readonly StructuredSearchWorkflow _structuredSearchWorkflow;

        public SearchController(
            ISearchService searchService,
            Microsoft.Extensions.Logging.ILogger<SearchController> logger,
            AudibleService audibleService,
            IAudiobookMetadataService metadataService,
            IImageCacheService? imageCacheService = null,
            MetadataConverters? metadataConverters = null,
            SearchResponseMapper? responseMapper = null,
            StructuredSearchWorkflow? structuredSearchWorkflow = null)
        {
            _searchService = searchService;
            _logger = logger;
            _audibleService = audibleService;
            _metadataService = metadataService;
            _imageCacheService = imageCacheService;
            var metadataConvertersInstance = metadataConverters ?? new MetadataConverters(imageCacheService, Microsoft.Extensions.Logging.Abstractions.NullLogger<MetadataConverters>.Instance);
            _responseMapper = responseMapper ?? new SearchResponseMapper(
                metadataService,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SearchResponseMapper>.Instance,
                imageCacheService);
            _structuredSearchWorkflow = structuredSearchWorkflow ?? new StructuredSearchWorkflow(
                searchService,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<StructuredSearchWorkflow>.Instance,
                audibleService,
                metadataService,
                imageCacheService,
                metadataConvertersInstance,
                _responseMapper);
        }

        private string BuildApiImagePath(string identifier, string? sourceUrl = null)
            => _responseMapper.BuildApiImagePath(identifier, HttpContext, sourceUrl: sourceUrl);

        /// <summary>
        /// Perform a combined metadata and indexer search using a structured request body.
        /// Supports simple (metadata-only) and advanced (indexer) search modes.
        /// </summary>
        /// <param name="reqJson">Search request JSON with query, mode, region, and optional filters.</param>
        /// <param name="simplified">When true (default), return simplified metadata for the "Add New" workflow.</param>
        [HttpPost]
        public async Task<ActionResult<object>> Search([FromBody] JsonElement reqJson, [FromQuery] bool? simplified = null)
        {
            var result = await _structuredSearchWorkflow.ExecuteAsync(reqJson, simplified, HttpContext);
            return result.Succeeded ? Ok(result.Payload) : BadRequest(result.Payload);
        }

        /// <summary>
        /// Search configured indexers for audiobook torrents/NZBs using query parameters.
        /// </summary>
        /// <param name="query">Search term.</param>
        /// <param name="category">Optional category filter.</param>
        /// <param name="apiIds">Optional list of specific API IDs to query.</param>
        /// <param name="enrichedOnly">When true, return only metadata results that have enriched data.</param>
        /// <param name="sortBy">Sort field (default: Seeders).</param>
        /// <param name="sortDirection">Sort direction (default: Descending).</param>
        /// <returns>Separated indexer and metadata results.</returns>
        [HttpGet]
        public async Task<ActionResult<List<SearchResult>>> Search(
            [FromQuery] string? query,
            [FromQuery] string? category = null,
            [FromQuery] List<string>? apiIds = null,
            [FromQuery] bool enrichedOnly = false,
            [FromQuery] SearchSortBy sortBy = SearchSortBy.Seeders,
            [FromQuery] SearchSortDirection sortDirection = SearchSortDirection.Descending)
        {
            try
            {
                if (string.IsNullOrEmpty(query))
                {
                    // If model-binding didn't populate the parameter (direct controller calls in tests),
                    // try to read the raw query string value. If still missing, fall back to empty string
                    // so unit/integration tests that call the action directly don't get a BadRequest.
                    try
                    {
                        var qFromReq = HttpContext?.Request?.Query["query"].ToString();
                        query = !string.IsNullOrWhiteSpace(qFromReq) ? qFromReq : string.Empty;
                    }
                    catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) { query = string.Empty; }
                }

                var searchResults = await _searchService.SearchAsync(query, category, apiIds, sortBy, sortDirection);

                // Convert List<SearchResult> to SearchResponse by separating indexer and metadata results
                var response = new SearchResponse();
                foreach (var result in searchResults)
                {
                    // Determine result type: indexer results have size/seeders, metadata results have description/publisher
                    if (result.Size > 0 || (result.Seeders ?? 0) > 0 || !string.IsNullOrEmpty(result.MagnetLink) || !string.IsNullOrEmpty(result.TorrentUrl) || !string.IsNullOrEmpty(result.NzbUrl))
                    {
                        var idx = SearchResultConverters.ToIndexerSearchResult(result);
                        response.IndexerResults.Add(SearchResultConverters.ToIndexerResultDto(idx));
                    }
                    else
                    {
                        response.MetadataResults.Add(SearchResultConverters.ToMetadata(result));
                    }
                }

                // Normalize/canonicalize images for returned search results so the
                // frontend receives local /api/v{version}/images/{asin} URLs when possible.
                var mdResults = response.MetadataResults;
                var cacheService = _imageCacheService;

                if (cacheService != null && mdResults != null)
                {
                    foreach (var r in mdResults)
                    {
                        try
                        {
                            if (r == null) continue;
                            if (string.IsNullOrWhiteSpace(r.Asin)) continue;

                            var asin = r.Asin!;

                            var cached = await cacheService.GetCachedImagePathAsync(asin);
                            if (!string.IsNullOrWhiteSpace(cached))
                            {
                                r.ImageUrl = BuildApiImagePath(asin);
                                continue;
                            }

                            var imageUrl = r.ImageUrl;
                            if (!string.IsNullOrWhiteSpace(imageUrl))
                            {
                                var url = imageUrl!;
                                if (url.StartsWith("http://") || url.StartsWith("https://"))
                                {
                                    var downloaded = await cacheService.DownloadAndCacheImageAsync(url, asin);
                                    if (!string.IsNullOrWhiteSpace(downloaded))
                                    {
                                        r.ImageUrl = BuildApiImagePath(asin);
                                    }
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogWarning(ex, "Failed to ensure cached image for search result ASIN {Asin}", r.Asin);
                        }
                    }
                }

                if (enrichedOnly && mdResults != null)
                {
                    response.MetadataResults = mdResults.Where(r => (r?.IsEnriched ?? false)).ToList();
                }
                return Ok(response);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error performing search for query: {Query}", query);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Perform an intelligent metadata search that automatically scores and ranks results using fuzzy matching.
        /// </summary>
        /// <param name="query">Search term (title, author, or combination).</param>
        /// <param name="category">Optional category filter.</param>
        /// <param name="candidateLimit">Maximum candidates to consider before ranking (default 50).</param>
        /// <param name="returnLimit">Maximum results to return (default 50).</param>
        /// <param name="containmentMode">Matching strictness: Relaxed or Strict (default Relaxed).</param>
        /// <param name="requireAuthorAndPublisher">When true, only return results with both author and publisher.</param>
        /// <param name="fuzzyThreshold">Minimum fuzzy-match score (0.0–1.0, default 0.7).</param>
        [HttpGet("intelligent")]
        [ProducesResponseType(typeof(List<MetadataSearchResult>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<List<MetadataSearchResult>>> IntelligentSearch(
                [FromQuery] string query,
                [FromQuery] string? category = null,
                [FromQuery] int candidateLimit = 50,
                [FromQuery] int returnLimit = 50,
                [FromQuery] string containmentMode = "Relaxed",
                [FromQuery] bool requireAuthorAndPublisher = false,
                [FromQuery] double fuzzyThreshold = 0.7)
        {
            try
            {
                // Debug: log raw incoming query to help integration-test diagnostics
                try { _logger.LogDebug("[DEBUG] IntelligentSearch called with query='{Query}'", LogRedaction.SanitizeText(query ?? "<null>")); }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    System.Diagnostics.Debug.WriteLine($"SearchController IntelligentSearch debug logging failed: {ex.Message}");
                }

                // Also emit a warning-level log so test output captures the value
                try { _logger.LogWarning("[DBG] IntelligentSearch called with query='{Query}'", LogRedaction.SanitizeText(query ?? "<null>")); }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    System.Diagnostics.Debug.WriteLine($"SearchController IntelligentSearch warning logging failed: {ex.Message}");
                }

                if (string.IsNullOrEmpty(query))
                {
                    return BadRequest("Query parameter is required");
                }

                _logger.LogInformation("IntelligentSearch called for query: {Query}", LogRedaction.SanitizeText(query));
                var region = Request.Query.TryGetValue("region", out var regionValue) ? regionValue.ToString() ?? "us" : "us";
                var language = Request.Query.TryGetValue("language", out var languageValue) ? languageValue.ToString() : null;
                var results = await _searchService.IntelligentSearchAsync(query, candidateLimit, returnLimit, containmentMode, requireAuthorAndPublisher, fuzzyThreshold, region, language, HttpContext.RequestAborted);
                await _responseMapper.NormalizeMetadataResultImagesAsync(results, HttpContext, "metadata result");
                _logger.LogInformation("IntelligentSearch returning {Count} results for query: {Query}", results?.Count ?? 0, LogRedaction.SanitizeText(query));
                return Ok(results ?? new List<MetadataSearchResult>());
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error performing intelligent search for query: {Query}", LogRedaction.SanitizeText(query));
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Search for audiobook series by name using the Audible catalog provider.
        /// </summary>
        /// <param name="name">Series name to search for.</param>
        /// <param name="region">Audible marketplace region (default: us).</param>
        [HttpGet("audible/series")]
        public async Task<ActionResult<object>> SearchAudibleSeries([FromQuery] string name, [FromQuery] string region = "us")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name)) return BadRequest("name query parameter is required");
                var res = await _audibleService.SearchSeriesByNameAsync(name, region);
                if (res == null) return NotFound();
                return Ok(res);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error proxying Audible series search for name {Name}", name);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Get all books in a series by the series ASIN.
        /// </summary>
        /// <param name="asin">Audible series ASIN.</param>
        /// <param name="region">Audible marketplace region (default: us).</param>
        [HttpGet("audible/series/books/{asin}")]
        public async Task<ActionResult<object>> GetAudibleSeriesBooks(string asin, [FromQuery] string region = "us")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(asin)) return BadRequest("asin is required");
                var res = await _audibleService.GetBooksBySeriesAsinAsync(asin, region);
                if (res == null) return NotFound();
                return Ok(res);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error proxying Audible series books for ASIN {Asin}", asin);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Search configured indexers only (no metadata enrichment). Supports MyAnonamouse-specific query parameters.
        /// </summary>
        /// <param name="query">Search term.</param>
        /// <param name="category">Optional category filter.</param>
        /// <param name="sortBy">Sort field (default: Seeders).</param>
        /// <param name="sortDirection">Sort direction (default: Descending).</param>
        /// <param name="isAutomaticSearch">Set to true when this search is triggered automatically rather than by user action.</param>
        [HttpGet("indexers")]
        [ProducesResponseType(typeof(List<SearchResult>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<List<SearchResult>>> IndexersSearch(
                [FromQuery] string query,
                [FromQuery] string? category = null,
                [FromQuery] SearchSortBy sortBy = SearchSortBy.Seeders,
                [FromQuery] SearchSortDirection sortDirection = SearchSortDirection.Descending,
                [FromQuery] bool isAutomaticSearch = false)
        {
            try
            {
                if (string.IsNullOrEmpty(query))
                {
                    return BadRequest("Query parameter is required");
                }

                _logger.LogInformation("IndexersSearch called for query: {Query}, isAutomaticSearch={IsAutomatic}", LogRedaction.SanitizeText(query), isAutomaticSearch);

                // Support MyAnonamouse query string toggles (mamFilter, mamSearchInDescription, mamSearchInSeries, mamSearchInFilenames, mamLanguage, mamFreeleechWedge)
                var req = new SearchRequest { MyAnonamouse = SearchMamOptionsReader.FromQuery(Request.Query) };
                var results = await _searchService.SearchIndexersAsync(query, category, sortBy, sortDirection, isAutomaticSearch, req);
                _logger.LogInformation("IndexersSearch returning {Count} results for query: {Query}", results.Count, LogRedaction.SanitizeText(query));
                return Ok(results);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error searching indexers for query: {Query}", LogRedaction.SanitizeText(query));
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Test connectivity to a configured API source.
        /// </summary>
        /// <param name="apiId">API configuration ID to test.</param>
        /// <returns>True if the connection succeeds, false otherwise.</returns>
        [HttpPost("test/{apiId}")]
        public async Task<ActionResult<bool>> TestApiConnection(string apiId)
        {
            try
            {
                var isConnected = await _searchService.TestApiConnectionAsync(apiId);
                return Ok(isConnected);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error testing API connection for {ApiId}", apiId);
                return StatusCode(500, "Internal server error");
            }
        }

        // [HttpGet("indexers")]
        // public async Task<ActionResult<List<SearchResult>>> SearchIndexers(
        //     [FromQuery] string query,
        //     [FromQuery] string? category = null)
        // {
        //     try
        //     {
        //         if (string.IsNullOrEmpty(query))
        //         {
        //             return BadRequest("Query parameter is required");
        //         }

        //         var results = await _searchService.SearchIndexersAsync(query, category);
        // Optional tuning parameters exposed to callers
        //var candidateLimit = int.TryParse(Request.Query["candidateLimit"], out var cl) ? Math.Clamp(cl, 5, 200) : 50;
        //var returnLimit = int.TryParse(Request.Query["returnLimit"], out var rl) ? Math.Clamp(rl, 1, 100) : 10;
        //var containmentMode = Request.Query.ContainsKey("containmentMode") ? Request.Query["containmentMode"].ToString() ?? "Relaxed" : "Relaxed";
        //var requireAuthorAndPublisher = bool.TryParse(Request.Query["requireAuthorAndPublisher"], out var rap) ? rap : false;
        //var fuzzyThreshold = double.TryParse(Request.Query["fuzzyThreshold"], out var ft) ? Math.Clamp(ft, 0.0, 1.0) : 0.7;
        //         return Ok(results);
        //     }
        //     catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) //     {
        //         _logger.LogError(ex, "Error searching indexers for query: {Query}", query);
        //         return StatusCode(500, "Internal server error");
        //     }
        // }

        /// <summary>
        /// Search the Audible catalog for audiobooks.
        /// </summary>
        [HttpGet("audible")]
        public async Task<ActionResult<AudibleSearchResponse>> SearchAudible(
            [FromQuery] string query,
            [FromQuery] string region = "us",
            [FromQuery] string? language = null)
        {
            try
            {
                if (string.IsNullOrEmpty(query))
                {
                    return BadRequest("Query parameter is required");
                }

                var result = await _audibleService.SearchBooksAsync(query, region: region, language: language);
                if (result == null)
                {
                    return NotFound("No results found");
                }

                return Ok(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error searching the Audible catalog for query: {Query}", query);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Search for audiobooks by title, automatically fetching full metadata from configured sources.
        /// Note: currently consumed by the Discord bot; changes here can cascade to that integration.
        /// </summary>
        [HttpGet("title")]
        [ProducesResponseType(typeof(List<object>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<List<object>>> SearchByTitle(
            [FromQuery] string query,
            [FromQuery] string region = "us",
            [FromQuery] int limit = 10)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(query))
                {
                    return BadRequest("Query parameter is required");
                }

                _logger.LogInformation("Searching by title: {Query}", query);

                // If the query looks like an ASIN, short-circuit to metadata lookup so we don't run
                // a full Amazon/Audible text search that can return unrelated items.
                bool IsAsin(string s)
                {
                    if (string.IsNullOrEmpty(s)) return false;
                    if (s.Length != 10) return false;
                    if (!(s.StartsWith("B0") || char.IsDigit(s[0]))) return false;
                    return s.All(char.IsLetterOrDigit);
                }

                if (IsAsin(query.Trim()))
                {
                    var asin = query.Trim();
                    _logger.LogInformation("Query appears to be an ASIN; attempting direct metadata lookup for: {Asin}", asin);

                    // Try the Audible-backed provider first, then fall back to other configured metadata sources.
                    try
                    {
                        var audible = await _audibleService.GetBookMetadataAsync(asin, region, true);
                        if (audible != null)
                        {
                            var metadataObj = new
                            {
                                metadata = audible,
                                source = "Audible",
                                sourceUrl = "https://www.audible.com"
                            };
                            return Ok(new List<object> { metadataObj });
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Audible metadata lookup failed for ASIN {Asin}, trying other configured metadata sources", asin);
                    }

                    // If audible didn't return anything, try configured metadata sources directly
                    try
                    {
                        var meta = await _metadataService.GetMetadataAsync(asin, region, true);
                        if (meta != null)
                        {
                            return Ok(new List<object> { meta });
                        }
                        _logger.LogWarning("Metadata lookup returned null for ASIN {Asin}, falling back to intelligent search", asin);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Metadata lookup failed for ASIN {Asin}, falling back to intelligent search", asin);
                    }

                    // If no metadata found via configured sources, fall back to the generic intelligent search below
                }

                // Use intelligent search (Amazon/Audible + metadata enrichment) for Discord bot
                // This excludes indexer results which are not suitable for bot interactions
                // The Discord bot now sends proper prefixes (TITLE:, AUTHOR:, AUTHOR_TITLE:)
                var searchResults = await _searchService.IntelligentSearchAsync(query, region: region, language: null, ct: HttpContext.RequestAborted);

                if (searchResults == null || !searchResults.Any())
                {
                    _logger.LogWarning("No results found for title search: {Query}", query);
                    return Ok(new List<object>());
                }

                // Convert SearchResult objects to the expected format for Discord bot
                var results = new List<object>();
                var resultsToReturn = searchResults.Take(limit).ToList();

                foreach (var searchResult in resultsToReturn)
                {
                    try
                    {
                        // Create a metadata-like object from the SearchResult
                        var metadata = new
                        {
                            Asin = searchResult.Asin,
                            Title = searchResult.Title,
                            Subtitle = searchResult.Series != null ? $"{searchResult.Series} #{searchResult.SeriesNumber}" : null,
                            Authors = !string.IsNullOrEmpty(searchResult.Author) ? new[] { new { Name = searchResult.Author } } : null,
                            Narrators = !string.IsNullOrEmpty(searchResult.Narrator) ? searchResult.Narrator.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries).Select(n => new { Name = n.Trim() }) : null,
                            Publisher = searchResult.Publisher,
                            Description = searchResult.Description,
                            ImageUrl = searchResult.ImageUrl,
                            LengthMinutes = searchResult.Runtime,
                            Language = searchResult.Language,
                            ReleaseDate = !string.IsNullOrWhiteSpace(searchResult.PublishedDate) ? searchResult.PublishedDate : null,
                            Series = !string.IsNullOrEmpty(searchResult.Series) ? new[] { new { Name = searchResult.Series, Position = searchResult.SeriesNumber } } : null
                        };

                        results.Add(new
                        {
                            metadata = metadata,
                            source = searchResult.MetadataSource ?? searchResult.Source ?? "Amazon/Audible",
                            sourceUrl = "https://www.amazon.com"
                        });
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Failed to convert search result for title: {Title}", searchResult.Title);
                        continue;
                    }
                }

                _logger.LogInformation("Successfully fetched {Count} enriched results for title search: {Query}", results.Count, query);
                return Ok(results);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error performing title search for query: {Query}", query);
                return StatusCode(500, "Internal server error");
            }
        }

        // existing code continuation
        /// <summary>
        /// Search a specific API by ID
        /// Note: This route uses a parameter and must come after all specific routes to avoid conflicts
        /// </summary>
        [HttpGet("{apiId}")]
        public async Task<ActionResult<object>> SearchByApi(
            string apiId,
            [FromQuery] string query,
            [FromQuery] string? category = null,
            [FromQuery] string? mamFilter = null,
            [FromQuery] bool? mamSearchInDescription = null,
            [FromQuery] bool? mamSearchInSeries = null,
            [FromQuery] bool? mamSearchInFilenames = null,
            [FromQuery] string? mamLanguage = null,
            [FromQuery] string? mamFreeleechWedge = null,
            [FromQuery] bool? mamEnrichResults = null,
            [FromQuery] int? mamEnrichTopResults = null)
        {
            try
            {
                _logger.LogInformation("SearchByApi called with apiId: {ApiId}, query: {Query}", apiId, query);

                if (string.IsNullOrEmpty(query))
                {
                    return BadRequest("Query parameter is required");
                }

                // If the caller provided explicit MyAnonamouse query params, construct a SearchRequest that will be passed to the service.
                var request = SearchMamOptionsReader.FromBoundParameters(
                    mamFilter,
                    mamSearchInDescription,
                    mamSearchInSeries,
                    mamSearchInFilenames,
                    mamLanguage,
                    mamFreeleechWedge,
                    mamEnrichResults,
                    mamEnrichTopResults);

                // Use the raw indexer results when the caller expects indexer-specific fields. SearchIndexerResultsAsync will
                // apply any MyAnonamouse options found in the indexer's AdditionalSettings if no explicit request was supplied.
                var idxResults = await _searchService.SearchIndexerResultsAsync(apiId, query, category, request);

                // If the underlying indexer implementation indicates MyAnonamouse (set on results by SearchIndexerAsync), return Prowlarr-like DTO shape
                if (idxResults.Count > 0 && !string.IsNullOrWhiteSpace(idxResults[0].IndexerImplementation) && string.Equals(idxResults[0].IndexerImplementation, "MyAnonamouse", StringComparison.OrdinalIgnoreCase))
                {
                    var dtos = idxResults.Select(r => SearchResultConverters.ToIndexerResultDto(r)).ToList();
                    return Ok(dtos);
                }

                // Otherwise, return the legacy SearchResult shape
                var results = idxResults.Select(r => SearchResultConverters.ToSearchResult(r)).ToList();
                _logger.LogInformation("SearchByApi returning {Count} results for apiId: {ApiId}", results.Count, apiId);
                return Ok(results);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error searching API {ApiId} for query: {Query}", apiId, query);
                return StatusCode(500, "Internal server error");
            }
        }
    }
}
