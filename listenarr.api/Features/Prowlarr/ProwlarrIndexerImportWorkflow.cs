/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Net;
using System.Text.Json;
using Listenarr.Api.Dtos;

namespace Listenarr.Api.Features.Prowlarr
{
    public sealed class ProwlarrIndexerImportWorkflow
    {
        private const string IndexerDiscoveryPath = "/api/v1/indexer";

        private readonly IIndexerRepository _indexerRepository;
        private readonly IConfigurationService _configurationService;
        private readonly HttpClient _httpClientNoRedirect;
        private readonly ILogger<ProwlarrIndexerImportWorkflow> _logger;

        public ProwlarrIndexerImportWorkflow(
            IIndexerRepository indexerRepository,
            IConfigurationService configurationService,
            HttpClient httpClient,
            ILogger<ProwlarrIndexerImportWorkflow> logger)
        {
            _indexerRepository = indexerRepository;
            _configurationService = configurationService;
            _httpClientNoRedirect = httpClient;
            _logger = logger;
        }

        public async Task<ProwlarrIndexerImportWorkflowResult> ImportAsync(ProwlarrImportRequestDto? request)
        {
            if (request == null)
            {
                return ProwlarrIndexerImportWorkflowResult.BadRequest("Request body is required");
            }

            var savedConnection = await _configurationService.GetProwlarrImportSettingsAsync(includeSecret: true);
            var effectiveUrl = string.IsNullOrWhiteSpace(request.Url) ? savedConnection.Url : request.Url.Trim();
            var effectivePort = request.ClearPort ? null : request.Port ?? savedConnection.Port;
            var effectiveApiKey = string.IsNullOrWhiteSpace(request.ApiKey) ? savedConnection.ApiKey : request.ApiKey.Trim();
            var effectiveTagFilter = request.TagFilter == null
                ? savedConnection.TagFilter?.Trim()
                : request.TagFilter.Trim();

            if (string.IsNullOrWhiteSpace(effectiveUrl))
            {
                return ProwlarrIndexerImportWorkflowResult.BadRequest("Prowlarr URL is required");
            }

            if (string.IsNullOrWhiteSpace(effectiveApiKey))
            {
                return ProwlarrIndexerImportWorkflowResult.BadRequest("Prowlarr API key is required");
            }

            var baseUrl = ProwlarrImportUrlPlanner.BuildBaseUrl(effectiveUrl, effectivePort);
            var blockedBaseUrlReason = ValidateOutboundUrl(baseUrl);
            if (!string.IsNullOrWhiteSpace(blockedBaseUrlReason))
            {
                return ProwlarrIndexerImportWorkflowResult.BadRequest($"Blocked Prowlarr target: {blockedBaseUrlReason}");
            }

            HttpResponseMessage response;
            string payload;
            Uri? discoveryUri;
            try
            {
                (response, payload, discoveryUri) = await FetchProwlarrIndexersAsync(baseUrl, effectiveApiKey.Trim());
            }
            catch (HttpRequestException ex)
            {
                return BuildProwlarrApiFailure(baseUrl, ex);
            }
            catch (TaskCanceledException ex)
            {
                return BuildProwlarrApiFailure(baseUrl, ex);
            }
            catch (UriFormatException ex)
            {
                return BuildProwlarrApiFailure(baseUrl, ex);
            }
            catch (InvalidOperationException ex)
            {
                return BuildProwlarrApiFailure(baseUrl, ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Prowlarr API returned {StatusCode}: {Body}", (int)response.StatusCode, LogRedaction.SanitizeText(payload));
                    return ProwlarrIndexerImportWorkflowResult.UpstreamError("Prowlarr API error", (int)response.StatusCode, (int)response.StatusCode);
                }
            }

            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return ProwlarrIndexerImportWorkflowResult.UpstreamError("Unexpected Prowlarr API response", StatusCodes.Status502BadGateway);
            }

            var effectiveBaseUrl = ProwlarrImportUrlPlanner.ResolveBaseUrlFromDiscovery(baseUrl, discoveryUri, IndexerDiscoveryPath);
            if (!string.Equals(effectiveBaseUrl, baseUrl, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Prowlarr answered on {Url} after a redirect; using it as the indexer proxy base",
                    LogRedaction.SanitizeUrl(effectiveBaseUrl));
            }

            await _configurationService.SaveProwlarrImportSettingsAsync(new ProwlarrImportConnectionSettings
            {
                Url = effectiveUrl,
                Port = effectivePort,
                ApiKey = string.IsNullOrWhiteSpace(request.ApiKey) ? null : request.ApiKey.Trim(),
                TagFilter = effectiveTagFilter,
            });

            var existingIndexers = await _indexerRepository.GetAllAsync();
            var createdIndexers = new List<Indexer>();
            var skipped = 0;
            Dictionary<string, string>? tagMap = null;

            if (!string.IsNullOrWhiteSpace(effectiveTagFilter))
            {
                tagMap = await TryFetchProwlarrTagMapAsync(effectiveBaseUrl, effectiveApiKey.Trim());
                if ((tagMap == null || tagMap.Count == 0) && ProwlarrIndexerPayloadParser.PayloadRequiresTagMap(doc.RootElement))
                {
                    _logger.LogWarning(
                        "Prowlarr tag-filtered import for {Url} requires tag label lookup, but tags could not be loaded",
                        LogRedaction.SanitizeUrl(baseUrl));
                    return ProwlarrIndexerImportWorkflowResult.UpstreamError("Failed to load Prowlarr tags required for tag-filtered import", StatusCodes.Status502BadGateway);
                }
            }

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.Number)
                {
                    skipped++;
                    continue;
                }

                var indexerId = idProp.GetInt32();
                var categoryIds = ProwlarrIndexerPayloadParser.GetCategoryIds(element);
                var prowlarrTags = ProwlarrIndexerPayloadParser.GetTagValues(element, tagMap);
                var matchesImportFilter = string.IsNullOrWhiteSpace(effectiveTagFilter)
                    ? categoryIds.Contains(3000) || categoryIds.Contains(3030)
                    : prowlarrTags.Any(tag => string.Equals(tag, effectiveTagFilter, StringComparison.OrdinalIgnoreCase));

                if (!matchesImportFilter)
                {
                    skipped++;
                    continue;
                }

                var name = element.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                    ? nameProp.GetString() ?? "Prowlarr Indexer"
                    : "Prowlarr Indexer";
                if (!name.EndsWith(" (Prowlarr)", StringComparison.OrdinalIgnoreCase))
                {
                    name = $"{name} (Prowlarr)";
                }

                var protocol = element.TryGetProperty("protocol", out var protocolProp) && protocolProp.ValueKind == JsonValueKind.String
                    ? protocolProp.GetString() ?? string.Empty
                    : string.Empty;

                var implementation = protocol.Equals("usenet", StringComparison.OrdinalIgnoreCase) ? "Newznab" : "Torznab";
                var proxyUrl = ProwlarrImportUrlPlanner.BuildProxyUrl(effectiveBaseUrl, indexerId);
                var normalizedUrl = ProwlarrImportUrlPlanner.NormalizeProxyUrl(proxyUrl);

                var exists = existingIndexers.FirstOrDefault(i =>
                    ProwlarrImportUrlPlanner.NormalizeProxyUrl(i.Url) == normalizedUrl &&
                    string.Equals(i.Implementation, implementation, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(i.ApiKey ?? string.Empty, effectiveApiKey ?? string.Empty, StringComparison.Ordinal));

                if (exists != null)
                {
                    skipped++;
                    continue;
                }

                var type = protocol.Equals("usenet", StringComparison.OrdinalIgnoreCase) ? "Usenet" : "Torrent";
                var categories = string.Join(',', categoryIds.Where(c => c == 3000 || c == 3030).OrderBy(c => c));

                var isEnabled = true;
                if (element.TryGetProperty("enable", out var enableProp))
                {
                    isEnabled = enableProp.ValueKind == JsonValueKind.True;
                }
                else if (element.TryGetProperty("enabled", out var enabledProp))
                {
                    isEnabled = enabledProp.ValueKind == JsonValueKind.True;
                }

                var indexer = new Indexer
                {
                    Name = name,
                    Type = type,
                    Implementation = implementation,
                    Url = normalizedUrl,
                    ApiKey = string.IsNullOrWhiteSpace(effectiveApiKey) ? null : effectiveApiKey.Trim(),
                    Categories = categories,
                    EnableRss = true,
                    EnableAutomaticSearch = true,
                    EnableInteractiveSearch = true,
                    IsEnabled = isEnabled,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                createdIndexers.Add(await _indexerRepository.AddAsync(indexer));
            }

            return ProwlarrIndexerImportWorkflowResult.Success(createdIndexers, skipped);
        }

        private ProwlarrIndexerImportWorkflowResult BuildProwlarrApiFailure(string baseUrl, Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reach Prowlarr at {Url}", LogRedaction.SanitizeUrl(baseUrl));
            return ProwlarrIndexerImportWorkflowResult.UpstreamError("Failed to reach Prowlarr API", StatusCodes.Status502BadGateway);
        }

        private async Task<(HttpResponseMessage Response, string Payload, Uri? FinalUri)> FetchProwlarrIndexersAsync(string baseUrl, string apiKey)
        {
            var encodedKey = WebUtility.UrlEncode(apiKey);
            // NOTE: This targets external Prowlarr instances, whose API path is /api/v1.
            // It is intentionally independent from Listenarr's own API version segment.
            var endpoints = new List<string>
            {
                $"{baseUrl}{IndexerDiscoveryPath}",
                $"{baseUrl}{IndexerDiscoveryPath}?apikey={encodedKey}"
            };

            HttpResponseMessage? lastResponse = null;
            string lastPayload = string.Empty;

            foreach (var endpoint in endpoints)
            {
                var (response, finalUri) = await SendValidatedAsync(currentUri =>
                {
                    var retryRequest = new HttpRequestMessage(HttpMethod.Get, currentUri);
                    retryRequest.Headers.Add("X-Api-Key", apiKey);
                    return retryRequest;
                }, endpoint);
                var body = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    return (response, body, finalUri);
                }

                lastResponse?.Dispose();
                lastResponse = response;
                lastPayload = body;

                if (response.StatusCode != HttpStatusCode.MethodNotAllowed &&
                    response.StatusCode != HttpStatusCode.Unauthorized &&
                    response.StatusCode != HttpStatusCode.Forbidden)
                {
                    break;
                }
            }

            return (lastResponse ?? new HttpResponseMessage(HttpStatusCode.BadGateway), lastPayload, null);
        }

        private async Task<Dictionary<string, string>?> TryFetchProwlarrTagMapAsync(string baseUrl, string apiKey)
        {
            try
            {
                var encodedKey = WebUtility.UrlEncode(apiKey);
                var endpoints = new List<string>
                {
                    $"{baseUrl}/api/v1/tag",
                    $"{baseUrl}/api/v1/tag?apikey={encodedKey}"
                };

                foreach (var endpoint in endpoints)
                {
                    var (tagResponse, _) = await SendValidatedAsync(currentUri =>
                    {
                        var retryRequest = new HttpRequestMessage(HttpMethod.Get, currentUri);
                        retryRequest.Headers.Add("X-Api-Key", apiKey);
                        return retryRequest;
                    }, endpoint);

                    using var response = tagResponse;
                    var body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        if (response.StatusCode != HttpStatusCode.MethodNotAllowed &&
                            response.StatusCode != HttpStatusCode.Unauthorized &&
                            response.StatusCode != HttpStatusCode.Forbidden)
                        {
                            break;
                        }

                        continue;
                    }

                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    {
                        return null;
                    }

                    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var tag in doc.RootElement.EnumerateArray())
                    {
                        if (!tag.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.Number)
                        {
                            continue;
                        }

                        var id = idProp.GetInt32().ToString();
                        var label =
                            tag.TryGetProperty("label", out var labelProp) && labelProp.ValueKind == JsonValueKind.String
                                ? labelProp.GetString()
                                : tag.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                                    ? nameProp.GetString()
                                    : null;

                        if (!string.IsNullOrWhiteSpace(label))
                        {
                            result[id] = label.Trim();
                        }
                    }

                    return result;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to load Prowlarr tags from {Url}", LogRedaction.SanitizeUrl(baseUrl));
            }

            return null;
        }

        private string? ValidateOutboundUrl(string url)
        {
            // *Arr standard behavior: allow private/loopback destinations for indexer connectivity
            // tests/imports, but still enforce absolute HTTP(S) URLs and block embedded credentials.
            if (!OutboundRequestSecurity.TryValidateExternalHttpUrl(url, out var reason, allowPrivateTargets: true))
            {
                return reason;
            }

            return null;
        }

        private async Task<(HttpResponseMessage Response, Uri FinalUri)> SendValidatedAsync(
            Func<Uri, HttpRequestMessage> requestFactory,
            string url,
            HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
            CancellationToken cancellationToken = default)
        {
            var uri = new Uri(url);
            return await OutboundRequestSecurity.SendWithValidatedRedirectsAsync(
                requestFactory,
                uri,
                _httpClientNoRedirect,
                _logger,
                allowPrivateTargets: true,
                completionOption: completionOption,
                cancellationToken: cancellationToken);
        }
    }

    public sealed record ProwlarrIndexerImportWorkflowResult(
        ProwlarrIndexerImportWorkflowResultKind Kind,
        string? Message,
        int? StatusCode,
        int? UpstreamStatus,
        List<Indexer> CreatedIndexers,
        int SkippedCount)
    {
        public int AddedCount => CreatedIndexers.Count;

        public int Total => AddedCount + SkippedCount;

        public static ProwlarrIndexerImportWorkflowResult Success(List<Indexer> createdIndexers, int skippedCount)
            => new(ProwlarrIndexerImportWorkflowResultKind.Success, null, null, null, createdIndexers, skippedCount);

        public static ProwlarrIndexerImportWorkflowResult BadRequest(string message)
            => new(ProwlarrIndexerImportWorkflowResultKind.BadRequest, message, StatusCodes.Status400BadRequest, null, new List<Indexer>(), 0);

        public static ProwlarrIndexerImportWorkflowResult UpstreamError(string message, int statusCode, int? upstreamStatus = null)
            => new(ProwlarrIndexerImportWorkflowResultKind.UpstreamError, message, statusCode, upstreamStatus, new List<Indexer>(), 0);
    }

    public enum ProwlarrIndexerImportWorkflowResultKind
    {
        Success,
        BadRequest,
        UpstreamError
    }
}
