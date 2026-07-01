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
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Search.Providers.Torznab;

public sealed class TorznabNewznabConnectionTester : IIndexerConnectionTester
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TorznabNewznabConnectionTester> _logger;

    public TorznabNewznabConnectionTester(
        HttpClient httpClient,
        ILogger<TorznabNewznabConnectionTester> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public string IndexerType => "TorznabNewznab";

    public async Task<IndexerConnectionTestResult> TestAsync(
        Indexer indexer,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(indexer.ApiKey))
        {
            return IndexerConnectionTestResult.Failure(
                "API key required.",
                "API key is required for Newznab/Torznab indexers.");
        }

        try
        {
            var testUrl = BuildSearchTestUrl(indexer);
            var userAgent = BuildUserAgent();

            _logger.LogInformation(
                "Testing Newznab/Torznab indexer {Name} at {Url}",
                LogRedaction.SanitizeText(indexer.Name),
                LogRedaction.SanitizeUrl(testUrl));

            using var response = await SendValidatedAsync(
                uri => CreateRequest(uri, userAgent),
                testUrl,
                cancellationToken);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return IndexerConnectionTestResult.Failure(
                    "Authentication failed.",
                    $"Authentication failed: Indexer returned HTTP {(int)response.StatusCode}.",
                    (int)response.StatusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                return IndexerConnectionTestResult.Failure(
                    "Indexer test failed.",
                    $"Indexer returned HTTP {(int)response.StatusCode}.",
                    (int)response.StatusCode);
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var errorMessage = ParseNewznabError(content);
            if (errorMessage != null)
            {
                var isAuthError = IsAuthenticationError(errorMessage);
                return IndexerConnectionTestResult.Failure(
                    isAuthError ? "Authentication failed." : "Indexer test failed.",
                    errorMessage);
            }

            return IndexerConnectionTestResult.Success("Indexer authentication successful.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Newznab/Torznab connection test request failed");
            return IndexerConnectionTestResult.Failure("Indexer test failed.", ex.Message);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Newznab/Torznab connection test timed out");
            return IndexerConnectionTestResult.Failure("Indexer test failed.", "The indexer connection test timed out.");
        }
        catch (XmlException ex)
        {
            _logger.LogWarning(ex, "Newznab/Torznab connection test returned invalid XML");
            return IndexerConnectionTestResult.Failure("Indexer test failed.", "Indexer returned an invalid XML response.");
        }
        catch (UriFormatException ex)
        {
            _logger.LogWarning(ex, "Newznab/Torznab connection test URL was invalid");
            return IndexerConnectionTestResult.Failure("Indexer test failed.", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Newznab/Torznab connection test could not be completed");
            return IndexerConnectionTestResult.Failure("Indexer test failed.", ex.Message);
        }
    }

    private static HttpRequestMessage CreateRequest(Uri uri, string userAgent)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(userAgent);
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

    private static string BuildSearchTestUrl(Indexer indexer)
    {
        var testUrl = IndexerUrlNormalizer.BuildApiEndpoint(indexer.Url);
        var separator = testUrl.Contains('?') ? '&' : '?';
        return testUrl + separator + "t=search&limit=1&offset=0&apikey=" + Uri.EscapeDataString(indexer.ApiKey ?? string.Empty);
    }

    private static string BuildUserAgent()
    {
        var version = typeof(TorznabNewznabConnectionTester).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        return $"Listenarr/{version} (+https://github.com/listenarrs/listenarr)";
    }

    private static string? ParseNewznabError(string xmlContent)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null
        };

        using var reader = XmlReader.Create(new StringReader(xmlContent), settings);
        var document = XDocument.Load(reader);
        var errorElement = document.Root?.Name.LocalName.Equals("error", StringComparison.OrdinalIgnoreCase) == true
            ? document.Root
            : document.Root?.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("error", StringComparison.OrdinalIgnoreCase));

        if (errorElement == null)
        {
            return null;
        }

        var code = errorElement.Attribute("code")?.Value;
        var description = errorElement.Attribute("description")?.Value ?? errorElement.Value;
        return string.IsNullOrWhiteSpace(description) ? $"Error code: {code}" : description;
    }

    private static bool IsAuthenticationError(string errorMessage)
        => errorMessage.Contains("api", StringComparison.OrdinalIgnoreCase) ||
           errorMessage.Contains("key", StringComparison.OrdinalIgnoreCase) ||
           errorMessage.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
           errorMessage.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
           errorMessage.Contains("authentication", StringComparison.OrdinalIgnoreCase);
}
