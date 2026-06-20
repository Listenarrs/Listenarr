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
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Search.Providers.MyAnonamouse;

public sealed class MyAnonamouseConnectionTester : IMyAnonamouseConnectionTester
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

    public async Task<MyAnonamouseConnectionTestResult> TestAsync(
        Indexer indexer,
        string mamId,
        CancellationToken cancellationToken = default)
    {
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
                if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                {
                    return MyAnonamouseConnectionTestResult.Failure(
                        "MyAnonamouse authentication failed.",
                        (int)response.StatusCode);
                }

                if (!response.IsSuccessStatusCode)
                {
                    return MyAnonamouseConnectionTestResult.Failure(
                        $"MyAnonamouse returned HTTP {(int)response.StatusCode}.",
                        (int)response.StatusCode);
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(content);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array)
                {
                    return MyAnonamouseConnectionTestResult.Failure(
                        "MyAnonamouse returned an invalid JSON response.");
                }

                return MyAnonamouseConnectionTestResult.Success(
                    MyAnonamouseHelper.TryExtractMamIdFromResponse(response));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return MyAnonamouseConnectionTestResult.Failure(
                "The MyAnonamouse connection test was cancelled.");
        }
        catch (OperationCanceledException)
        {
            return MyAnonamouseConnectionTestResult.Failure(
                "The MyAnonamouse connection test timed out.");
        }
        catch (JsonException)
        {
            return MyAnonamouseConnectionTestResult.Failure(
                "MyAnonamouse returned an invalid JSON response.");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or UriFormatException)
        {
            _logger.LogWarning(ex, "MyAnonamouse connection test request failed");
            return MyAnonamouseConnectionTestResult.Failure(
                "The MyAnonamouse connection request failed.");
        }
    }
}
