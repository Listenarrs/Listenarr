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

namespace Listenarr.Infrastructure.DownloadClients.Qbittorrent
{
    internal sealed class QbittorrentItemFetchWorkflow(
        IHttpClientFactory httpClientFactory,
        QbittorrentAuthSession authSession,
        ILogger<QbittorrentAdapter> logger,
        string clientType)
    {
        /// <summary>
        /// Gets all qBittorrent downloads as standardized DownloadClientItem objects.
        /// </summary>
        public async Task<List<DownloadClientItem>> GetItemsAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var items = new List<DownloadClientItem>();
            if (client == null) return items;

            var baseUrl = DownloadClientUriBuilder.BuildAuthority(client);
            var categoryFilter = QBittorrentHelpers.BuildCategoryParameter(client.Settings, "&");

            try
            {
                using var httpClient = httpClientFactory.CreateClient(clientType);
                try
                {
                    await authSession.LoginAsync(httpClient, client, ct);
                }
                catch (QbittorrentException exception)
                {
                    logger.LogWarning(exception, "qBittorrent authentication failed for client {ClientId}", LogRedaction.SanitizeText(client.Id));
                    return items;
                }

                // Fetch qBittorrent global preferences for seed limit evaluation (Sonarr parity).
                // Keep this behavior inside item fetch because queue polling should remain lean.
                bool globalMaxRatioEnabled = false;
                float globalMaxRatio = -1f;
                bool globalMaxSeedingTimeEnabled = false;
                long globalMaxSeedingTime = -1;
                try
                {
                    using var prefsResp = await httpClient.GetAsync($"{baseUrl}/api/v2/app/preferences", ct);
                    if (prefsResp.IsSuccessStatusCode)
                    {
                        var prefsJson = await prefsResp.Content.ReadAsStringAsync(ct);
                        if (!string.IsNullOrWhiteSpace(prefsJson))
                        {
                            var prefs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(prefsJson);
                            if (prefs != null)
                            {
                                globalMaxRatioEnabled = prefs.TryGetValue("max_ratio_enabled", out var mre) && mre.GetBoolean();
                                globalMaxRatio = prefs.TryGetValue("max_ratio", out var mr) ? (float)mr.GetDouble() : -1f;
                                globalMaxSeedingTimeEnabled = prefs.TryGetValue("max_seeding_time_enabled", out var mste) && mste.GetBoolean();
                                globalMaxSeedingTime = prefs.TryGetValue("max_seeding_time", out var mst) ? mst.GetInt64() : -1;
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    logger.LogDebug(ex, "Failed to fetch qBittorrent preferences for seed limit evaluation, will use conservative defaults");
                }

                var removeCompletedDownloads = !string.IsNullOrEmpty(client.RemoveCompletedDownloads) &&
                    client.RemoveCompletedDownloads != "none";

                var fields = "name,progress,size,downloaded,dlspeed,eta,state,hash,added_on,num_seeds,num_leechs,ratio,save_path,category,content_path,ratio_limit,seeding_time_limit,seeding_time";
                using var torrentsResp = await httpClient.GetAsync($"{baseUrl}/api/v2/torrents/info?fields={Uri.EscapeDataString(fields)}{categoryFilter}", ct);
                if (!torrentsResp.IsSuccessStatusCode) return items;

                var json = await torrentsResp.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(json)) return items;

                var torrents = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json);
                if (torrents == null) return items;

                foreach (var torrent in torrents)
                {
                    // Same per-item isolation as the queue fetch. This list is what completion and
                    // import decisions are made from, so a torrent lost here is not just a missing
                    // row in a view: everything after it stops being considered for import at all.
                    try
                    {
                        items.Add(QbittorrentResponseMapper.MapDownloadClientItem(
                            torrent,
                            client,
                            removeCompletedDownloads,
                            globalMaxRatioEnabled,
                            globalMaxRatio,
                            globalMaxSeedingTimeEnabled,
                            globalMaxSeedingTime));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        var hash = torrent.TryGetValue("hash", out var hashEl) && hashEl.ValueKind == JsonValueKind.String
                            ? hashEl.GetString() ?? string.Empty
                            : string.Empty;
                        logger.LogWarning(
                            ex,
                            "Skipping unreadable qBittorrent torrent {TorrentHash} for client {ClientId}; the rest of the item list is unaffected",
                            LogRedaction.SanitizeText(hash),
                            LogRedaction.SanitizeText(client.Id));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Error getting qBittorrent items - client may be unreachable");
            }

            return items;
        }
    }
}
