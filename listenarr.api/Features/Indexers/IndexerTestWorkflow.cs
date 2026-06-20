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
using Listenarr.Application.Common;

namespace Listenarr.Api.Features.Indexers
{
    public sealed class IndexerTestWorkflow
    {
        private readonly IIndexerRepository _indexerRepository;
        private readonly HttpClient _httpClientNoRedirect;
        private readonly ILogger<IndexerTestWorkflow> _logger;
        private readonly IMyAnonamouseConnectionTester? _myAnonamouseConnectionTester;

        public IndexerTestWorkflow(
            IIndexerRepository indexerRepository,
            HttpClient httpClient,
            ILogger<IndexerTestWorkflow> logger,
            IMyAnonamouseConnectionTester? myAnonamouseConnectionTester = null)
        {
            _indexerRepository = indexerRepository;
            _httpClientNoRedirect = httpClient;
            _logger = logger;
            _myAnonamouseConnectionTester = myAnonamouseConnectionTester;
        }

        public async Task<IndexerTestWorkflowResult> TestGenericIndexerAsync(Indexer indexer, bool persist)
        {
            try
            {
                var target = indexer.Url?.TrimEnd('/') ?? string.Empty;
                var testUrl = target.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? target : target + "/api";

                var implName = (indexer.Implementation ?? string.Empty).Trim().ToLowerInvariant();
                var isNewznabStyle = implName == "newznab" || implName == "torznab";

                if (isNewznabStyle)
                {
                    if (string.IsNullOrWhiteSpace(indexer.ApiKey))
                    {
                        await SaveTestResultAsync(indexer, persist, false, "API key is required for Newznab/Torznab indexers");
                        return IndexerTestWorkflowResult.Failure("API key is required for Newznab/Torznab indexers");
                    }

                    var separator = testUrl.Contains('?') ? '&' : '?';
                    testUrl = testUrl + separator + "t=search&limit=1&offset=0";
                    testUrl = testUrl + "&apikey=" + WebUtility.UrlEncode(indexer.ApiKey);
                }

                var version = typeof(IndexerTestWorkflow).Assembly.GetName().Version?.ToString() ?? "0.0.0";
                var userAgent = $"Listenarr/{version} (+https://github.com/listenarrs/listenarr)";

                _logger.LogInformation("[IndexerTest] GET {Url} UA={UserAgent}", LogRedaction.SanitizeUrl(testUrl), LogRedaction.SanitizeText(userAgent));

                var blockedReason = ValidateOutboundUrl(testUrl);
                if (!string.IsNullOrWhiteSpace(blockedReason))
                {
                    await SaveTestResultAsync(indexer, persist, false, $"Blocked outbound target: {blockedReason}");
                    return IndexerTestWorkflowResult.Failure($"Blocked outbound target: {blockedReason}");
                }

                using var response = await SendValidatedAsync(currentUri =>
                {
                    var retryRequest = new HttpRequestMessage(HttpMethod.Get, currentUri);
                    retryRequest.Headers.UserAgent.ParseAdd(userAgent);
                    if (!string.IsNullOrEmpty(indexer.ApiKey))
                    {
                        retryRequest.Headers.Add("X-Api-Key", indexer.ApiKey);
                    }

                    return retryRequest;
                }, testUrl);

                _logger.LogInformation("[IndexerTest] {Name} responded {StatusCode}", LogRedaction.SanitizeText(indexer.Name), (int)response.StatusCode);

                if (response.StatusCode == HttpStatusCode.Unauthorized ||
                    response.StatusCode == HttpStatusCode.Forbidden)
                {
                    await SaveTestResultAsync(indexer, persist, false, $"Authentication failed: HTTP {(int)response.StatusCode}");
                    return IndexerTestWorkflowResult.Failure("Authentication failed", (int)response.StatusCode);
                }

                if (!response.IsSuccessStatusCode)
                {
                    await SaveTestResultAsync(indexer, persist, false, $"HTTP {(int)response.StatusCode}");
                    return IndexerTestWorkflowResult.Failure("Generic indexer test failed", (int)response.StatusCode);
                }

                if (isNewznabStyle)
                {
                    var xmlContent = await response.Content.ReadAsStringAsync();
                    var errorMessage = NewznabErrorParser.Parse(xmlContent);

                    if (errorMessage != null)
                    {
                        var isAuthError = errorMessage.Contains("api", StringComparison.OrdinalIgnoreCase) ||
                                          errorMessage.Contains("key", StringComparison.OrdinalIgnoreCase) ||
                                          errorMessage.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
                                          errorMessage.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                                          errorMessage.Contains("authentication", StringComparison.OrdinalIgnoreCase);

                        var failureMessage = isAuthError ? $"Authentication failed: {errorMessage}" : errorMessage;
                        await SaveTestResultAsync(indexer, persist, false, failureMessage);
                        return IndexerTestWorkflowResult.Failure(failureMessage);
                    }
                }

                await SaveTestResultAsync(indexer, persist, true, null);
                return IndexerTestWorkflowResult.Success("Indexer authentication successful");
            }
            catch (HttpRequestException ex)
            {
                return await BuildGenericFailureAsync(indexer, persist, ex);
            }
            catch (TaskCanceledException ex)
            {
                return await BuildGenericFailureAsync(indexer, persist, ex);
            }
            catch (JsonException ex)
            {
                return await BuildGenericFailureAsync(indexer, persist, ex);
            }
            catch (UriFormatException ex)
            {
                return await BuildGenericFailureAsync(indexer, persist, ex);
            }
            catch (InvalidOperationException ex)
            {
                return await BuildGenericFailureAsync(indexer, persist, ex);
            }
        }

        public async Task<IndexerTestWorkflowResult> TestInternetArchiveAsync(Indexer indexer, bool persist)
        {
            try
            {
                var collection = "librivoxaudio";
                if (!string.IsNullOrEmpty(indexer.AdditionalSettings))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(indexer.AdditionalSettings);
                        if (doc.RootElement.TryGetProperty("collection", out var collectionProperty))
                        {
                            collection = collectionProperty.GetString() ?? "librivoxaudio";
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse AdditionalSettings for Internet Archive indexer");
                    }
                }

                var testUrl = $"https://archive.org/advancedsearch.php?q=collection:{collection}&rows=1&output=json";

                _logger.LogInformation("Testing Internet Archive indexer '{Name}' with collection '{Collection}'",
                    LogRedaction.SanitizeText(indexer.Name), LogRedaction.SanitizeText(collection));

                using var response = await SendValidatedAsync(
                    currentUri => new HttpRequestMessage(HttpMethod.Get, currentUri),
                    testUrl);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync();
                using var jsonDoc = JsonDocument.Parse(content);

                if (!jsonDoc.RootElement.TryGetProperty("response", out var responseProperty))
                {
                    throw new InvalidOperationException("Invalid response format: missing 'response' property");
                }

                if (!responseProperty.TryGetProperty("docs", out _))
                {
                    throw new InvalidOperationException("Invalid response format: missing 'docs' property");
                }

                await SaveTestResultAsync(indexer, persist, true, null);

                _logger.LogInformation("Internet Archive indexer '{Name}' test succeeded for collection '{Collection}'",
                    LogRedaction.SanitizeText(indexer.Name), LogRedaction.SanitizeText(collection));

                return IndexerTestWorkflowResult.Success(
                    $"Internet Archive connection successful for collection '{collection}'",
                    collection: collection);
            }
            catch (HttpRequestException ex)
            {
                return await BuildInternetArchiveFailureAsync(indexer, persist, ex);
            }
            catch (TaskCanceledException ex)
            {
                return await BuildInternetArchiveFailureAsync(indexer, persist, ex);
            }
            catch (JsonException ex)
            {
                return await BuildInternetArchiveFailureAsync(indexer, persist, ex);
            }
            catch (UriFormatException ex)
            {
                return await BuildInternetArchiveFailureAsync(indexer, persist, ex);
            }
            catch (InvalidOperationException ex)
            {
                return await BuildInternetArchiveFailureAsync(indexer, persist, ex);
            }
        }

        public async Task<IndexerTestWorkflowResult> TestMyAnonamouseAsync(Indexer indexer, bool persist)
        {
            var mamId = MyAnonamouseHelper.TryGetMamId(indexer.AdditionalSettings);
            if (string.IsNullOrWhiteSpace(mamId))
            {
                const string error = "MAM ID is required for MyAnonamouse";
                await SaveTestResultAsync(indexer, persist, false, error);
                return IndexerTestWorkflowResult.Failure("MyAnonamouse test failed", error: error);
            }

            if (_myAnonamouseConnectionTester == null)
            {
                const string error = "MyAnonamouse connection testing is unavailable.";
                await SaveTestResultAsync(indexer, persist, false, error);
                return IndexerTestWorkflowResult.Failure("MyAnonamouse test failed", error: error);
            }

            _logger.LogInformation(
                "Testing MyAnonamouse indexer '{Name}'",
                LogRedaction.SanitizeText(indexer.Name));

            var result = await _myAnonamouseConnectionTester.TestAsync(indexer, mamId);
            if (!result.Succeeded)
            {
                await SaveTestResultAsync(indexer, persist, false, result.Message);
                _logger.LogWarning(
                    "MyAnonamouse indexer '{Name}' test failed: {Message}",
                    LogRedaction.SanitizeText(indexer.Name),
                    LogRedaction.SanitizeText(result.Message));
                return IndexerTestWorkflowResult.Failure(
                    "MyAnonamouse test failed",
                    result.StatusCode,
                    result.Message);
            }

            var activeMamId = mamId;
            if (!string.IsNullOrWhiteSpace(result.RefreshedMamId))
            {
                activeMamId = result.RefreshedMamId;
                indexer.AdditionalSettings = MyAnonamouseHelper.UpdateMamIdInAdditionalSettings(
                    indexer.AdditionalSettings,
                    activeMamId);
            }

            await SaveTestResultAsync(
                indexer,
                persist,
                true,
                null,
                persistAdditionalSettings: !string.IsNullOrWhiteSpace(result.RefreshedMamId));
            _logger.LogInformation(
                "MyAnonamouse indexer '{Name}' test succeeded",
                LogRedaction.SanitizeText(indexer.Name));
            return IndexerTestWorkflowResult.Success(
                "MyAnonamouse authentication successful",
                mamId: activeMamId);
        }

        private async Task<IndexerTestWorkflowResult> BuildGenericFailureAsync(Indexer indexer, bool persist, Exception ex)
        {
            _logger.LogWarning(ex, "Generic indexer test failed for {Name}", LogRedaction.SanitizeText(indexer.Name));
            await SaveTestResultAsync(indexer, persist, false, ex.Message);
            return IndexerTestWorkflowResult.Failure("Indexer test failed", error: ex.Message);
        }

        private async Task<IndexerTestWorkflowResult> BuildInternetArchiveFailureAsync(Indexer indexer, bool persist, Exception ex)
        {
            _logger.LogWarning(ex, "Internet Archive indexer '{Name}' test failed", LogRedaction.SanitizeText(indexer.Name));
            await SaveTestResultAsync(indexer, persist, false, ex.Message);
            return IndexerTestWorkflowResult.Failure("Internet Archive test failed", error: ex.Message);
        }

        private string? ValidateOutboundUrl(string url)
        {
            if (!OutboundRequestSecurity.TryValidateExternalHttpUrl(url, out var reason, allowPrivateTargets: true))
            {
                return reason;
            }

            return null;
        }

        private async Task<HttpResponseMessage> SendValidatedAsync(
            Func<Uri, HttpRequestMessage> requestFactory,
            string url,
            HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
            CancellationToken cancellationToken = default)
        {
            var uri = new Uri(url);
            var (response, _) = await OutboundRequestSecurity.SendWithValidatedRedirectsAsync(
                requestFactory,
                uri,
                _httpClientNoRedirect,
                _logger,
                allowPrivateTargets: true,
                completionOption: completionOption,
                cancellationToken: cancellationToken);
            return response;
        }

        private async Task SaveTestResultAsync(
            Indexer indexer,
            bool persist,
            bool success,
            string? error,
            bool persistAdditionalSettings = false)
        {
            indexer.LastTestedAt = DateTime.UtcNow;
            indexer.LastTestSuccessful = success;
            indexer.LastTestError = error;

            if (persist && indexer.Id != 0)
            {
                var existing = await _indexerRepository.GetByIdAsync(indexer.Id);
                if (existing != null)
                {
                    existing.LastTestedAt = indexer.LastTestedAt;
                    existing.LastTestSuccessful = success;
                    existing.LastTestError = error;
                    if (persistAdditionalSettings)
                    {
                        existing.AdditionalSettings = indexer.AdditionalSettings;
                    }
                    existing.UpdatedAt = DateTime.UtcNow;
                    await _indexerRepository.UpdateAsync(existing);
                }
            }
        }
    }

    public sealed record IndexerTestWorkflowResult(bool Succeeded, string Message, int? Status = null, string? Error = null, string? Collection = null, string? MamId = null)
    {
        public static IndexerTestWorkflowResult Success(string message, string? collection = null, string? mamId = null) => new(true, message, Collection: collection, MamId: mamId);

        public static IndexerTestWorkflowResult Failure(string message, int? status = null, string? error = null) => new(false, message, status, error);
    }
}
