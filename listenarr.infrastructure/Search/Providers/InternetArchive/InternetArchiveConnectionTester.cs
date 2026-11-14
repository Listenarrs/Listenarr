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

namespace Listenarr.Infrastructure.Search.Providers.InternetArchive;

public sealed class InternetArchiveConnectionTester : IIndexerConnectionTester
{
    private const string DefaultCollection = "librivoxaudio";

    private readonly HttpClient _httpClient;
    private readonly ILogger<InternetArchiveConnectionTester> _logger;

    public InternetArchiveConnectionTester(
        HttpClient httpClient,
        ILogger<InternetArchiveConnectionTester> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public string IndexerType => "InternetArchive";

    public async Task<IndexerConnectionTestResult> TestAsync(
        Indexer indexer,
        CancellationToken cancellationToken = default)
    {
        var collection = ParseCollection(indexer.AdditionalSettings);

        try
        {
            var testUrl = BuildAdvancedSearchUrl(collection);

            _logger.LogInformation(
                "Testing Internet Archive indexer {Name} with collection {Collection}",
                LogRedaction.SanitizeText(indexer.Name),
                LogRedaction.SanitizeText(collection));

            using var response = await SendValidatedAsync(
                currentUri => new HttpRequestMessage(HttpMethod.Get, currentUri),
                testUrl,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return IndexerConnectionTestResult.Failure(
                    "Internet Archive test failed.",
                    $"Internet Archive returned HTTP {(int)response.StatusCode}.",
                    (int)response.StatusCode);
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            using var jsonDocument = JsonDocument.Parse(content);

            if (jsonDocument.RootElement.ValueKind != JsonValueKind.Object ||
                !jsonDocument.RootElement.TryGetProperty("response", out var responseProperty) ||
                responseProperty.ValueKind != JsonValueKind.Object ||
                !responseProperty.TryGetProperty("docs", out _))
            {
                return IndexerConnectionTestResult.Failure(
                    "Internet Archive test failed.",
                    "Invalid response format: missing 'response' or 'docs'.");
            }

            return IndexerConnectionTestResult.Success(
                $"Internet Archive connection successful for collection '{collection}'.",
                collection: collection);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                "Internet Archive connection test request failed with {ExceptionType}",
                ex.GetType().Name);
            return IndexerConnectionTestResult.Failure(
                "Internet Archive test failed.",
                "The Internet Archive connection request failed.");
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Internet Archive connection test timed out");
            return IndexerConnectionTestResult.Failure("Internet Archive test failed.", "The Internet Archive connection test timed out.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Internet Archive connection test returned invalid JSON");
            return IndexerConnectionTestResult.Failure("Internet Archive test failed.", "Internet Archive returned invalid JSON.");
        }
        catch (UriFormatException ex)
        {
            _logger.LogWarning(
                "Internet Archive connection test URL was invalid with {ExceptionType}",
                ex.GetType().Name);
            return IndexerConnectionTestResult.Failure(
                "Internet Archive test failed.",
                "The Internet Archive connection URL is invalid.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                "Internet Archive connection test could not be completed with {ExceptionType}",
                ex.GetType().Name);
            return IndexerConnectionTestResult.Failure("Internet Archive test failed.", ex.Message);
        }
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

    private static string BuildAdvancedSearchUrl(string collection)
        => "https://archive.org/advancedsearch.php?q=" +
           Uri.EscapeDataString($"collection:{collection}") +
           "&rows=1&output=json";

    private static string ParseCollection(string? additionalSettings)
    {
        if (string.IsNullOrWhiteSpace(additionalSettings))
        {
            return DefaultCollection;
        }

        try
        {
            using var settings = JsonDocument.Parse(additionalSettings);
            if (settings.RootElement.ValueKind == JsonValueKind.Object &&
                settings.RootElement.TryGetProperty("collection", out var collectionProperty))
            {
                var collection = collectionProperty.GetString();
                if (!string.IsNullOrWhiteSpace(collection))
                {
                    return collection;
                }
            }
        }
        catch (JsonException)
        {
            return DefaultCollection;
        }

        return DefaultCollection;
    }
}
