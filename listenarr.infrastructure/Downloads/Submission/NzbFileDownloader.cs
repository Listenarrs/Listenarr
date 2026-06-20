/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Microsoft.AspNetCore.WebUtilities;

namespace Listenarr.Infrastructure.Downloads.Submission;

public sealed class NzbFileDownloader(
    IHttpClientFactory httpClientFactory,
    IIndexerRepository indexerRepository) : INzbFileDownloader
{
    public async Task<byte[]> DownloadAsync(
        string url,
        int? indexerId,
        CancellationToken cancellationToken = default)
    {
        var resolvedUrl = url;
        if (indexerId is int id)
        {
            var indexer = await indexerRepository.GetByIdAsync(id, cancellationToken);
            if (indexer != null &&
                !string.IsNullOrWhiteSpace(indexer.ApiKey) &&
                !url.Contains("apikey=", StringComparison.OrdinalIgnoreCase))
            {
                resolvedUrl = QueryHelpers.AddQueryString(url, "apikey", indexer.ApiKey);
            }
        }

        if (!OutboundRequestSecurity.TryValidateExternalHttpUrl(
                resolvedUrl,
                out var reason,
                allowPrivateTargets: true))
        {
            throw new DownloadClientSubmissionException($"The NZB URL was rejected: {reason}");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, resolvedUrl);
        request.Headers.UserAgent.ParseAdd("Listenarr/1.0");
        using var response = await httpClientFactory.CreateClient("DownloadClient")
            .SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new DownloadClientSubmissionException(
                $"NZB metadata download failed with HTTP {(int)response.StatusCode}.");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length == 0)
        {
            throw new DownloadClientSubmissionException("The NZB metadata was empty.");
        }

        return bytes;
    }
}
