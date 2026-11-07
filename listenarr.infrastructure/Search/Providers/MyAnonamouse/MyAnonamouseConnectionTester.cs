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
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Search.Providers.MyAnonamouse;

public sealed class MyAnonamouseConnectionTester : IIndexerConnectionTester
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<MyAnonamouseConnectionTester> _logger;

    public MyAnonamouseConnectionTester(
        HttpClient httpClient,
        ILogger<MyAnonamouseConnectionTester> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public string IndexerType => "MyAnonamouse";

    public async Task<IndexerConnectionTestResult> TestAsync(
        Indexer indexer,
        CancellationToken cancellationToken = default)
    {
        var mamId = MyAnonamouseHelper.TryGetMamId(indexer.AdditionalSettings);
        if (string.IsNullOrWhiteSpace(mamId))
        {
            return IndexerConnectionTestResult.Failure(
                "MyAnonamouse test failed.",
                "MAM ID is required for MyAnonamouse.");
        }

        try
        {
            var searchUri = MyAnonamouseRequestFactory.BuildSearchUri(indexer, "test", perPage: 1);
            var useInjectedClient = _httpClient.BaseAddress != null;
            using var authenticatedClient = useInjectedClient
                ? null
                : MyAnonamouseHelper.CreateAuthenticatedHttpClient(mamId, indexer.Url);
            var client = authenticatedClient ?? _httpClient;

            var (response, _) = await OutboundRequestSecurity.SendWithValidatedRedirectsAsync(
                uri => MyAnonamouseRequestFactory.CreateSearchRequest(uri, mamId, useInjectedClient),
                searchUri,
                client,
                _logger,
                allowPrivateTargets: true,
                cancellationToken: cancellationToken);

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return IndexerConnectionTestResult.Failure(
                        "MyAnonamouse authentication failed.",
                        $"MyAnonamouse returned HTTP {(int)response.StatusCode}.",
                        (int)response.StatusCode);
                }

                if (!response.IsSuccessStatusCode)
                {
                    return IndexerConnectionTestResult.Failure(
                        "MyAnonamouse test failed.",
                        $"MyAnonamouse returned HTTP {(int)response.StatusCode}.",
                        (int)response.StatusCode);
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(content);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array)
                {
                    return IndexerConnectionTestResult.Failure(
                        "MyAnonamouse test failed.",
                        "MyAnonamouse returned an invalid JSON response.");
                }

                var refreshedMamId = MyAnonamouseHelper.TryExtractMamIdFromResponse(response);
                return IndexerConnectionTestResult.Success(
                    "MyAnonamouse authentication successful.",
                    mamId: refreshedMamId);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                "MAM connection test request failed with {ExceptionType}",
                ex.GetType().Name);
            return IndexerConnectionTestResult.Failure(
                "MyAnonamouse test failed.",
                "The MyAnonamouse connection request failed.");
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "MyAnonamouse connection test timed out");
            return IndexerConnectionTestResult.Failure("MyAnonamouse test failed.", "The MyAnonamouse connection test timed out.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "MyAnonamouse connection test returned invalid JSON");
            return IndexerConnectionTestResult.Failure("MyAnonamouse test failed.", "MyAnonamouse returned an invalid JSON response.");
        }
        catch (CookieException ex)
        {
            _logger.LogWarning(
                "MAM connection test cookie was invalid with {ExceptionType}",
                ex.GetType().Name);
            return IndexerConnectionTestResult.Failure("MyAnonamouse test failed.", "The configured MAM ID cookie is invalid.");
        }
        catch (UriFormatException ex)
        {
            _logger.LogWarning(
                "MAM connection test URL was invalid with {ExceptionType}",
                ex.GetType().Name);
            return IndexerConnectionTestResult.Failure(
                "MyAnonamouse test failed.",
                "The configured MyAnonamouse indexer URL is invalid.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                "MAM connection test could not be completed with {ExceptionType}",
                ex.GetType().Name);
            return IndexerConnectionTestResult.Failure("MyAnonamouse test failed.", ex.Message);
        }
    }
}
