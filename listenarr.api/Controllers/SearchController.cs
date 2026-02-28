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

using System;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Listenarr.Domain.Models;
using Listenarr.Api.Services;
using Listenarr.Api.Services.Search;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SearchController : ControllerBase
    {
        private readonly ISearchService _searchService;
        private readonly Microsoft.Extensions.Logging.ILogger _logger;
        private readonly AudimetaService _audimetaService;
        private readonly IAudiobookMetadataService _metadataService;
        private readonly IImageCacheService? _imageCacheService;
        private readonly MetadataConverters _metadataConverters;

        public SearchController(
            ISearchService searchService,
            Microsoft.Extensions.Logging.ILogger<SearchController> logger,
            AudimetaService audimetaService,
            IAudiobookMetadataService metadataService,
            IImageCacheService? imageCacheService = null,
            MetadataConverters? metadataConverters = null)
        {
            _searchService = searchService;
            _logger = logger;
            _audimetaService = audimetaService;
            _metadataService = metadataService;
            _imageCacheService = imageCacheService;
            _metadataConverters = metadataConverters ?? new MetadataConverters(imageCacheService, Microsoft.Extensions.Logging.Abstractions.NullLogger<Listenarr.Api.Services.Search.MetadataConverters>.Instance);
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
                        r.ImageUrl = $"/api/images/{r.Asin}";
                        continue;
                    }

                    // If the result includes an external HTTP(S) image URL, try
                    // to download and cache it using the ASIN as identifier.
                    if (!string.IsNullOrWhiteSpace(r.ImageUrl) && (r.ImageUrl.StartsWith("http://") || r.ImageUrl.StartsWith("https://")))
                    {
                        var downloaded = await _imageCacheService.DownloadAndCacheImageAsync(r.ImageUrl, r.Asin);
                        if (!string.IsNullOrWhiteSpace(downloaded))
                        {
                            r.ImageUrl = $"/api/images/{r.Asin}";
                        }
                        else
                        {
                            // Let the client trigger an on-demand download by including the original URL as a query param
                            r.ImageUrl = $"/api/images/{r.Asin}?url={Uri.EscapeDataString(r.ImageUrl)}";
                        }
                    }
                    // If no external URL was present, map to API endpoint if ASIN present
                    else if (!string.IsNullOrWhiteSpace(r.Asin))
                    {
                        r.ImageUrl = $"/api/images/{r.Asin}";
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogWarning(ex, "Failed to normalize image for search result ASIN {Asin}", r?.Asin);
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

                var req = JsonSerializer.Deserialize<Listenarr.Api.Models.SearchRequest>(reqJson.GetRawText(), options);
                if (req == null) return BadRequest("SearchRequest body is required");
                _logger.LogDebug("[DBG] Search received mode={Mode}, query='{Query}'", req.Mode, req.Query ?? "<null>");

                // Default to simplified=true for both modes (user only needs metadata for Add New feature)
                var useSimplified = simplified ?? true;

                if (req.Mode == Listenarr.Api.Models.SearchMode.Simple)
                {
                    var q = req.Query ?? string.Empty;
                    var region = string.IsNullOrWhiteSpace(req.Region) ? "us" : req.Region;
                    var language = string.IsNullOrWhiteSpace(req.Language) ? null : req.Language;
                    var results = await _searchService.IntelligentSearchAsync(q, region: region, language: language, ct: HttpContext.RequestAborted) ?? new List<MetadataSearchResult>();

                    // Normalize images for metadata results so the SPA receives local /api/images/{asin} when possible
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
                                    r.ImageUrl = $"/api/images/{r.Asin}";
                                    continue;
                                }

                                if (!string.IsNullOrWhiteSpace(r.ImageUrl) && (r.ImageUrl.StartsWith("http://") || r.ImageUrl.StartsWith("https://")))
                                {
                                    var downloaded = await _imageCacheService.DownloadAndCacheImageAsync(r.ImageUrl, r.Asin);
                                    if (!string.IsNullOrWhiteSpace(downloaded))
                                    {
                                        r.ImageUrl = $"/api/images/{r.Asin}";
                                    }
                                    else
                                    {
                                        r.ImageUrl = $"/api/images/{r.Asin}?url={Uri.EscapeDataString(r.ImageUrl)}";
                                    }
                                }
                                else if (!string.IsNullOrWhiteSpace(r.Asin))
                                {
                                    r.ImageUrl = $"/api/images/{r.Asin}";
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                _logger.LogWarning(ex, "Failed to normalize image for metadata result ASIN {Asin}", r?.Asin);
                            }
                        }
                    }

                    // Map metadata results into Audimeta-like objects for public API consumers
                    var mapped = await Task.WhenAll((results ?? new List<MetadataSearchResult>()).Select(r => MapMetadataResultToAudimetaAsync(r, region))).ConfigureAwait(false);
                    _logger.LogDebug("[DBG] Search(simple) returning {Count} metadata results", mapped?.Length ?? 0);
                    return Ok(mapped);
                }
                else // Advanced
                {
                    // Route all advanced search logic through SearchService for normalization, filtering, and orchestration
                    

                    // Validate and normalize ISBN/ASIN inputs for advanced searches.
                    // If an ISBN-10 is supplied, convert it to ISBN-13 using the 978 prefix.
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(req.Isbn))
                        {
                            var rawIsbn = Regex.Replace(req.Isbn ?? string.Empty, "[^0-9Xx]", string.Empty);
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
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
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
                    try { _logger.LogInformation("[DBG] Advanced search request: Author='{Author}', Title='{Title}', Isbn='{Isbn}', Asin='{Asin}', Query='{Query}', Region='{Region}', Language='{Language}'", req.Author, req.Title, req.Isbn, req.Asin, req.Query, region, language); }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                        System.Diagnostics.Debug.WriteLine($"SearchController advanced-search info logging failed: {ex.Message}");
                    }
                    try { _logger.LogDebug("[DBG] Advanced params: Title='{Title}', Author='{Author}', Isbn='{Isbn}'", req.Title, req.Author, req.Isbn); }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                        System.Diagnostics.Debug.WriteLine($"SearchController advanced-search debug logging failed: {ex.Message}");
                    }

                    // If the advanced request contains an ASIN, prefer a direct Audimeta metadata
                    // lookup and return a single enriched SearchResult. ASIN searches should
                    // be authoritative and ignore other advanced inputs.
                    if (!string.IsNullOrWhiteSpace(req.Asin))
                    {
                        try
                        {
                            var audimeta = await _audimetaService.GetBookMetadataAsync(req.Asin, region, true);
                            if (audimeta != null)
                            {
                                // Convert audimeta response to internal metadata then to SearchResult
                                var metadata = _metadataConverters.ConvertAudimetaToMetadata(audimeta, req.Asin, source: "Audimeta");
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
                                            md.ImageUrl = $"/api/images/{md.Asin}";
                                        }
                                        else if (!string.IsNullOrWhiteSpace(md.ImageUrl) && (md.ImageUrl.StartsWith("http://") || md.ImageUrl.StartsWith("https://")))
                                        {
                                            var downloaded = await _imageCacheService.DownloadAndCacheImageAsync(md.ImageUrl, md.Asin);
                                            if (!string.IsNullOrWhiteSpace(downloaded)) md.ImageUrl = $"/api/images/{md.Asin}";
                                            else md.ImageUrl = $"/api/images/{md.Asin}?url={Uri.EscapeDataString(md.ImageUrl)}";
                                        }
                                    }
                                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
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
                            // If audimeta didn't return a record, fall through to unified search below
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                            _logger.LogWarning(ex, "Audimeta lookup failed for ASIN {Asin} in advanced search; falling back to unified search", req.Asin);
                        }
                    }



                    // If a series name or series ASIN was provided, prefer Audimeta series endpoints
                    // If series is provided and no author is supplied, take the series-specialized path. If an author is present, prefer the author flow and later filter by series.
                    if (!string.IsNullOrWhiteSpace(req.Series) && string.IsNullOrWhiteSpace(req.Author))
                    {
                        try
                        {
                            string seriesAsin = req.Series.Trim();
                            // If the provided value does not look like an ASIN, try to search by name
                            if (!(seriesAsin.StartsWith("B0", StringComparison.OrdinalIgnoreCase) && seriesAsin.Length >= 10))
                            {
                                var seriesSearch = await _audimetaService.SearchSeriesByNameAsync(req.Series.Trim(), region);
                                if (seriesSearch == null)
                                {
                                    _logger.LogInformation("No series matches found for '{SeriesName}'", req.Series);
                                    // Fall through to unified search instead of returning empty
                                }
                                else
                                {
                                    // Attempt to extract an ASIN from the returned object. Commonly the result
                                    // is an array of objects containing an 'asin' property.
                                    try
                                    {
                                        var seriesJson = JsonSerializer.Serialize(seriesSearch);
                                        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                                        var root = JsonSerializer.Deserialize<JsonElement>(seriesJson, opts);
                                        if (root.ValueKind == JsonValueKind.Array)
                                        {
                                            string? chosenAsin = null;
                                            foreach (var el in root.EnumerateArray())
                                            {
                                                try
                                                {
                                                    if (el.ValueKind != JsonValueKind.Object) continue;
                                                    string? elRegion = null;
                                                    string? elAsin = null;
                                                    if (el.TryGetProperty("region", out var pRegion) && pRegion.ValueKind == JsonValueKind.String) elRegion = pRegion.GetString();
                                                    if (el.TryGetProperty("asin", out var pAsin) && pAsin.ValueKind == JsonValueKind.String) elAsin = pAsin.GetString();

                                                    // Fallbacks: try 'id', 'slug', 'url'/'link' fields to extract an ASIN-like token if asin is missing
                                                    if (string.IsNullOrWhiteSpace(elAsin))
                                                    {
                                                        if (el.TryGetProperty("id", out var pId) && pId.ValueKind == JsonValueKind.String)
                                                        {
                                                            var idStr = pId.GetString();
                                                            if (!string.IsNullOrWhiteSpace(idStr) && System.Text.RegularExpressions.Regex.IsMatch(idStr, @"B0[A-Z0-9]{8,}", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                                                                elAsin = idStr;
                                                        }

                                                        if (string.IsNullOrWhiteSpace(elAsin) && el.TryGetProperty("slug", out var pSlug) && pSlug.ValueKind == JsonValueKind.String)
                                                        {
                                                            var slug = pSlug.GetString();
                                                            // slug may contain ASIN-like token; search for it
                                                            if (!string.IsNullOrWhiteSpace(slug))
                                                            {
                                                                var m = System.Text.RegularExpressions.Regex.Match(slug, @"B0[A-Z0-9]{8,}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                                                if (m.Success) elAsin = m.Value;
                                                            }
                                                        }

                                                        if (string.IsNullOrWhiteSpace(elAsin) && (el.TryGetProperty("url", out var pUrl) || el.TryGetProperty("link", out pUrl) || el.TryGetProperty("href", out pUrl)) && pUrl.ValueKind == JsonValueKind.String)
                                                        {
                                                            var urlStr = pUrl.GetString();
                                                            if (!string.IsNullOrWhiteSpace(urlStr))
                                                            {
                                                                var m = System.Text.RegularExpressions.Regex.Match(urlStr, @"B0[A-Z0-9]{8,}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                                                if (m.Success) elAsin = m.Value;
                                                            }
                                                        }
                                                    }

                                                    if (!string.IsNullOrWhiteSpace(elRegion) && string.Equals(elRegion, region, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(elAsin))
                                                    {
                                                        chosenAsin = elAsin;
                                                        break;
                                                    }
                                                    if (string.IsNullOrWhiteSpace(chosenAsin) && !string.IsNullOrWhiteSpace(elAsin)) chosenAsin = elAsin;
                                                }
                                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                                    _logger.LogDebug(ex, "Failed to parse audimeta series candidate element for series '{Series}'", req.Series);
                                                }
                                            }
                                            if (!string.IsNullOrWhiteSpace(chosenAsin)) seriesAsin = chosenAsin;
                                        }
                                        else if (root.ValueKind == JsonValueKind.Object)
                                        {
                                            if (root.TryGetProperty("asin", out var pAsin) && pAsin.ValueKind == JsonValueKind.String)
                                            {
                                                seriesAsin = pAsin.GetString() ?? seriesAsin;
                                            }
                                        }
                                    }
                                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                        _logger.LogDebug(ex, "Failed to extract series ASIN from audimeta series search result for '{SeriesName}'", req.Series);
                                    }
                                }
                            }

                            // If we now have a candidate series ASIN, fetch books for the series
                            if (!string.IsNullOrWhiteSpace(seriesAsin))
                            {
                                var booksObj = await _audimetaService.GetBooksBySeriesAsinAsync(seriesAsin, region);
                                if (booksObj != null)
                                {
                                    var json = JsonSerializer.Serialize(booksObj);
                                    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                                    List<Listenarr.Api.Services.AudimetaSearchResult>? books = null;
                                    try
                                    {
                                        var resp = JsonSerializer.Deserialize<List<Listenarr.Api.Services.AudimetaSearchResult>>(json, opts);
                                        books = resp;
                                    }
                                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                        _logger.LogDebug(ex, "Failed to deserialize audimeta series books list for series ASIN {SeriesAsin}", seriesAsin);
                                    }
                                    if (books == null)
                                    {
                                        try
                                        {
                                            var respEnv = JsonSerializer.Deserialize<Listenarr.Api.Services.AudimetaSearchResponse>(json, opts);
                                            books = respEnv?.Results;
                                        }
                                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                            _logger.LogDebug(ex, "Failed to deserialize audimeta series books envelope for series ASIN {SeriesAsin}", seriesAsin);
                                        }
                                    }

                                    if (books != null && books.Any())
                                    {
                                        var converted = new List<SearchResult>();
                                        foreach (var book in books)
                                        {
                                            if (string.IsNullOrWhiteSpace(book.Asin)) continue;
                                            var bookResp = new Listenarr.Api.Services.AudimetaBookResponse
                                            {
                                                Asin = book.Asin,
                                                Title = book.Title,
                                                Subtitle = book.Subtitle,
                                                Authors = book.Authors,
                                                ImageUrl = book.ImageUrl,
                                                LengthMinutes = book.RuntimeLengthMin ?? book.LengthMinutes ?? book.RuntimeMinutes,
                                                Language = book.Language,
                                                BookFormat = book.BookFormat,
                                                Genres = book.Genres,
                                                Series = book.Series,
                                                Publisher = book.Publisher,
                                                Narrators = book.Narrators,
                                                ReleaseDate = book.ReleaseDate,
                                                Isbn = book.Isbn
                                            };
                                            try
                                            {
                                                var meta = _metadataConverters.ConvertAudimetaToMetadata(bookResp, book.Asin ?? string.Empty, "Audimeta");
                                                var sr = await _metadataConverters.ConvertMetadataToSearchResultAsync(meta, book.Asin ?? string.Empty, req.Title, req.Author, fallbackImageUrl: null, fallbackLanguage: language);
                                                sr.IsEnriched = true;
                                                sr.MetadataSource = "Audimeta";
                                                SanitizeResultForPublicApi(sr, region);
                                                converted.Add(sr);
                                            }
                                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                                _logger.LogWarning(ex, "Failed converting audimeta series book to SearchResult for ASIN {Asin}", book.Asin);
                                            }
                                        }

                                        if (converted.Any()) {
                                            // Convert to metadata results and ensure images are normalized for API consumers
                                            var mdList = converted.Select(r => SearchResultConverters.ToMetadata(r)).ToList();

                                            if (_imageCacheService != null)
                                            {
                                                foreach (var md in mdList)
                                                {
                                                    try
                                                    {
                                                        if (string.IsNullOrWhiteSpace(md.Asin)) continue;
                                                        var cached = await _imageCacheService.GetCachedImagePathAsync(md.Asin);
                                                        if (!string.IsNullOrWhiteSpace(cached))
                                                        {
                                                            md.ImageUrl = $"/api/images/{md.Asin}";
                                                            continue;
                                                        }
                                                        if (!string.IsNullOrWhiteSpace(md.ImageUrl) && (md.ImageUrl.StartsWith("http://") || md.ImageUrl.StartsWith("https://")))
                                                        {
                                                            var downloaded = await _imageCacheService.DownloadAndCacheImageAsync(md.ImageUrl, md.Asin);
                                                            if (!string.IsNullOrWhiteSpace(downloaded)) md.ImageUrl = $"/api/images/{md.Asin}";
                                                        }
                                                    }
                                                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                                        _logger.LogWarning(ex, "Failed to normalize image for series metadata ASIN {Asin}", md?.Asin);
                                                    }
                                                }
                                            }

                                            var flatSeries = mdList.Select(SearchResultConverters.ToSearchResult).ToList();
                                            return Ok(flatSeries);
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                            _logger.LogWarning(ex, "Failed to perform series lookup for '{Series}' in advanced search; falling back to unified search", req.Series);
                        }
                    }

                    // Previously there was a special-case path here that handled author-only
                    // advanced searches separately. To ensure all advanced searches (author-only,
                    // author+title, title-only, ISBN, etc.) receive identical metadata
                    // enrichment and conversion, route advanced requests through the
                    // unified IntelligentSearch pipeline below. This guarantees Audimeta
                    // metadata is fetched and converted consistently.

                    // Compose a query string from advanced parameters for unified handling
                    var queryParts = new List<string>();
                    // Prefix author/title/isbn/asin tokens so IntelligentSearch parser
                    // recognizes them and selects the correct search branch (e.g. AUTHOR_TITLE).
                    if (!string.IsNullOrWhiteSpace(req.Author)) queryParts.Add($"AUTHOR:{req.Author}");
                    if (!string.IsNullOrWhiteSpace(req.Title)) queryParts.Add($"TITLE:{req.Title}");
                    if (!string.IsNullOrWhiteSpace(req.Isbn)) queryParts.Add($"ISBN:{req.Isbn}");
                    if (!string.IsNullOrWhiteSpace(req.Asin)) queryParts.Add($"ASIN:{req.Asin}");
                    var query = queryParts.Count > 0 ? string.Join(" ", queryParts) : (req.Query ?? string.Empty);
                    try { _logger.LogInformation("Advanced search request composed parts={Parts} -> query='{Query}'", string.Join("|", queryParts), LogRedaction.SanitizeText(query)); }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                        System.Diagnostics.Debug.WriteLine($"SearchController composed-query logging failed: {ex.Message}");
                    }
                    // Respect optional pagination/candidate caps from the client
                    var candidateLimit = req.Cap.HasValue ? Math.Clamp(req.Cap.Value, 5, 2000) : 200;
                    var returnLimit = req.Pagination != null && req.Pagination.Limit > 0 ? Math.Clamp(req.Pagination.Limit, 1, 1000) : 50;
                    var results = await _searchService.IntelligentSearchAsync(query, candidateLimit, returnLimit, region: region, language: language, ct: HttpContext.RequestAborted);

                    // Ensure images for results are served via our API when possible.
                    // For results that provide an ASIN, prefer the local /api/images/{asin}
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
                                    r.ImageUrl = $"/api/images/{r.Asin}";
                                    continue;
                                }

                                if (!string.IsNullOrWhiteSpace(r.ImageUrl) && (r.ImageUrl.StartsWith("http://") || r.ImageUrl.StartsWith("https://")))
                                {
                                    var downloaded = await _imageCacheService.DownloadAndCacheImageAsync(r.ImageUrl, r.Asin);
                                    if (!string.IsNullOrWhiteSpace(downloaded))
                                    {
                                        r.ImageUrl = $"/api/images/{r.Asin}";
                                    }
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                _logger.LogWarning(ex, "Failed to normalize image for result with ASIN {Asin}", r.Asin);
                            }
                        }
                    }

                    // If both Author and Series were provided in the advanced request, prefer the author flow
                    // and apply a series filter on the resulting candidates using the `Series` key so
                    // advanced author searches can be constrained to a specific series.
                    if (!string.IsNullOrWhiteSpace(req.Author) && !string.IsNullOrWhiteSpace(req.Series) && results != null)
                    {
                        try
                        {
                            var seriesFilter = req.Series.Trim();
                            if (System.Text.RegularExpressions.Regex.IsMatch(seriesFilter, @"^B0[A-Z0-9]{8,}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                            {
                                results = results.Where(r => (!string.IsNullOrWhiteSpace(r.Series) && r.Series.IndexOf(seriesFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                                                            || (!string.IsNullOrWhiteSpace(r.Asin) && string.Equals(r.Asin, seriesFilter, StringComparison.OrdinalIgnoreCase))).ToList();
                            }
                            else
                            {
                                results = results.Where(r => !string.IsNullOrWhiteSpace(r.Series) && r.Series.IndexOf(seriesFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                            _logger.LogDebug(ex, "Failed to apply series filter '{Series}' to advanced author search results", req.Series);
                        }
                    }

                    // Flatten metadata results into Audimeta-like objects for public POST /api/search response
                    var flatMapped = await Task.WhenAll((results ?? new List<MetadataSearchResult>()).Select(r => MapMetadataResultToAudimetaAsync(r, region))).ConfigureAwait(false);
                    return Ok(flatMapped);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
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
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogDebug(ex, "Failed to sanitize public search result for ASIN {Asin}", r.Asin);
            }
        }

        // Map our internal MetadataSearchResult to a lightweight Audimeta-shaped object (async)
        private async Task<object> MapMetadataResultToAudimetaAsync(MetadataSearchResult md, string region)
        {
            // If we have an ASIN and the metadata was enriched, try to fetch the canonical Audimeta payload
            Listenarr.Api.Services.AudimetaBookResponse? aud = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(md?.Asin))
                {
                    aud = await _metadataService.GetAudimetaMetadataAsync(md.Asin, region, true);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogDebug(ex, "Failed to retrieve audimeta metadata for ASIN {Asin}", md?.Asin);
            }

            // If audimeta provided a rich response, prefer it (but normalize image URLs to local /api/images/{asin} when possible)
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
                            imageUrl = $"/api/images/{aud.Asin}";
                        }
                        else if (!string.IsNullOrWhiteSpace(imageUrl) && (imageUrl.StartsWith("http://") || imageUrl.StartsWith("https://")))
                        {
                            var downloaded = await _imageCacheService.DownloadAndCacheImageAsync(imageUrl, aud.Asin);
                            if (!string.IsNullOrWhiteSpace(downloaded)) imageUrl = $"/api/images/{aud.Asin}";
                        }
                        else
                        {
                            // Map to API endpoint even if not cached to keep behaviour consistent
                            imageUrl = $"/api/images/{aud.Asin}";
                            _ = _imageCacheService.DownloadAndCacheImageAsync(aud.ImageUrl ?? imageUrl, aud.Asin);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogWarning(ex, "Failed to normalize audimeta image for {Asin}", aud.Asin);
                }

                var authors = (aud.Authors ?? new List<Listenarr.Api.Services.AudimetaAuthor>()).Where(a => a != null).Select(a => new
                {
                    asin = a!.Asin,
                    name = a!.Name,
                    region = a!.Region ?? region,
                    regions = new[] { a!.Region ?? region },
                    image = (string?)null,
                    updatedAt = DateTime.UtcNow.ToString("o")
                }).ToList();

                var narrators = (aud.Narrators ?? new List<Listenarr.Api.Services.AudimetaNarrator>()).Where(n => n != null).Select(n => new { name = n!.Name, updatedAt = DateTime.UtcNow.ToString("o") }).ToList();

                var genres = (aud.Genres ?? new List<Listenarr.Api.Services.AudimetaGenre>()).Where(g => g != null).Select(g => new
                {
                    asin = g!.Asin,
                    name = g!.Name,
                    type = g!.Type,
                    betterType = (string?)null,
                    updatedAt = DateTime.UtcNow.ToString("o")
                }).ToList();

                var series = (aud.Series ?? new List<Listenarr.Api.Services.AudimetaSeries>()).Where(s => s != null).Select(s => new
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
                    link = (string.IsNullOrWhiteSpace(md?.ProductUrl) ? null : md?.ProductUrl) ?? (!string.IsNullOrWhiteSpace(aud.Asin) ? $"https://www.audible.com/pd/{aud.Asin}" : null),
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
                    // Indicate this was mapped from Audimeta and expose a simple series name list for the client tooltip
                    metadataSource = "audimeta",
                    seriesList = series?.Select(s => $"{s.name}{(s.position != null ? $" #{s.position}" : "")}").ToList(),
                    updatedAt = DateTime.UtcNow.ToString("o")
                };
            }

            // Fallback: build a permissive Audimeta-like object from available MetadataSearchResult fields
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
            return twelve + check.ToString();
        }

        private async Task EnsureCachedImagesForAudimetaResultsAsync(List<AudimetaSearchResult>? results)
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
                        r.ImageUrl = $"/api/images/{r.Asin}";
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(r.ImageUrl))
                    {
                        var downloaded = await _imageCacheService.DownloadAndCacheImageAsync(r.ImageUrl, r.Asin);
                        if (!string.IsNullOrWhiteSpace(downloaded))
                        {
                            r.ImageUrl = $"/api/images/{r.Asin}";
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogWarning(ex, "Failed to ensure cached image for {Asin}", r?.Asin);
                }
            }
        }

        [HttpGet]
        public async Task<ActionResult<List<SearchResult>>> Search(
            [FromQuery] string query,
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
                        if (!string.IsNullOrWhiteSpace(qFromReq))
                        {
                            query = qFromReq;
                        }
                        else
                        {
                            query = query ?? string.Empty;
                        }
                    }
                    catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) { query = query ?? string.Empty; }
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
                // frontend receives local /api/images/{asin} URLs when possible.
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

                            // Use a local copy of the service to make control-flow nullability obvious to the analyzer
                            var svc = cacheService;
                            if (svc == null) break; // defensive: shouldn't happen because of outer check

                            var cached = await svc.GetCachedImagePathAsync(asin);
                            if (!string.IsNullOrWhiteSpace(cached))
                            {
                                r.ImageUrl = $"/api/images/{asin}";
                                continue;
                            }

                            var imageUrl = r.ImageUrl;
                            if (!string.IsNullOrWhiteSpace(imageUrl))
                            {
                                var url = imageUrl!;
                                if (url.StartsWith("http://") || url.StartsWith("https://"))
                                {
                                    var downloaded = await svc.DownloadAndCacheImageAsync(url, asin);
                                    if (!string.IsNullOrWhiteSpace(downloaded))
                                    {
                                        r.ImageUrl = $"/api/images/{asin}";
                                    }
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                            _logger.LogWarning(ex, "Failed to ensure cached image for search result ASIN {Asin}", r?.Asin);
                        }
                    }
                }

                if (enrichedOnly && mdResults != null)
                {
                    response.MetadataResults = mdResults.Where(r => (r?.IsEnriched ?? false)).ToList();
                }
                return Ok(response);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error performing search for query: {Query}", query);
                return StatusCode(500, "Internal server error");
            }
        }

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
                try { _logger.LogDebug("[DEBUG] IntelligentSearch called with query='{Query}'", query ?? "<null>"); }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    System.Diagnostics.Debug.WriteLine($"SearchController IntelligentSearch debug logging failed: {ex.Message}");
                }

                // Also emit a warning-level log so test output captures the value
                try { _logger.LogWarning("[DBG] IntelligentSearch called with query='{Query}'", query ?? "<null>"); }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    System.Diagnostics.Debug.WriteLine($"SearchController IntelligentSearch warning logging failed: {ex.Message}");
                }

                if (string.IsNullOrEmpty(query))
                {
                    return BadRequest("Query parameter is required");
                }

                _logger.LogInformation("IntelligentSearch called for query: {Query}", LogRedaction.SanitizeText(query));
                var region = Request.Query.ContainsKey("region") ? Request.Query["region"].ToString() ?? "us" : "us";
                var language = Request.Query.ContainsKey("language") ? Request.Query["language"].ToString() : null;
                var results = await _searchService.IntelligentSearchAsync(query, candidateLimit, returnLimit, containmentMode, requireAuthorAndPublisher, fuzzyThreshold, region, language, HttpContext.RequestAborted);
                // Normalize images for metadata results so the SPA receives local /api/images/{asin} when possible
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
                                r.ImageUrl = $"/api/images/{r.Asin}";
                                continue;
                            }

                            if (!string.IsNullOrWhiteSpace(r.ImageUrl) && (r.ImageUrl.StartsWith("http://") || r.ImageUrl.StartsWith("https://")))
                            {
                                var downloaded = await _imageCacheService.DownloadAndCacheImageAsync(r.ImageUrl, r.Asin);
                                if (!string.IsNullOrWhiteSpace(downloaded)) r.ImageUrl = $"/api/images/{r.Asin}";
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                            _logger.LogWarning(ex, "Failed to normalize image for metadata result ASIN {Asin}", r?.Asin);
                        }
                    }
                }
                _logger.LogInformation("IntelligentSearch returning {Count} results for query: {Query}", results?.Count ?? 0, LogRedaction.SanitizeText(query));
                return Ok(results ?? new List<MetadataSearchResult>());
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error performing intelligent search for query: {Query}", LogRedaction.SanitizeText(query));
                return StatusCode(500, "Internal server error");
            }
        }

        [HttpGet("audimeta/series")]
        public async Task<ActionResult<object>> SearchAudimetaSeries([FromQuery] string name, [FromQuery] string region = "us")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name)) return BadRequest("name query parameter is required");
                var res = await _audimetaService.SearchSeriesByNameAsync(name, region);
                if (res == null) return NotFound();
                return Ok(res);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error proxying audimeta series search for name {Name}", name);
                return StatusCode(500, "Internal server error");
            }
        }

        [HttpGet("audimeta/series/books/{asin}")]
        public async Task<ActionResult<object>> GetAudimetaSeriesBooks(string asin, [FromQuery] string region = "us")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(asin)) return BadRequest("asin is required");
                var res = await _audimetaService.GetBooksBySeriesAsinAsync(asin, region);
                if (res == null) return NotFound();
                return Ok(res);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error proxying audimeta series books for ASIN {Asin}", asin);
                return StatusCode(500, "Internal server error");
            }
        }

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
                var mamOptions = new Listenarr.Api.Models.MyAnonamouseOptions();
                if (Request.Query.ContainsKey("mamFilter") && Enum.TryParse<Listenarr.Api.Models.MamTorrentFilter>(Request.Query["mamFilter"].ToString() ?? string.Empty, true, out var mamFilter))
                    mamOptions.Filter = mamFilter;
                if (Request.Query.ContainsKey("mamSearchInDescription") && bool.TryParse(Request.Query["mamSearchInDescription"], out var sd)) mamOptions.SearchInDescription = sd;
                if (Request.Query.ContainsKey("mamSearchInSeries") && bool.TryParse(Request.Query["mamSearchInSeries"], out var ss)) mamOptions.SearchInSeries = ss;
                if (Request.Query.ContainsKey("mamSearchInFilenames") && bool.TryParse(Request.Query["mamSearchInFilenames"], out var sf)) mamOptions.SearchInFilenames = sf;
                if (Request.Query.ContainsKey("mamLanguage")) mamOptions.SearchLanguage = Request.Query["mamLanguage"].ToString();
                if (Request.Query.ContainsKey("mamFreeleechWedge") && Enum.TryParse<Listenarr.Api.Models.MamFreeleechWedge>(Request.Query["mamFreeleechWedge"].ToString() ?? string.Empty, true, out var mw)) mamOptions.FreeleechWedge = mw;

                var req = new Listenarr.Api.Models.SearchRequest { MyAnonamouse = mamOptions };
                var results = await _searchService.SearchIndexersAsync(query, category, sortBy, sortDirection, isAutomaticSearch, req);
                _logger.LogInformation("IndexersSearch returning {Count} results for query: {Query}", results.Count, LogRedaction.SanitizeText(query));
                return Ok(results);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error searching indexers for query: {Query}", LogRedaction.SanitizeText(query));
                return StatusCode(500, "Internal server error");
            }
        }

        [HttpPost("test/{apiId}")]
        public async Task<ActionResult<bool>> TestApiConnection(string apiId)
        {
            try
            {
                var isConnected = await _searchService.TestApiConnectionAsync(apiId);
                return Ok(isConnected);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
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
        /// Search for audiobooks using audimeta.de
        /// </summary>
        [HttpGet("audimeta")]
        public async Task<ActionResult<AudimetaSearchResponse>> SearchAudimeta(
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

                var result = await _audimetaService.SearchBooksAsync(query, region: region, language: language);
                if (result == null)
                {
                    return NotFound("No results found");
                }

                return Ok(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error searching audimeta for query: {Query}", query);
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

                    // Try configured metadata sources (audimeta, audnexus, etc.) via AudimetaService first
                    try
                    {
                        var audimeta = await _audimetaService.GetBookMetadataAsync(asin, region, true);
                        if (audimeta != null)
                        {
                            var metadataObj = new
                            {
                                metadata = audimeta,
                                source = "Audimeta",
                                sourceUrl = "https://audimeta.de"
                            };
                            return Ok(new List<object> { metadataObj });
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                        _logger.LogWarning(ex, "Audimeta lookup failed for ASIN {Asin}, trying other configured metadata sources", asin);
                    }

                    // If audimeta didn't return anything, try configured metadata sources directly
                    try
                    {
                        var meta = await _metadataService.GetMetadataAsync(asin, region, true);
                        if (meta != null)
                        {
                            return Ok(new List<object> { meta });
                        }
                        _logger.LogWarning("Metadata lookup returned null for ASIN {Asin}, falling back to intelligent search", asin);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
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
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                        _logger.LogWarning(ex, "Failed to convert search result for title: {Title}", searchResult.Title);
                        continue;
                    }
                }

                _logger.LogInformation("Successfully fetched {Count} enriched results for title search: {Query}", results.Count, query);
                return Ok(results);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
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
                Listenarr.Api.Models.SearchRequest? request = null;
                if (mamFilter != null || mamSearchInDescription.HasValue || mamSearchInSeries.HasValue || mamSearchInFilenames.HasValue || mamLanguage != null || mamFreeleechWedge != null || mamEnrichResults.HasValue || mamEnrichTopResults.HasValue)
                {
                    request = new Listenarr.Api.Models.SearchRequest();
                    request.MyAnonamouse = new Listenarr.Api.Models.MyAnonamouseOptions();

                    if (mamSearchInDescription.HasValue) request.MyAnonamouse.SearchInDescription = mamSearchInDescription.Value;
                    if (mamSearchInSeries.HasValue) request.MyAnonamouse.SearchInSeries = mamSearchInSeries.Value;
                    if (mamSearchInFilenames.HasValue) request.MyAnonamouse.SearchInFilenames = mamSearchInFilenames.Value;
                    if (!string.IsNullOrWhiteSpace(mamLanguage)) request.MyAnonamouse.SearchLanguage = mamLanguage;

                    if (!string.IsNullOrWhiteSpace(mamFilter) && Enum.TryParse<Listenarr.Api.Models.MamTorrentFilter>(mamFilter, true, out var mf))
                        request.MyAnonamouse.Filter = mf;

                    if (!string.IsNullOrWhiteSpace(mamFreeleechWedge) && Enum.TryParse<Listenarr.Api.Models.MamFreeleechWedge>(mamFreeleechWedge, true, out var fw))
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
                    var dtos = idxResults.Select(r => Listenarr.Domain.Models.SearchResultConverters.ToIndexerResultDto(r)).ToList();
                    return Ok(dtos);
                }

                // Otherwise, return the legacy SearchResult shape
                var results = idxResults.Select(r => Listenarr.Domain.Models.SearchResultConverters.ToSearchResult(r)).ToList();
                _logger.LogInformation("SearchByApi returning {Count} results for apiId: {ApiId}", results.Count, apiId);
                return Ok(results);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error searching API {ApiId} for query: {Query}", apiId, query);
                return StatusCode(500, "Internal server error");
            }
        }
    }
}

