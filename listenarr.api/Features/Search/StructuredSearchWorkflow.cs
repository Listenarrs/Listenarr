/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Text.Json;

namespace Listenarr.Api.Features.Search
{
    public sealed class StructuredSearchWorkflow
    {
        private readonly ISearchService _searchService;
        private readonly Microsoft.Extensions.Logging.ILogger<StructuredSearchWorkflow> _logger;
        private readonly AudibleService _audibleService;
        private readonly IImageCacheService? _imageCacheService;
        private readonly MetadataConverters _metadataConverters;
        private readonly SearchResponseMapper _responseMapper;
        private readonly SearchRequestReader _requestReader;
        private readonly IConfigurationService? _configurationService;

        public StructuredSearchWorkflow(
            ISearchService searchService,
            Microsoft.Extensions.Logging.ILogger<StructuredSearchWorkflow> logger,
            AudibleService audibleService,
            IAudiobookMetadataService metadataService,
            IImageCacheService? imageCacheService = null,
            MetadataConverters? metadataConverters = null,
            SearchResponseMapper? responseMapper = null,
            IConfigurationService? configurationService = null)
        {
            _searchService = searchService;
            _logger = logger;
            _audibleService = audibleService;
            _imageCacheService = imageCacheService;
            _configurationService = configurationService;
            _metadataConverters = metadataConverters ?? new MetadataConverters(imageCacheService, Microsoft.Extensions.Logging.Abstractions.NullLogger<MetadataConverters>.Instance);
            _responseMapper = responseMapper ?? new SearchResponseMapper(
                metadataService,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SearchResponseMapper>.Instance,
                imageCacheService);
            _requestReader = new SearchRequestReader(_logger);
        }

        public async Task<StructuredSearchWorkflowResult> ExecuteAsync(JsonElement reqJson, bool? simplified, HttpContext httpContext)
        {
            try
            {
                if (reqJson.ValueKind == JsonValueKind.Undefined || reqJson.ValueKind == JsonValueKind.Null)
                {
                    return StructuredSearchWorkflowResult.BadRequest("SearchRequest body is required");
                }

                var req = _requestReader.Read(reqJson);
                if (req == null) return StructuredSearchWorkflowResult.BadRequest("SearchRequest body is required");
                _logger.LogDebug("[DBG] Search received mode={Mode}, query='{Query}'", req.Mode, LogRedaction.SanitizeText(req.Query ?? "<null>"));

                var useSimplified = simplified ?? true;

                if (req.Mode == SearchMode.Simple)
                {
                    return await ExecuteSimpleSearchAsync(req, JsonObjectHasProperty(reqJson, "region"), httpContext);
                }

                return await ExecuteAdvancedSearchAsync(req, useSimplified, JsonObjectHasProperty(reqJson, "region"), httpContext);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error parsing search request body");
                return StructuredSearchWorkflowResult.BadRequest("Invalid search request");
            }
        }

        private async Task<StructuredSearchWorkflowResult> ExecuteSimpleSearchAsync(SearchRequest req, bool hasRequestedRegion, HttpContext httpContext)
        {
            var q = req.Query ?? string.Empty;
            var region = await ResolveSearchRegionAsync(hasRequestedRegion ? req.Region : null);
            var language = string.IsNullOrWhiteSpace(req.Language) ? null : req.Language;
            var results = await _searchService.IntelligentSearchAsync(q, region: region, language: language, ct: httpContext.RequestAborted) ?? new List<MetadataSearchResult>();

            await SearchResultImageNormalizer.NormalizeMetadataResultsAsync(
                results,
                _imageCacheService,
                httpContext,
                _logger,
                "metadata result",
                setApiPathWhenNoExternalImage: true);

            var mapped = await Task.WhenAll((results ?? new List<MetadataSearchResult>()).Select(r => _responseMapper.MapMetadataResultToAudibleAsync(r, region, httpContext))).ConfigureAwait(false);
            _logger.LogDebug("[DBG] Search(simple) returning {Count} metadata results", mapped?.Length ?? 0);
            return StructuredSearchWorkflowResult.Ok(mapped);
        }

        private async Task<StructuredSearchWorkflowResult> ExecuteAdvancedSearchAsync(SearchRequest req, bool useSimplified, bool hasRequestedRegion, HttpContext httpContext)
        {
            var advancedValidationError = _requestReader.NormalizeAdvancedRequest(req);
            if (!string.IsNullOrWhiteSpace(advancedValidationError))
            {
                return StructuredSearchWorkflowResult.BadRequest(advancedValidationError);
            }

            var region = await ResolveSearchRegionAsync(hasRequestedRegion ? req.Region : null);
            var language = string.IsNullOrWhiteSpace(req.Language) ? null : req.Language;

            if (string.IsNullOrWhiteSpace(req.Title)
                && string.IsNullOrWhiteSpace(req.Author)
                && string.IsNullOrWhiteSpace(req.Query)
                && string.IsNullOrWhiteSpace(req.Isbn)
                && string.IsNullOrWhiteSpace(req.Asin)
                && string.IsNullOrWhiteSpace(req.Series))
            {
                return StructuredSearchWorkflowResult.BadRequest("At least one advanced search parameter (title, author, isbn, asin, series, or query) is required");
            }

            LogAdvancedRequest(req, region, language);

            var asinResult = await TryExecuteAsinSearchAsync(req, useSimplified, region, language, httpContext);
            if (asinResult != null)
            {
                return asinResult;
            }

            var seriesResult = await TryExecuteSeriesSearchAsync(req, region, language, httpContext);
            if (seriesResult != null)
            {
                return seriesResult;
            }

            var query = ComposeAdvancedQuery(req);
            var candidateLimit = req.Cap.HasValue ? Math.Clamp(req.Cap.Value, 5, 2000) : 200;
            var returnLimit = req.Pagination != null && req.Pagination.Limit > 0 ? Math.Clamp(req.Pagination.Limit, 1, 1000) : 50;
            var results = await _searchService.IntelligentSearchAsync(query, candidateLimit, returnLimit, region: region, language: language, ct: httpContext.RequestAborted);

            await _responseMapper.NormalizeMetadataResultImagesAsync(results, httpContext, "result");
            results = ApplySeriesFilter(req, results);

            var flatMapped = await Task.WhenAll((results ?? new List<MetadataSearchResult>()).Select(r => _responseMapper.MapMetadataResultToAudibleAsync(r, region, httpContext))).ConfigureAwait(false);
            return StructuredSearchWorkflowResult.Ok(flatMapped);
        }

        private void LogAdvancedRequest(SearchRequest req, string region, string? language)
        {
            try { _logger.LogInformation("[DBG] Advanced search request: Author='{Author}', Title='{Title}', Isbn='{Isbn}', Asin='{Asin}', Query='{Query}', Region='{Region}', Language='{Language}'", LogRedaction.SanitizeText(req.Author), LogRedaction.SanitizeText(req.Title), LogRedaction.SanitizeText(req.Isbn), LogRedaction.SanitizeText(req.Asin), LogRedaction.SanitizeText(req.Query), LogRedaction.SanitizeText(region), LogRedaction.SanitizeText(language)); }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                // Nothing is logged here: the call that failed is the logging call itself.
            }
            try { _logger.LogDebug("[DBG] Advanced params: Title='{Title}', Author='{Author}', Isbn='{Isbn}'", LogRedaction.SanitizeText(req.Title), LogRedaction.SanitizeText(req.Author), LogRedaction.SanitizeText(req.Isbn)); }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                // Nothing is logged here: the call that failed is the logging call itself.
            }
        }

        private static bool JsonObjectHasProperty(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private async Task<string> ResolveSearchRegionAsync(string? requestedRegion)
        {
            if (!string.IsNullOrWhiteSpace(requestedRegion))
            {
                return requestedRegion.Trim();
            }

            if (_configurationService != null)
            {
                try
                {
                    var settings = await _configurationService.GetApplicationSettingsAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(settings?.DefaultSearchRegion))
                    {
                        return settings.DefaultSearchRegion.Trim();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to resolve configured default search region; falling back to us");
                }
            }

            return "us";
        }

        private async Task<StructuredSearchWorkflowResult?> TryExecuteAsinSearchAsync(SearchRequest req, bool useSimplified, string region, string? language, HttpContext httpContext)
        {
            if (string.IsNullOrWhiteSpace(req.Asin))
            {
                return null;
            }

            try
            {
                var audible = await _audibleService.GetBookMetadataAsync(req.Asin, region, true, language);
                if (audible != null)
                {
                    var metadata = _metadataConverters.ConvertAudibleToMetadata(audible, req.Asin, source: "Audible");
                    var sr = await _metadataConverters.ConvertMetadataToSearchResultAsync(metadata, req.Asin, req.Title, req.Author, fallbackImageUrl: null, fallbackLanguage: language);
                    _responseMapper.SanitizeResultForPublicApi(sr, region);
                    var md = SearchResultConverters.ToMetadata(sr);
                    await SearchResultImageNormalizer.NormalizeMetadataResultAsync(
                        md,
                        _imageCacheService,
                        httpContext,
                        _logger,
                        "ASIN metadata",
                        setApiPathWhenNoExternalImage: false);
                    if (md != null)
                    {
                        var result = SearchResultConverters.ToSearchResult(md);
                        var asinResults = new List<SearchResult> { result };
                        return StructuredSearchWorkflowResult.Ok(useSimplified ? _responseMapper.SimplifySearchResults(asinResults) : asinResults);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Audible metadata lookup failed for ASIN {Asin} in advanced search; falling back to unified search", req.Asin);
            }

            return null;
        }

        private async Task<StructuredSearchWorkflowResult?> TryExecuteSeriesSearchAsync(SearchRequest req, string region, string? language, HttpContext httpContext)
        {
            if (string.IsNullOrWhiteSpace(req.Series) || !string.IsNullOrWhiteSpace(req.Author))
            {
                return null;
            }

            try
            {
                string? seriesAsin = null;
                var seriesInput = req.Series.Trim();

                if (seriesInput.StartsWith("B0", StringComparison.OrdinalIgnoreCase) && seriesInput.Length >= 10)
                {
                    seriesAsin = seriesInput;
                }
                else
                {
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

                if (!string.IsNullOrWhiteSpace(seriesAsin))
                {
                    var booksObj = await _audibleService.GetBooksBySeriesAsinAsync(seriesAsin, region);
                    var books = booksObj as List<AudibleSearchResult>;

                    if (books != null && books.Any())
                    {
                        if (!string.IsNullOrWhiteSpace(language) && !string.Equals(language, "all", StringComparison.OrdinalIgnoreCase))
                        {
                            var langFilter = language.Trim();
                            books = books.Where(b =>
                                string.IsNullOrWhiteSpace(b.Language) ||
                                string.Equals(b.Language.Trim(), langFilter, StringComparison.OrdinalIgnoreCase))
                                .ToList();
                        }

                        _logger.LogInformation("Series ASIN {SeriesAsin} returned {Count} books (after language filter)", seriesAsin, books.Count);

                        var seriesResults = new List<object>();
                        foreach (var book in books)
                        {
                            try
                            {
                                seriesResults.Add(await _responseMapper.MapAudibleSearchResultToOutputAsync(book, region, httpContext));
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                _logger.LogWarning(ex, "Failed converting series book to output for ASIN {Asin}", book.Asin);
                            }
                        }

                        if (seriesResults.Any())
                        {
                            return StructuredSearchWorkflowResult.Ok(seriesResults);
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

            return null;
        }

        private string ComposeAdvancedQuery(SearchRequest req)
        {
            var queryParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(req.Author)) queryParts.Add($"AUTHOR:{req.Author}");
            if (!string.IsNullOrWhiteSpace(req.Title)) queryParts.Add($"TITLE:{req.Title}");
            if (!string.IsNullOrWhiteSpace(req.Isbn)) queryParts.Add($"ISBN:{req.Isbn}");
            if (!string.IsNullOrWhiteSpace(req.Asin)) queryParts.Add($"ASIN:{req.Asin}");
            if (queryParts.Count == 0 && !string.IsNullOrWhiteSpace(req.Series))
                queryParts.Add(req.Series);

            var query = queryParts.Count > 0 ? string.Join(" ", queryParts) : (req.Query ?? string.Empty);
            try { _logger.LogInformation("Advanced search request composed parts={Parts} -> query='{Query}'", string.Join("|", queryParts), LogRedaction.SanitizeText(query)); }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                // Nothing is logged here: the call that failed is the logging call itself.
            }

            return query;
        }

        private List<MetadataSearchResult>? ApplySeriesFilter(SearchRequest req, List<MetadataSearchResult>? results)
        {
            if (string.IsNullOrWhiteSpace(req.Series) || results == null)
            {
                return results;
            }

            try
            {
                var seriesFilter = req.Series.Trim();
                var ci = System.Globalization.CultureInfo.InvariantCulture.CompareInfo;
                const System.Globalization.CompareOptions diOpts = System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace;
                return System.Text.RegularExpressions.Regex.IsMatch(seriesFilter, @"^B0[A-Z0-9]{8,}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    ? results.Where(r => (!string.IsNullOrWhiteSpace(r.Series) && ci.IndexOf(r.Series, seriesFilter, diOpts) >= 0)
                        || (!string.IsNullOrWhiteSpace(r.Asin) && string.Equals(r.Asin, seriesFilter, StringComparison.OrdinalIgnoreCase))).ToList()
                    : results.Where(r => !string.IsNullOrWhiteSpace(r.Series) && ci.IndexOf(r.Series, seriesFilter, diOpts) >= 0).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to apply series filter '{Series}' to advanced search results", LogRedaction.SanitizeText(req.Series));
                return results;
            }
        }
    }

    public sealed record StructuredSearchWorkflowResult(bool Succeeded, object? Payload)
    {
        public static StructuredSearchWorkflowResult Ok(object? payload) => new(true, payload);

        public static StructuredSearchWorkflowResult BadRequest(object? payload) => new(false, payload);
    }
}
