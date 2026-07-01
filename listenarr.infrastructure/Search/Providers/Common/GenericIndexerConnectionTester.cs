/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Search.Providers.Common;

public sealed class GenericIndexerConnectionTester : IIndexerConnectionTester
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GenericIndexerConnectionTester> _logger;

    public GenericIndexerConnectionTester(
        HttpClient httpClient,
        ILogger<GenericIndexerConnectionTester> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public string IndexerType => "Generic";

    public async Task<IndexerConnectionTestResult> TestAsync(
        Indexer indexer,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var testUrl = IndexerUrlNormalizer.BuildApiEndpoint(indexer.Url);
            var userAgent = BuildUserAgent();

            _logger.LogInformation(
                "Testing generic indexer {Name} at {Url}",
                LogRedaction.SanitizeText(indexer.Name),
                LogRedaction.SanitizeUrl(testUrl));

            using var response = await SendValidatedAsync(
                uri => CreateRequest(uri, userAgent, indexer.ApiKey),
                testUrl,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return IndexerConnectionTestResult.Failure(
                    "Generic indexer test failed.",
                    $"Indexer returned HTTP {(int)response.StatusCode}.",
                    (int)response.StatusCode);
            }

            return IndexerConnectionTestResult.Success("Indexer authentication successful.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Generic indexer connection test request failed");
            return IndexerConnectionTestResult.Failure("Indexer test failed.", ex.Message);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Generic indexer connection test timed out");
            return IndexerConnectionTestResult.Failure("Indexer test failed.", "The indexer connection test timed out.");
        }
        catch (UriFormatException ex)
        {
            _logger.LogWarning(ex, "Generic indexer connection test URL was invalid");
            return IndexerConnectionTestResult.Failure("Indexer test failed.", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Generic indexer connection test could not be completed");
            return IndexerConnectionTestResult.Failure("Indexer test failed.", ex.Message);
        }
    }

    private static HttpRequestMessage CreateRequest(Uri uri, string userAgent, string? apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(userAgent);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Add("X-Api-Key", apiKey);
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendValidatedAsync(
        Func<Uri, HttpRequestMessage> requestFactory,
        string url,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(url);
        var (response, _) = await OutboundRequestSecurity.SendWithValidatedRedirectsAsync(
            requestFactory,
            uri,
            _httpClient,
            _logger,
            allowPrivateTargets: true,
            cancellationToken: cancellationToken);
        return response;
    }

    private static string BuildUserAgent()
    {
        var version = typeof(GenericIndexerConnectionTester).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        return $"Listenarr/{version} (+https://github.com/listenarrs/listenarr)";
    }
}
