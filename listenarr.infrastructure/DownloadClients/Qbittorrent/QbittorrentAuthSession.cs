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
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Qbittorrent
{
    internal sealed class QbittorrentAuthSession
    {
        private readonly ILogger _logger;

        public QbittorrentAuthSession(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<bool> LoginAsync(HttpClient httpClient, DownloadClientConfiguration client, CancellationToken cancellationToken = default)
        {
            var baseUrl = DownloadClientUriBuilder.BuildAuthority(client);

            using var loginData = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("username", client.Username ?? string.Empty),
                new KeyValuePair<string, string>("password", client.Password ?? string.Empty)
            ]);

            using var loginResponse = await httpClient.PostAsync($"{baseUrl}/api/v2/auth/login", loginData, cancellationToken);
            if (!loginResponse.IsSuccessStatusCode)
            {
                _ = await loginResponse.Content.ReadAsStringAsync(cancellationToken);

                if (loginResponse.StatusCode == HttpStatusCode.Forbidden)
                {
                    using var testResp = await httpClient.GetAsync($"{baseUrl}/api/v2/app/version", cancellationToken);
                    if (!testResp.IsSuccessStatusCode)
                    {
                        throw new QbittorrentException($"qBittorrent authentication enabled but credentials are incorrect for {client.Id}");
                    }

                    _logger.LogDebug($"qBittorrent authentication disabled; proceeding without credentials for client {client.Id}");
                }
                else
                {
                    throw new QbittorrentException($"qBittorrent login failed with status {loginResponse.StatusCode}");
                }
            }
            else
            {
                _logger.LogDebug("Authenticated to qBittorrent for client {ClientId}", LogRedaction.SanitizeText(client.Id));
            }

            return true;
        }
    }
}
