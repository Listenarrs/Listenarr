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

namespace Listenarr.Infrastructure.DownloadClients.Qbittorrent
{
    internal sealed class QbittorrentImportMarkerWorkflow(
        IHttpClientFactory httpClientFactory,
        ILogger<QbittorrentAdapter> logger,
        string clientType)
    {
        /// <summary>
        /// Marks a torrent as imported by changing its category to the configured post-import category.
        /// This allows users to differentiate imported vs active torrents in qBittorrent.
        /// Mirrors Sonarr's MarkItemAsImported behavior.
        /// </summary>
        public async Task<bool> MarkItemAsImportedAsync(DownloadClientConfiguration client, string downloadId, CancellationToken ct = default)
        {
            if (client == null) return false;
            if (string.IsNullOrEmpty(downloadId)) return false;

            var postImportCategory = client.Settings?.GetValueOrDefault("postImportCategory")?.ToString();
            if (string.IsNullOrEmpty(postImportCategory))
            {
                logger.LogDebug("No postImportCategory configured for qBittorrent client {ClientId}, skipping MarkItemAsImported", client.Id);
                return true; // No-op is success
            }

            var baseUrl = DownloadClientUriBuilder.BuildAuthority(client);
            try
            {
                using var httpClient = httpClientFactory.CreateClient(clientType);

                // This intentionally preserves qBittorrent's legacy mark-import path.
                // It performs a best-effort login and category update, returning false
                // instead of throwing so deferred cleanup can retry later.
                using var loginData = QbittorrentCookieSession.CreateLoginContent(client);
                using (await httpClient.PostAsync($"{baseUrl}/api/v2/auth/login", loginData, ct)) { }

                using var setCategoryData = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("hashes", downloadId.ToLowerInvariant()),
                    new KeyValuePair<string, string>("category", postImportCategory)
                });

                using var resp = await httpClient.PostAsync($"{baseUrl}/api/v2/torrents/setCategory", setCategoryData, ct);
                if (resp.IsSuccessStatusCode)
                {
                    logger.LogInformation("Marked torrent {Hash} as imported (category: {Category}) in qBittorrent", downloadId, postImportCategory);
                    return true;
                }

                logger.LogWarning("Failed to mark torrent {Hash} as imported in qBittorrent: {StatusCode}", downloadId, resp.StatusCode);
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Error marking torrent {Hash} as imported in qBittorrent", downloadId);
                return false;
            }
        }
    }
}
