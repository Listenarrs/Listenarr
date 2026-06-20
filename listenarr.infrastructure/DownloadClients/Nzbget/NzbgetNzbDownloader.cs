/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Text;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Nzbget
{
    internal sealed class NzbgetNzbDownloader
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _clientType;
        private readonly ILogger _logger;

        public NzbgetNzbDownloader(IHttpClientFactory httpClientFactory, string clientType, ILogger logger)
        {
            _httpClientFactory = httpClientFactory;
            _clientType = clientType;
            _logger = logger;
        }

        public async Task<byte[]> DownloadAsync(string nzbUrl, string? indexerApiKey, CancellationToken ct)
        {
            if (!OutboundRequestSecurity.TryValidateExternalHttpUrl(nzbUrl, out var ssrfReason, allowPrivateTargets: true))
            {
                _logger.LogWarning("Blocked SSRF attempt in NZB download: {Reason}", ssrfReason);
                throw new InvalidOperationException($"NZB URL blocked: {ssrfReason}");
            }

            try
            {
                _logger.LogDebug("Downloading NZB from {Url}", LogRedaction.SanitizeUrl(nzbUrl));

                var httpClient = _httpClientFactory.CreateClient(_clientType);
                using var request = new HttpRequestMessage(HttpMethod.Get, nzbUrl);
                request.Headers.Add("User-Agent", "Listenarr/1.0.0.0");

                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                _logger.LogDebug("NZB download response: StatusCode={StatusCode}, ContentType={ContentType}, ContentLength={ContentLength}",
                    response.StatusCode,
                    response.Content.Headers.ContentType?.ToString() ?? "null",
                    response.Content.Headers.ContentLength?.ToString() ?? "unknown");

                response.EnsureSuccessStatusCode();

                var contentBytes = await response.Content.ReadAsByteArrayAsync(ct);

                _logger.LogInformation("Downloaded NZB content: {Size} bytes", contentBytes.Length);

                if (contentBytes.Length > 0 && contentBytes.Length < 500)
                {
                    var contentText = Encoding.UTF8.GetString(contentBytes);
                    _logger.LogWarning("NZB content is suspiciously small ({Size} bytes). Content: {Content}",
                        contentBytes.Length, contentText);
                }

                if (contentBytes.Length == 0)
                {
                    _logger.LogError("Downloaded NZB file is empty (0 bytes) from {Url}", LogRedaction.SanitizeUrl(nzbUrl));
                    throw new InvalidOperationException($"Downloaded NZB file is empty from {nzbUrl}");
                }

                return contentBytes;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to download NZB content from {Url}", LogRedaction.SanitizeUrl(nzbUrl));
                throw new InvalidOperationException($"Unable to retrieve NZB content from {nzbUrl}");
            }
        }
    }
}
