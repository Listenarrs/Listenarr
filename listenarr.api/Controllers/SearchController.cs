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

using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Listenarr.Application.Common;
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
        private readonly MetadataConverters _metadataConverters;

        public SearchController(
            ISearchService searchService,
            Microsoft.Extensions.Logging.ILogger<SearchController> logger,
            AudibleService audibleService,
            IAudiobookMetadataService metadataService,
            IImageCacheService? imageCacheService = null,
            MetadataConverters? metadataConverters = null)
        {
            _searchService = searchService;
            _logger = logger;
            _audibleService = audibleService;
            _metadataService = metadataService;
            _imageCacheService = imageCacheService;
            _metadataConverters = metadataConverters ?? new MetadataConverters(imageCacheService, Microsoft.Extensions.Logging.Abstractions.NullLogger<MetadataConverters>.Instance);
        }

        private string BuildApiImagePath(string identifier, string? sourceUrl = null)
            => ApiVersionPathBuilder.BuildImagePath(identifier, HttpContext, sourceUrl: sourceUrl);

        private static string? NormalizeStructuredAdvancedField(string? value, string prefix)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            var trimmed = value.Trim();
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            var stripped = trimmed.Substring(prefix.Length).Trim();
            return string.IsNullOrWhiteSpace(stripped) ? null : stripped;
        }

        private async Task NormalizeSearchResultImagesAsync(List<SearchResult> results)
        {
            if (_imageCacheService == null || results == null) return;

            foreach (var r in results)
            {
                try
                {
                    if (r == null) continue;
                    if (string.IsNullOrWhiteSpace(r.Asin)) continue;

                    // If we already have a cached path, map to API endpoint
                    var cached = await _imageCacheService.GetCachedImagePathAsync(r.Asin);
                    if (!string.IsNullOrWhiteSpace(cached))
                    {
                        r.ImageUrl = BuildApiImagePath(r.Asin);
                        continue;
                    }

                    // If the result includes an external HTTP(S) image URL, try
                    // to download and cache it using the ASIN as identifier.
                    if (!string.IsNullOrWhiteSpace(r.ImageUrl) && (r.ImageUrl.StartsWith("http://") || r.ImageUrl.StartsWith("https://")))
                    {
                        var downloaded = await _imageCacheService.DownloadAndCacheImageAsync(r.ImageUrl, r.Asin);
                        r.ImageUrl = !string.IsNullOrWhiteSpace(downloaded)
                            ? BuildApiImagePath(r.Asin)
                            : BuildApiImagePath(r.Asin, r.ImageUrl);
                    }
                    // If no external URL was present, map to API endpoint if ASIN present
                    else if (!string.IsNullOrWhiteSpace(r.Asin))
                    {
                        r.ImageUrl = BuildApiImagePath(r.Asin);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to normalize image for search result ASIN {Asin}", r.Asin);
                }
            }
        }


        private List<object> SimplifySearchResults(List<SearchResult> results)
        {
            return results?.Select(r => new
            {
                r.Id,
                r.Title,
                Artist = r.Artist,
                r.Subtitle,
                r.Description,
                r.Publisher,
                r.Language,
                r.Runtime,
                r.Narrator,
                r.ImageUrl,
                r.Asin,
                Isbn = r.Isbn ?? new List<string>(),
                r.Series,
                r.SeriesNumber,
                r.ProductUrl,
                r.PublishedDate,
                r.PublishYear,
                r.Genres,
                r.IsEnriched,
                r.MetadataSource,
                r.Source,
                r.SourceLink,
                r.Score
            }).Cast<object>().ToList() ?? new List<object>();
        }

        /// <summary>
        /// Perform a combined metadata and indexer search using a structured request body.
        /// Supports simple (metadata-only) and advanced (indexer) search modes.
        /// </summary>
        /// <param name="reqJson">Search request JSON with query, mode, region, and optional filters.</param>
        /// <param name="simplified">When true (default), return simplified metadata for the "Add New" workflow.</param>
        [HttpPost]
        public async Task<ActionResult<object>> Search([FromBody] JsonElement reqJson, [FromQuery] bool? simplified = null)
        {
            try
            {
                if (reqJson.ValueKind == JsonValueKind.Undefined || reqJson.ValueKind == JsonValueKind.Null)
                {
                    return BadRequest("SearchRequest body is required");
                }

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

                var req = JsonSerializer.Deserialize<SearchRequest>(reqJson.GetRawText(), options);
                if (req == null) return BadRequest("SearchRequest body is required");
                _logger.LogDebug("[DBG] Search received mode={Mode}, query='{Query}'", req.Mode, LogRedaction.SanitizeText(req.Query ?? "<null>"));

                // Default to simplified=true for both modes (user only needs metadata for Add New feature)
                var useSimplified = simplified ?? true;

                if (req.Mode == SearchMode.Simple)
                {
                    var q = req.Query ?? string.Empty;
                    var region = string.IsNullOrWhiteSpace(req.Region) ? "us" : req.Region;
                    var language = string.IsNullOrWhiteSpace(req.Language) ? null : req.Language;
                    var results = await _searchService.IntelligentSearchAsync(q, region: region, language: language, ct: HttpContext.RequestAborted) ?? new List<MetadataSearchResult>();

                    // Normalize images for metadata results so the SPA receives local /api/v{version}/images/{asin} when possible
                    if (_imageCacheService != null && results != null)
                    {
                        foreach (var r in results)
                        {
                            try
                            {
                                if (r == null) continue;
                                if (string.IsNullOrWhiteSpace(r.Asin)) continue;

                                var cached = await _imageCacheService.GetCachedImagePathAsync(r.Asin);
                                if (!string.IsNullOrWhiteSpace(cached))
                                {
                                    r.ImageUrl = BuildApiImagePath(r.Asin);
                                    continue;
                                }

                                if (!string.IsNullOrWhiteSpace(r.ImageUrl) && (r.ImageUrl.StartsWith("http://") || r.ImageUrl.StartsWith("https://")))
                                {
                                    var downloaded = await _imageCacheService.DownloadAndCacheImageAsync(r.ImageUrl, r.Asin);
                                    r.ImageUrl = !string.IsNullOrWhiteSpace(downloaded)
                                        ? BuildApiImagePath(r.Asin)
                                        : BuildApiImagePath(r.Asin, r.ImageUrl);
                                }
                                else if (!string.IsNullOrWhiteSpace(r.Asin))
                                {
                                    r.ImageUrl = BuildApiImagePath(r.Asin);
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                _logger.LogWarning(ex, "Failed to normalize image for metadata result ASIN {Asin}", r.Asin);
                            }
                        }
                    }

                    // Map metadata results into Audible-shaped objects for public API consumers
                    var mapped = await Task.WhenAll((results ?? new List<MetadataSearchResult>()).Select(r => MapMetadataResultToAudibleAsync(r, region))).ConfigureAwait(false);
                    _logger.LogDebug("[DBG] Search(simple) returning {Count} metadata results", mapped?.Length ?? 0);
                    return Ok(mapped);
                }
                else // Advanced
                {
                    // Route all advanced search logic through SearchService for normalization, filtering, and orchestration
                    req.Author = NormalizeStructuredAdvancedField(req.Author, "AUTHOR:");
                    req.Title = NormalizeStructuredAdvancedField(req.Title, "TITLE:");
                    req.Isbn = NormalizeStructuredAdvancedField(req.Isbn, "ISBN:");
                    req.Asin = NormalizeStructuredAdvancedField(req.Asin, "ASIN:");

                    // Validate and normalize ISBN/ASIN inputs for advanced searches.
                    // If an ISBN-10 is supplied, convert it to ISBN-13 using the 978 prefix.
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(req.Isbn))
                        {
                            var rawIsbn = Regex.Replace(req.Isbn, "[^0-9Xx]", string.Empty);
                            if (rawIsbn.Length == 10)
                            {
                                var converted = ConvertIsbn10ToIsbn13(rawIsbn);
                                if (converted == null)
                                {
                                    return BadRequest("Invalid ISBN-10 provided");
                                }
                                req.Isbn = converted; // replace with ISBN-13
                                _logger.LogInformation("Converted ISBN-10 to ISBN-13: {Original} -> {Converted}", rawIsbn, converted);
                            }
                            else if (rawIsbn.Length == 13)
                            {
                                if (!Regex.IsMatch(rawIsbn, "^[0-9]{13}$"))
                                {
                                    return BadRequest("ISBN must be 13 digits");
                                }
                                req.Isbn = rawIsbn;
                            }
                            else
                            {
                                return BadRequest("ISBN must be either 10 or 13 characters");
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Failed to normalize ISBN in advanced search");
                        return BadRequest("Invalid ISBN format");
                    }

                    // Compose a query string from advanced parameters for unified handling
                    var region = string.IsNullOrWhiteSpace(req.Region) ? "us" : req.Region;
                    var language = string.IsNullOrWhiteSpace(req.Language) ? null : req.Language;

                    // If no advanced search parameters were provided, signal BadRequest to caller
                    if (string.IsNullOrWhiteSpace(req.Title)
                        && string.IsNullOrWhiteSpace(req.Author)
                        && string.IsNullOrWhiteSpace(req.Query)
                        && string.IsNullOrWhiteSpace(req.Isbn)
                        && string.IsNullOrWhiteSpace(req.Asin)
                        && string.IsNullOrWhiteSpace(req.Series))
                    {
                        return BadRequest("At least one advanced search parameter (title, author, isbn, asin, series, or query) is required");
                    }
                    // Debug: log incoming advanced parameters for diagnostics
                    try { _logger.LogInformation("[DBG] Advanced search request: Author='{Author}', Title='{Title}', Isbn='{Isbn}', Asin='{Asin}', Query='{Query}', Region='{Region}', Language='{Language}'", LogRedaction.SanitizeText(req.Author), LogRedaction.SanitizeText(req.Title), LogRedaction.SanitizeText(req.Isbn), LogRedaction.SanitizeText(req.Asin), LogRedaction.SanitizeText(req.Query), LogRedaction.SanitizeText(region), LogRedaction.SanitizeText(language)); }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        System.Diagnostics.Debug.WriteLine($"SearchController advanced-search info logging failed: {ex.Message}");
                    }
                    try { _logger.LogDebug("[DBG] Advanced params: Title='{Title}', Author='{Author}', Isbn='{Isbn}'", LogRedaction.SanitizeText(req.Title), LogRedaction.SanitizeText(req.Author), LogRedaction.SanitizeText(req.Isbn)); }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        System.Diagnostics.Debug.WriteLine($"SearchController advanced-search debug logging failed: {ex.Message}");
                    }

                    // If the advanced request contains an ASIN, prefer a direct Audible metadata
                    // lookup and return a single enriched SearchResult. ASIN searches should
                    // be authoritative and ignore other advanced inputs.
                    if (!string.IsNullOrWhiteSpace(req.Asin))
                    {
                        try
                        {
                            var audible = await _audibleService.GetBookMetadataAsync(req.Asin, region, true);
                            if (audible != null)
                            {
                                // Convert audible response to internal metadata then to SearchResult
                                var metadata = _metadataConverters.ConvertAudibleToMetadata(audible, req.Asin, source: "Audible");
                                var sr = await _metadataConverters.ConvertMetadataToSearchResultAsync(metadata, req.Asin, req.Title, req.Author, fallbackImageUrl: null, fallbackLanguage: language);
                                SanitizeResultForPublicApi(sr, region);
                                // Convert to metadata result and normalize images for API response
                                var md = SearchResultConverters.ToMetadata(sr);
                                if (_imageCacheService != null && !string.IsNullOrWhiteSpace(md.Asin))
                                {
                                    try
                                    {
                                        var cached = await _imageCacheService.GetCachedImagePathAsync(md.Asin);
                                        if (!string.IsNullOrWhiteSpace(cached))
                                        {
                                            md.ImageUrl = BuildApiImagePath(md.Asin);
                                        }
                                        else if (!string.IsNullOrWhiteSpace(md.ImageUrl) && (md.ImageUrl.StartsWith("http://") || md.ImageUrl.StartsWith("https://")))
                                        {
                                            var downloaded = await _imageCacheService.DownloadAndCacheImageAsync(md.ImageUrl, md.Asin);
                                            md.ImageUrl = !string.IsNullOrWhiteSpace(downloaded)
                                                ? BuildApiImagePath(md.Asin)
                                                : BuildApiImagePath(md.Asin, md.ImageUrl);
                                        }
                                    }
                                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                    {
                                        _logger.LogWarning(ex, "Failed to normalize image for ASIN metadata {Asin}", md?.Asin);
                                    }
                                }
                                if (md != null)
                                {
                                    var result = SearchResultConverters.ToSearchResult(md);
                                    var asinResults = new List<SearchResult> { result };
                                    return Ok(useSimplified ? SimplifySearchResults(asinResults) : asinResults);
                                }
                            }
                            // If audible didn't return a record, fall through to unified search below
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogWarning(ex, "Audible metadata lookup failed for ASIN {Asin} in advanced search; falling back to unified search", req.Asin);
                        }
                    }



                    // If a series name or series ASIN was provided, prefer Audible series endpoints.
                    // If series is provided and no author is supplied, take the series-specialized path.
                    // If an author is present, prefer the author flow and later filter by series.
                    if (!string.IsNullOrWhiteSpace(req.Series) && string.IsNullOrWhiteSpace(req.Author))
                    {
                        try
                        {
                            string? seriesAsin = null;
                            var seriesInput = req.Series.Trim();

                            // Check if the provided value already looks like an ASIN
                            if (seriesInput.StartsWith("B0", StringComparison.OrdinalIgnoreCase) && seriesInput.Length >= 10)
                            {
                                seriesAsin = seriesInput;
                            }
                            else
                            {
                                // Search by name to resolve the series ASIN
                                var seriesSearch = await _audibleService.SearchSeriesByNameAsync(seriesInput, region);
                                _logger.LogInformation("SearchSeriesByNameAsync returned type={Type}, isNull={IsNull}",
                                    seriesSearch?.GetType().Name ?? "null", seriesSearch == null);
                                if (seriesSearch is IEnumerable<SeriesLookupItem> seriesList)
                                {
                                    var seriesListMaterialized = seriesList.ToList();
                                    _logger.LogInformation("Series lookup for '{SeriesName}' returned {Count} items", LogRedaction.SanitizeText(seriesInput), seriesListMaterialized.Count);
                                    var chosenItem = seriesListMaterialized.FirstOrDefault(s =>
                                                        !string.IsNullOrWhiteSpace(s.Asin) &&
                                                        string.Equals(s.Region, region, StringComparison.OrdinalIgnoreCase))
                                                    ?? seriesListMaterialized.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Asin));
                                    if (chosenItem != null)
                                    {
                                        seriesAsin = chosenItem.Asin;
                                        _logger.LogInformation("Resolved series '{SeriesName}' to ASIN {SeriesAsin}", LogRedaction.SanitizeText(req.Series), LogRedaction.SanitizeText(seriesAsin));
                                    }
                                }

                                if (string.IsNullOrWhiteSpace(seriesAsin))
                                {
                                    _logger.LogInformation("No series ASIN found for '{SeriesName}'; falling back to unified search", LogRedaction.SanitizeText(req.Series));
                                }
                            }

                            // Fetch all books for the resolved series ASIN
                            if (!string.IsNullOrWhiteSpace(seriesAsin))
                            {
                                var booksObj = await _audibleService.GetBooksBySeriesAsinAsync(seriesAsin, region);

                                // Direct cast — GetBooksBySeriesAsinAsync returns List<AudibleSearchResult>
                                var books = booksObj as List<AudibleSearchResult>;

                                if (books != null && books.Any())
                                {
                                    // Apply language filter when a preferred language was specified
                                    if (!string.IsNullOrWhiteSpace(language) && !string.Equals(language, "all", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var langFilter = language.Trim();
                                        books = books.Where(b =>
                                            string.IsNullOrWhiteSpace(b.Language) ||
                                            string.Equals(b.Language.Trim(), langFilter, StringComparison.OrdinalIgnoreCase))
                                            .ToList();
                                    }

                                    _logger.LogInformation("Series ASIN {SeriesAsin} returned {Count} books (after language filter)", seriesAsin, books.Count);

                                    // Return books in the same Audible-shaped format as the unified search path
                                    var seriesResults = new List<object>();
                                    foreach (var book in books)
                                    {
                                        try
                                        {
                                            seriesResults.Add(await MapAudibleSearchResultToOutputAsync(book, region));
                                        }
                                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                        {
                                            _logger.LogWarning(ex, "Failed converting series book to output for ASIN {Asin}", book.Asin);
                                        }
                                    }

                                    if (seriesResults.Any())
                                    {
                                        return Ok(seriesResults);
                                    }
                                }
                                else
                                {
                                    _logger.LogInformation("Series ASIN {SeriesAsin} returned no books", seriesAsin);
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogWarning(ex, "Failed to perform series lookup for '{Series}' in advanced search; falling back to unified search", LogRedaction.SanitizeText(req.Series));
                        }
                    }

                    // Previously there was a special-case path here that handled author-only
                    // advanced searches separately. To ensure all advanced searches (author-only,
                    // author+title, title-only, ISBN, etc.) receive identical metadata
                    // enrichment and conversion, route advanced requests through the
                    // unified IntelligentSearch pipeline below. This guarantees Audible
                    // metadata is fetched and converted consistently.

                    // Compose a query string from advanced parameters for unified handling
                    var queryParts = new List<string>();
                    // Prefix author/title/isbn/asin tokens so IntelligentSearch parser
                    // recognizes them and selects the correct search branch (e.g. AUTHOR_TITLE).
                    if (!string.IsNullOrWhiteSpace(req.Author)) queryParts.Add($"AUTHOR:{req.Author}");
                    if (!string.IsNullOrWhiteSpace(req.Title)) queryParts.Add($"TITLE:{req.Title}");
                    if (!string.IsNullOrWhiteSpace(req.Isbn)) queryParts.Add($"ISBN:{req.Isbn}");
                    if (!string.IsNullOrWhiteSpace(req.Asin)) queryParts.Add($"ASIN:{req.Asin}");
                    // When only a series name was provided and the series-specific lookup above
                    // didn't resolve, use it as a plain keyword query so the general
                    // SearchBooksAsync branch handles it (more resilient than TITLE-specific).
                    // The destructive series filter below ensures only matching results return.
                    if (queryParts.Count == 0 && !string.IsNullOrWhiteSpace(req.Series))
                        queryParts.Add(req.Series);
                    var query = queryParts.Count > 0 ? string.Join(" ", queryParts) : (req.Query ?? string.Empty);
                    try { _logger.LogInformation("Advanced search request composed parts={Parts} -> query='{Query}'", string.Join("|", queryParts), LogRedaction.SanitizeText(query)); }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        System.Diagnostics.Debug.WriteLine($"SearchController composed-query logging failed: {ex.Message}");
                    }
                    // Respect optional pagination/candidate caps from the client
                    var candidateLimit = req.Cap.HasValue ? Math.Clamp(req.Cap.Value, 5, 2000) : 200;
                    var returnLimit = req.Pagination != null && req.Pagination.Limit > 0 ? Math.Clamp(req.Pagination.Limit, 1, 1000) : 50;
                    var results = await _searchService.IntelligentSearchAsync(query, candidateLimit, returnLimit, region: region, language: language, ct: HttpContext.RequestAborted);

                    // Ensure images for results are served via our API when possible.
                    // For results that provide an ASIN, prefer the local /api/v{version}/images/{asin}
                    // endpoint by checking cached images or attempting to download and cache
                    // external image URLs. This prevents leaking external Amazon/Audible
                    // image URLs to the SPA and avoids mixed image sources.
                    if (_imageCacheService != null && results != null)
                    {
                        foreach (var r in results)
                        {
                            try
                            {
                                if (r == null) continue;
                                if (string.IsNullOrWhiteSpace(r.Asin)) continue;

                                var cached = await _imageCacheService.GetCachedImagePathAsync(r.Asin);
                                if (!string.IsNullOrWhiteSpace(cached))
                                {
                                    r.ImageUrl = BuildApiImagePath(r.Asin);
                                    continue;
                                }

                                if (!string.IsNullOrWhiteSpace(r.ImageUrl) && (r.ImageUrl.StartsWith("http://") || r.ImageUrl.StartsWith("https://")))
                                {
                                    var downloaded = await _imageCacheService.DownloadAndCacheImageAsync(r.ImageUrl, r.Asin);
                                    if (!string.IsNullOrWhiteSpace(downloaded))
                                    {
                                        r.ImageUrl = BuildApiImagePath(r.Asin);
                                    }
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                _logger.LogWarning(ex, "Failed to normalize image for result with ASIN {Asin}", r.Asin);
                            }
                        }
                    }

                    // When a Series filter was provided, apply it to unified search results so only
                    // books actually belonging to the series are returned. This covers both the
                    // author+series path and the series-only fallback (when the series ASIN lookup
                    // above didn't resolve and the series name was injected as TITLE:).
                    if (!string.IsNullOrWhiteSpace(req.Series) && results != null)
                    {
                        try
                        {
                            var seriesFilter = req.Series.Trim();
                            var ci = System.Globalization.CultureInfo.InvariantCulture.CompareInfo;
                            const System.Globalization.CompareOptions diOpts = System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace;
                            var filtered = System.Text.RegularExpressions.Regex.IsMatch(seriesFilter, @"^B0[A-Z0-9]{8,}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                                ? results.Where(r => (!string.IsNullOrWhiteSpace(r.Series) && ci.IndexOf(r.Series, seriesFilter, diOpts) >= 0)
                                    || (!string.IsNullOrWhiteSpace(r.Asin) && string.Equals(r.Asin, seriesFilter, StringComparison.OrdinalIgnoreCase))).ToList()
                                : results.Where(r => !string.IsNullOrWhiteSpace(r.Series) && ci.IndexOf(r.Series, seriesFilter, diOpts) >= 0).ToList();

                            results = filtered;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogDebug(ex, "Failed to apply series filter '{Series}' to advanced search results", LogRedaction.SanitizeText(req.Series));
                        }
                    }

                    // Flatten metadata results into Audible-shaped objects for public POST /api/search response
                    var flatMapped = await Task.WhenAll((results ?? new List<MetadataSearchResult>()).Select(r => MapMetadataResultToAudibleAsync(r, region))).ConfigureAwait(false);
                    return Ok(flatMapped);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error parsing search request body");
                return BadRequest("Invalid search request");
            }
        }

        private void SanitizeResultForPublicApi(SearchResult r, string region)
        {
            // Minimal sanitization for public API: ensure ProductUrl is an http(s) URL when ASIN is available
            try
            {
                if (r == null) return;
                if (string.IsNullOrWhiteSpace(r.ProductUrl) && !string.IsNullOrWhiteSpace(r.Asin))
                {
                    r.ProductUrl = $"https://www.amazon.com/dp/{r.Asin}";
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to sanitize public search result for ASIN {Asin}", r.Asin);
            }
        }

        // Map an AudibleSearchResult (from series/direct endpoints) to the Audible-shaped output object
        private async Task<object> MapAudibleSearchResultToOutputAsync(AudibleSearchResult book, string region)
        {
            string? imageUrl = book.ImageUrl;
            if (!string.IsNullOrWhiteSpace(book.Asin) && _imageCacheService != null)
            {
                try
                {
                    var cached = await _imageCacheService.GetCachedImagePathAsync(book.Asin);
                    if (!string.IsNullOrWhiteSpace(cached))
                    {
                        imageUrl = BuildApiImagePath(book.Asin);
                    }
                    else if (!string.IsNullOrWhiteSpace(imageUrl) && (imageUrl.StartsWith("http://") || imageUrl.StartsWith("https://")))
                    {
                        var downloaded = await _imageCacheService.DownloadAndCacheImageAsync(imageUrl, book.Asin);
                        if (!string.IsNullOrWhiteSpace(downloaded)) imageUrl = BuildApiImagePath(book.Asin);
                    }
                    else
                    {
                        imageUrl = BuildApiImagePath(book.Asin);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogDebug(ex, "Failed to normalize image for series result ASIN {Asin}", book.Asin);
                }
            }

            var authors = (book.Authors ?? new List<AudibleAuthor>()).Where(a => a != null).Select(a => new
            {
                asin = a!.Asin,
                name = a!.Name,
                region = a!.Region ?? region,
                regions = new[] { a!.Region ?? region },
                updatedAt = DateTime.UtcNow.ToString("o")
            }).ToList();
            var narrators = (book.Narrators ?? new List<AudibleNarrator>()).Where(n => n != null).Select(n => new { name = n!.Name, updatedAt = DateTime.UtcNow.ToString("o") }).ToList();
            var genres = (book.Genres ?? new List<AudibleGenre>()).Where(g => g != null).Select(g => new
            {
                asin = g!.Asin,
                name = g!.Name,
                type = g!.Type,
                updatedAt = DateTime.UtcNow.ToString("o")
            }).ToList();
            var series = (book.Series ?? new List<AudibleSeries>()).Where(s => s != null).Select(s => new
            {
                asin = s!.Asin,
                name = s!.Name,
                region = region,
                position = s!.Position,
                updatedAt = DateTime.UtcNow.ToString("o")
            }).ToList();

            return new
            {
                asin = book.Asin,
                title = book.Title,
                subtitle = book.Subtitle,
                region = region,
                regions = new[] { region },
                description = (string?)null,
                summary = (string?)null,
                bookFormat = book.BookFormat,
                imageUrl = imageUrl,
                lengthMinutes = book.RuntimeLengthMin ?? book.LengthMinutes ?? book.RuntimeMinutes,
                whisperSync = false,
                publisher = book.Publisher,
                isbn = book.Isbn,
                language = book.Language,
                releaseDate = book.ReleaseDate,
                @explicit = false,
                hasPdf = false,
                link = !string.IsNullOrWhiteSpace(book.Asin) ? $"https://www.audible.com/pd/{book.Asin}" : (string?)null,
                sku = book.Sku,
                isListenable = !string.IsNullOrWhiteSpace(book.Asin),
                isAvailable = true,
                isBuyable = true,
                contentType = book.ContentType ?? "Product",
                contentDeliveryType = book.ContentDeliveryType,
                authors,
                narrators,
                genres,
                series,
                seriesList = series.Select(s => $"{s.name}{(s.position != null ? $" #{s.position}" : "")}").ToList(),
                updatedAt = DateTime.UtcNow.ToString("o")
            };
        }

        // Map our internal MetadataSearchResult to a lightweight Audible-shaped object (async)
        private async Task<object> MapMetadataResultToAudibleAsync(MetadataSearchResult md, string region)
        {
            // If we have an ASIN and the metadata was enriched, try to fetch the canonical Audible payload
            AudibleBookResponse? aud = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(md?.Asin))
                {
                    aud = await _metadataService.GetAudibleMetadataAsync(md.Asin, region, true);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to retrieve Audible metadata for ASIN {Asin}", md?.Asin);
            }

            // If Audible provided a rich response, prefer it (but normalize image URLs to local /api/v{version}/images/{asin} when possible)
            if (aud != null)
            {
                string? imageUrl = aud.ImageUrl;
                try
                {
                    if (!string.IsNullOrWhiteSpace(aud.Asin) && _imageCacheService != null)
                    {
                        var cached = await _imageCacheService.GetCachedImagePathAsync(aud.Asin);
                        if (!string.IsNullOrWhiteSpace(cached))
                        {
                            imageUrl = BuildApiImagePath(aud.Asin);
                        }
                        else if (!string.IsNullOrWhiteSpace(imageUrl) && (imageUrl.StartsWith("http://") || imageUrl.StartsWith("https://")))
                        {
                            var downloaded = await _imageCacheService.DownloadAndCacheImageAsync(imageUrl, aud.Asin);
                            if (!string.IsNullOrWhiteSpace(downloaded)) imageUrl = BuildApiImagePath(aud.Asin);
                        }
                        else
                        {
                            // Map to API endpoint even if not cached to keep behaviour consistent
                            imageUrl = BuildApiImagePath(aud.Asin);
                            _ = _imageCacheService.DownloadAndCacheImageAsync(aud.ImageUrl ?? imageUrl, aud.Asin);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to normalize Audible image for {Asin}", aud.Asin);
                }

                var authors = (aud.Authors ?? new List<AudibleAuthor>()).Where(a => a != null).Select(a => new
                {
                    asin = a!.Asin,
                    name = a!.Name,
                    region = a!.Region ?? region,
                    regions = new[] { a!.Region ?? region },
                    image = (string?)null,
                    updatedAt = DateTime.UtcNow.ToString("o")
                }).ToList();

                var narrators = (aud.Narrators ?? new List<AudibleNarrator>()).Where(n => n != null).Select(n => new { name = n!.Name, updatedAt = DateTime.UtcNow.ToString("o") }).ToList();

                var genres = (aud.Genres ?? new List<AudibleGenre>()).Where(g => g != null).Select(g => new
                {
                    asin = g!.Asin,
                    name = g!.Name,
                    type = g!.Type,
                    betterType = (string?)null,
                    updatedAt = DateTime.UtcNow.ToString("o")
                }).ToList();

                var series = (aud.Series ?? new List<AudibleSeries>()).Where(s => s != null).Select(s => new
                {
                    asin = s!.Asin,
                    name = s!.Name,
                    region = region,
                    position = s!.Position,
                    updatedAt = DateTime.UtcNow.ToString("o")
                }).ToList();

                return new
                {
                    asin = aud.Asin ?? md?.Asin,
                    title = aud.Title ?? md?.Title,
                    subtitle = aud.Subtitle ?? md?.Subtitle,
                    region = aud.Region ?? region,
                    regions = new[] { aud.Region ?? region },
                    description = aud.Description ?? md?.Description,
                    summary = aud.Description ?? md?.Description,
                    copyright = (string?)null,
                    bookFormat = aud.BookFormat,
                    imageUrl = imageUrl,
                    lengthMinutes = aud.LengthMinutes ?? md?.Runtime,
                    whisperSync = false,
                    publisher = aud.Publisher ?? md?.Publisher,
                    isbn = aud.Isbn,
                    language = aud.Language ?? md?.Language,
                    rating = (double?)null,
                    releaseDate = aud.ReleaseDate ?? aud.PublishDate ?? md?.PublishedDate,
                    @explicit = aud.Explicit ?? false,
                    hasPdf = false,
                    link = !string.IsNullOrWhiteSpace(md?.ProductUrl)
                        ? md.ProductUrl
                        : !string.IsNullOrWhiteSpace(aud.Asin) ? $"https://www.audible.com/pd/{aud.Asin}" : null,
                    sku = aud.Sku,
                    skuGroup = (string?)null,
                    isListenable = !string.IsNullOrWhiteSpace(aud.Asin ?? md?.Asin),
                    isAvailable = true,
                    isBuyable = true,
                    contentType = aud.ContentType ?? (string?)null,
                    contentDeliveryType = aud.ContentDeliveryType,
                    authors = authors,
                    narrators = narrators,
                    genres = genres,
                    series = series,
                    seriesList = series?.Select(s => $"{s.name}{(s.position != null ? $" #{s.position}" : "")}").ToList(),
                    updatedAt = DateTime.UtcNow.ToString("o")
                };
            }

            // Fallback: build a permissive Audible-like object from available MetadataSearchResult fields
            var fallbackAuthors = new List<object>();
            var fallbackNarrators = new List<object>();
            if (!string.IsNullOrWhiteSpace(md?.Narrator)) fallbackNarrators.Add(new { name = md.Narrator, updatedAt = (string?)null });
            if (!string.IsNullOrWhiteSpace(md?.Author)) fallbackAuthors.Add(new { asin = (string?)null, name = md.Author, region = region, regions = new[] { region }, image = (string?)null, updatedAt = (string?)null });

            var fallbackSeries = new List<object>();
            if (!string.IsNullOrWhiteSpace(md?.Series)) fallbackSeries.Add(new { asin = md.Series, name = md.Series, region = region, position = md.SeriesNumber, updatedAt = (string?)null });

            return new
            {
                asin = md?.Asin,
                title = md?.Title,
                subtitle = md?.Subtitle,
                region = region,
                regions = new[] { region },
                description = md?.Description,
                summary = md?.Description,
                copyright = (string?)null,
                bookFormat = (string?)null,
                imageUrl = md?.ImageUrl,
                lengthMinutes = md?.Runtime,
                whisperSync = false,
                publisher = md?.Publisher,
                isbn = md?.Isbn,
                language = md?.Language,
                rating = (double?)null,
                releaseDate = md?.PublishedDate,
                @explicit = false,
                hasPdf = false,
                link = md?.ProductUrl,
                sku = (string?)null,
                skuGroup = (string?)null,
                isListenable = !string.IsNullOrWhiteSpace(md?.Asin),
                isAvailable = true,
                isBuyable = true,
                contentType = "Product",
                contentDeliveryType = (string?)null,
                authors = fallbackAuthors,
                narrators = fallbackNarrators,
                genres = new List<object>(),
                series = fallbackSeries,
                updatedAt = (string?)null
            };
        }

        private static string? ConvertIsbn10ToIsbn13(string isbn10)
        {
            if (string.IsNullOrWhiteSpace(isbn10)) return null;
            // isbn10 is expected to be 10 chars where first 9 are digits and last is digit or 'X'
            if (isbn10.Length != 10) return null;
            var first9 = isbn10.Substring(0, 9);
            if (!Regex.IsMatch(first9, "^[0-9]{9}$")) return null;
            var twelve = "978" + first9; // 12 digits
            int sum = 0;
            for (int i = 0; i < 12; i++)
            {
                int d = twelve[i] - '0';
                sum += (i % 2 == 0) ? d * 1 : d * 3;
            }
            int mod = sum % 10;
            int check = (10 - mod) % 10;
            return string.Concat(twelve, check);
        }

        private async Task EnsureCachedImagesForAudibleResultsAsync(List<AudibleSearchResult>? results)
        {
            if (results == null || results.Count == 0) return;
            if (_imageCacheService == null) return; // nothing to do in tests if not provided

            foreach (var r in results)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(r.Asin)) continue;

                    var cached = await _imageCacheService.GetCachedImagePathAsync(r.Asin);
                    if (!string.IsNullOrWhiteSpace(cached))
                    {
                        r.ImageUrl = BuildApiImagePath(r.Asin);
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(r.ImageUrl))
                    {
                        var downloaded = await _imageCacheService.DownloadAndCacheImageAsync(r.ImageUrl, r.Asin);
                        if (!string.IsNullOrWhiteSpace(downloaded))
                        {
                            r.ImageUrl = BuildApiImagePath(r.Asin);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to ensure cached image for {Asin}", r?.Asin);
                }
            }
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
                // Normalize images for metadata results so the SPA receives local /api/v{version}/images/{asin} when possible
                if (_imageCacheService != null && results != null)
                {
                    foreach (var r in results)
                    {
                        try
                        {
                            if (r == null) continue;
                            if (string.IsNullOrWhiteSpace(r.Asin)) continue;

                            var cached = await _imageCacheService.GetCachedImagePathAsync(r.Asin);
                            if (!string.IsNullOrWhiteSpace(cached))
                            {
                                r.ImageUrl = BuildApiImagePath(r.Asin);
                                continue;
                            }

                            if (!string.IsNullOrWhiteSpace(r.ImageUrl) && (r.ImageUrl.StartsWith("http://") || r.ImageUrl.StartsWith("https://")))
                            {
                                var downloaded = await _imageCacheService.DownloadAndCacheImageAsync(r.ImageUrl, r.Asin);
                                if (!string.IsNullOrWhiteSpace(downloaded)) r.ImageUrl = BuildApiImagePath(r.Asin);
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogWarning(ex, "Failed to normalize image for metadata result ASIN {Asin}", r.Asin);
                        }
                    }
                }
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
                var mamOptions = new MyAnonamouseOptions();
                if (Request.Query.TryGetValue("mamFilter", out var queryMamFilter) && Enum.TryParse<MamTorrentFilter>(queryMamFilter.ToString() ?? string.Empty, true, out var mamFilter))
                    mamOptions.Filter = mamFilter;
                if (Request.Query.TryGetValue("mamSearchInDescription", out var queryMamSearchInDescription) && bool.TryParse(queryMamSearchInDescription, out var sd)) mamOptions.SearchInDescription = sd;
                if (Request.Query.TryGetValue("mamSearchInSeries", out var queryMamSearchInSeries) && bool.TryParse(queryMamSearchInSeries, out var ss)) mamOptions.SearchInSeries = ss;
                if (Request.Query.TryGetValue("mamSearchInFilenames", out var queryMamSearchInFilenames) && bool.TryParse(queryMamSearchInFilenames, out var sf)) mamOptions.SearchInFilenames = sf;
                if (Request.Query.TryGetValue("mamLanguage", out var queryMamLanguage)) mamOptions.SearchLanguage = queryMamLanguage.ToString();
                if (Request.Query.TryGetValue("mamFreeleechWedge", out var queryMamFreeleechWedge) && Enum.TryParse<MamFreeleechWedge>(queryMamFreeleechWedge.ToString() ?? string.Empty, true, out var mw)) mamOptions.FreeleechWedge = mw;

                var req = new SearchRequest { MyAnonamouse = mamOptions };
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
                SearchRequest? request = null;
                if (mamFilter != null || mamSearchInDescription.HasValue || mamSearchInSeries.HasValue || mamSearchInFilenames.HasValue || mamLanguage != null || mamFreeleechWedge != null || mamEnrichResults.HasValue || mamEnrichTopResults.HasValue)
                {
                    request = new SearchRequest();
                    request.MyAnonamouse = new MyAnonamouseOptions();

                    if (mamSearchInDescription.HasValue) request.MyAnonamouse.SearchInDescription = mamSearchInDescription.Value;
                    if (mamSearchInSeries.HasValue) request.MyAnonamouse.SearchInSeries = mamSearchInSeries.Value;
                    if (mamSearchInFilenames.HasValue) request.MyAnonamouse.SearchInFilenames = mamSearchInFilenames.Value;
                    if (!string.IsNullOrWhiteSpace(mamLanguage)) request.MyAnonamouse.SearchLanguage = mamLanguage;

                    if (!string.IsNullOrWhiteSpace(mamFilter) && Enum.TryParse<MamTorrentFilter>(mamFilter, true, out var mf))
                        request.MyAnonamouse.Filter = mf;

                    if (!string.IsNullOrWhiteSpace(mamFreeleechWedge) && Enum.TryParse<MamFreeleechWedge>(mamFreeleechWedge, true, out var fw))
                        request.MyAnonamouse.FreeleechWedge = fw;
                    if (mamEnrichResults.HasValue) request.MyAnonamouse.EnrichResults = mamEnrichResults.Value;
                    if (mamEnrichTopResults.HasValue) request.MyAnonamouse.EnrichTopResults = mamEnrichTopResults.Value;
                }

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


