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
using System.Text.RegularExpressions;
using AsyncKeyedLock;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Search.Providers.MyAnonamouse
{
    /// <summary>
    /// Search provider for MyAnonamouse private tracker.
    /// Handles cookie-based authentication, JSON API responses, and optional per-result enrichment.
    /// </summary>
    public class MyAnonamouseSearchProvider : IIndexerSearchProvider
    {
        private readonly ILogger<MyAnonamouseSearchProvider> _logger;
        private readonly HttpClient _httpClient;
        private readonly IIndexerRepository _indexerRepository;

        public string IndexerType => "MyAnonamouse";

        public MyAnonamouseSearchProvider(
            ILogger<MyAnonamouseSearchProvider> logger,
            HttpClient httpClient,
            IIndexerRepository indexerRepository)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _indexerRepository = indexerRepository ?? throw new ArgumentNullException(nameof(indexerRepository));
        }

        public async Task<List<IndexerSearchResult>> SearchAsync(Indexer indexer, string query, string? category, SearchRequest? request = null)
        {
            try
            {
                _logger.LogInformation("Searching MyAnonamouse for: {Query}", query);

                // Parse mam_id from AdditionalSettings (robust: case-insensitive and nested)
                var mamId = MyAnonamouseHelper.TryGetMamId(indexer.AdditionalSettings);

                if (string.IsNullOrEmpty(mamId))
                {
                    _logger.LogWarning("MyAnonamouse indexer {Name} missing mam_id", indexer.Name);
                    return new List<IndexerSearchResult>();
                }

                var searchUri = MyAnonamouseRequestFactory.BuildSearchUri(indexer, query, request);
                using var disposableClient = _httpClient.BaseAddress == null
                    ? MyAnonamouseHelper.CreateAuthenticatedHttpClient(mamId, indexer.Url)
                    : null;
                HttpClient httpClientToUse = disposableClient ?? _httpClient;
                var addCookieHeader = _httpClient.BaseAddress != null;

                _logger.LogDebug(
                    "MyAnonamouse API URL: {Url}",
                    LogRedaction.RedactText(
                        searchUri.GetLeftPart(UriPartial.Path),
                        LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { indexer.ApiKey ?? string.Empty })));

                var (searchResponse, _) = await OutboundRequestSecurity.SendWithValidatedRedirectsAsync(
                    uri => MyAnonamouseRequestFactory.CreateSearchRequest(uri, mamId, addCookieHeader),
                    searchUri,
                    httpClientToUse,
                    _logger,
                    allowPrivateTargets: true);
                using var response = searchResponse;
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("MyAnonamouse returned status {Status}", response.StatusCode);
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("MyAnonamouse error response: {Content}", LogRedaction.RedactText(errorContent, LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { indexer.ApiKey ?? string.Empty })));
                    return new List<IndexerSearchResult>();
                }

                // Capture and persist an updated mam_id cookie if the tracker provided one in Set-Cookie
                try
                {
                    var newMam = MyAnonamouseHelper.TryExtractMamIdFromResponse(response);
                    if (!string.IsNullOrEmpty(newMam) && !string.Equals(newMam, mamId, StringComparison.Ordinal))
                    {
                        _logger.LogInformation("MyAnonamouse: received updated mam_id from response for indexer {Name}", indexer.Name);
                        var idx = await _indexerRepository.GetByIdAsync(indexer.Id);
                        if (idx != null)
                        {
                            idx.AdditionalSettings = MyAnonamouseHelper.UpdateMamIdInAdditionalSettings(idx.AdditionalSettings, newMam);
                            await _indexerRepository.UpdateAsync(idx);
                            mamId = newMam;
                        }
                    }
                }
                catch (Exception exMam) when (exMam is not OperationCanceledException && exMam is not OutOfMemoryException && exMam is not StackOverflowException)
                {
                    _logger.LogDebug(exMam, "Failed to persist updated mam_id from MyAnonamouse response");
                }

                var jsonResponse = await response.Content.ReadAsStringAsync();
                _logger.LogDebug("MyAnonamouse raw response: {Response}", jsonResponse);
                var results = MyAnonamouseResponseParser.Parse(jsonResponse, indexer, _logger);

                // Optional per-result enrichment: fetch individual item pages to populate missing fields
                try
                {
                    // Respect global IncludeEnrichment and per-indexer MyAnonamouse options
                    var mamRequestOptions = request?.MyAnonamouse;
                    var shouldEnrich = request?.IncludeEnrichment == true && mamRequestOptions?.EnrichResults == true;
                    if (shouldEnrich)
                    {
                        var enrichTop = mamRequestOptions!.EnrichTopResults ?? 3;
                        await EnrichMyAnonamouseResultsAsync(indexer, results, enrichTop, mamId, httpClientToUse);
                    }
                }
                catch (Exception exEnrich) when (exEnrich is not OperationCanceledException && exEnrich is not OutOfMemoryException && exEnrich is not StackOverflowException)
                {
                    _logger.LogWarning(exEnrich, "MyAnonamouse enrichment step failed");
                }

                _logger.LogInformation("MyAnonamouse returned {Count} results", results.Count);
                return results;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error searching MyAnonamouse indexer {Name}", indexer.Name);
                return new List<IndexerSearchResult>();
            }
        }

        private async Task EnrichMyAnonamouseResultsAsync(Indexer indexer, List<IndexerSearchResult> results, int topN, string? mamId, HttpClient httpClient)
        {
            if (results == null || results.Count == 0) return;
            if (topN <= 0) return;

            var candidates = results.Where(r => (r.Grabs == 0 || r.Files == 0 || string.IsNullOrEmpty(r.Format) || string.IsNullOrEmpty(r.Language))).Take(topN).ToList();
            if (!candidates.Any()) return;

            _logger.LogDebug("Enriching {Count} MyAnonamouse results (topN={TopN})", candidates.Count, topN);

            using var sem = new AsyncNonKeyedLocker(4);
            var tasks = candidates.Select(async r =>
            {
                using var lockHandle = await sem.LockAsync();
                try
                {
                    if (string.IsNullOrEmpty(r.ResultUrl)) return;

                    // Extract torrent ID from result URL
                    var idMatch = Regex.Match(r.ResultUrl, @"/t/(\d+)");
                    if (!idMatch.Success) return;
                    var torrentId = idMatch.Groups[1].Value;

                    // Request JSON detail endpoint
                    var detailUrl = $"{indexer.Url.TrimEnd('/')}/tor/js/loadTorrentJSONBasic.php?id={torrentId}";
                    using var req = new HttpRequestMessage(HttpMethod.Get, detailUrl);
                    req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                    req.Headers.Accept.ParseAdd("application/json");
                    if (!string.IsNullOrEmpty(mamId)) req.Headers.Add("Cookie", $"mam_id={mamId}");

                    using var resp = await httpClient.SendAsync(req);
                    if (!resp.IsSuccessStatusCode) return;
                    var json = await resp.Content.ReadAsStringAsync();

                    // Parse JSON for enrichment fields
                    try
                    {
                        using var detailDocument = JsonDocument.Parse(json);
                        var detail = detailDocument.RootElement;

                        if (detail.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Object)
                        {
                            detail = dataProp;
                        }
                        else if (detail.TryGetProperty("response", out var respProp) && respProp.ValueKind == JsonValueKind.Object)
                        {
                            detail = respProp;
                        }

                        var grabs = 0;
                        var grabKeys = new[] { "grabs", "snatches", "snatched", "snatched_count", "snatches_count", "numgrabs", "num_grabs", "grab_count", "times_completed", "time_completed", "downloaded", "times_downloaded", "completed" };
                        foreach (var gEl in grabKeys.Where(key => detail.TryGetProperty(key, out _)).Select(key => detail.GetProperty(key)))
                        {
                            if (gEl.ValueKind == JsonValueKind.Number)
                            {
                                grabs = gEl.GetInt32();
                                break;
                            }
                            else if (gEl.ValueKind == JsonValueKind.String && int.TryParse(gEl.GetString(), out var gtmp))
                            {
                                grabs = gtmp;
                                break;
                            }
                        }

                        var files = 0;
                        if (detail.TryGetProperty("files", out var filesElem) && filesElem.ValueKind == JsonValueKind.Number)
                        {
                            files = filesElem.GetInt32();
                        }

                        var format = "";
                        if (detail.TryGetProperty("filetype", out var formatElem) && formatElem.ValueKind == JsonValueKind.String)
                        {
                            format = formatElem.GetString() ?? "";
                        }

                        var langCode = "";
                        if (detail.TryGetProperty("lang_code", out var langElem) && langElem.ValueKind == JsonValueKind.String)
                        {
                            langCode = langElem.GetString() ?? "";
                        }

                        // Apply values
                        if (grabs > 0) r.Grabs = grabs;
                        if (files > 0) r.Files = files;
                        if (!string.IsNullOrEmpty(format) && string.IsNullOrEmpty(r.Format)) r.Format = format.ToUpper();
                        if (!string.IsNullOrEmpty(langCode) && string.IsNullOrEmpty(r.Language)) r.Language = SearchResultAttributeParser.ParseLanguageFromCode(langCode);

                        _logger.LogDebug("Enriched MyAnonamouse result {Id}: grabs={Grabs}, files={Files}, format={Format}, language={Language}", r.Id, r.Grabs, r.Files, r.Format, r.Language);
                    }
                    catch (Exception exParse) when (exParse is not OperationCanceledException && exParse is not OutOfMemoryException && exParse is not StackOverflowException)
                    {
                        _logger.LogDebug(exParse, "Failed to parse MyAnonamouse detail JSON for {Id}", r.Id);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogDebug(ex, "Failed to enrich MyAnonamouse result {Id}", r.Id);
                }
            }).ToArray();

            await Task.WhenAll(tasks);
        }

    }
}
