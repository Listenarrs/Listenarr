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
    internal sealed class QbittorrentQueueFetchWorkflow(
        IHttpClientFactory httpClientFactory,
        QbittorrentAuthSession authSession,
        ILogger<QbittorrentAdapter> logger,
        string clientType)
    {
        public async Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, List<string> ids, CancellationToken ct = default)
        {
            var items = new List<QueueItem>();
            if (client == null) return items;

            var isMonitorPoll = ids.Count > 0;
            var baseUrl = DownloadClientUriBuilder.BuildAuthority(client);

            try
            {
                using var httpClient = httpClientFactory.CreateClient(clientType);
                try
                {
                    await authSession.LoginAsync(httpClient, client, ct);
                }
                catch (QbittorrentException exception)
                {
                    var message = $"qBittorrent authentication failed for client {LogRedaction.SanitizeText(client.Id)}.";
                    logger.LogWarning(exception, "qBittorrent authentication failed for client {ClientId}", LogRedaction.SanitizeText(client.Id));
                    if (isMonitorPoll)
                    {
                        throw new DownloadClientAdapterPollingException(message, exception);
                    }
                    return items;
                }

                // Limit fields returned to reduce memory usage.
                var fields = "name,progress,size,downloaded,dlspeed,eta,state,hash,added_on,num_seeds,num_leechs,ratio,save_path";
                var categoryFilter = QBittorrentHelpers.BuildCategoryParameter(client.Settings, "&");

                var category = client.Settings?.TryGetValue("category", out var categoryObj) is true
                    ? categoryObj?.ToString()
                    : null;
                QBittorrentHelpers.LogCategoryFiltering(logger, category);

                // Display requests intentionally fetch a broad snapshot. ID-filtered monitor
                // requests use qBittorrent's hashes parameter so large queues do not become
                // unnecessary full snapshots.
                var hashesFilter = ids.Count > 0
                    ? $"&hashes={Uri.EscapeDataString(string.Join('|', ids.Select(id => id.ToLowerInvariant())))}"
                    : string.Empty;

                using var torrentsResp = await httpClient.GetAsync($"{baseUrl}/api/v2/torrents/info?fields={Uri.EscapeDataString(fields)}{categoryFilter}{hashesFilter}", ct);
                if (!torrentsResp.IsSuccessStatusCode)
                {
                    var message = $"qBittorrent queue request failed with status {torrentsResp.StatusCode} for client {LogRedaction.SanitizeText(client.Id)}.";
                    logger.LogWarning("qBittorrent queue request failed with status {Status} for client {ClientId}", torrentsResp.StatusCode, LogRedaction.SanitizeText(client.Id));
                    if (isMonitorPoll)
                    {
                        throw new DownloadClientAdapterPollingException(message);
                    }
                    return items;
                }

                var json = await torrentsResp.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(json))
                {
                    var message = $"qBittorrent returned an empty queue response for client {LogRedaction.SanitizeText(client.Id)}.";
                    logger.LogWarning("qBittorrent returned an empty queue response for client {ClientId}", LogRedaction.SanitizeText(client.Id));
                    if (isMonitorPoll)
                    {
                        throw new DownloadClientAdapterPollingException(message);
                    }
                    return items;
                }

                var torrents = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json);
                if (torrents == null)
                {
                    var message = $"qBittorrent returned an invalid queue response for client {LogRedaction.SanitizeText(client.Id)}.";
                    logger.LogWarning("qBittorrent returned an invalid queue response for client {ClientId}", LogRedaction.SanitizeText(client.Id));
                    if (isMonitorPoll)
                    {
                        throw new DownloadClientAdapterPollingException(message);
                    }
                    return items;
                }

                foreach (var torrent in torrents)
                {
                    var hash = torrent.TryGetValue("hash", out var hashEl) && hashEl.ValueKind == JsonValueKind.String
                        ? hashEl.GetString() ?? string.Empty
                        : string.Empty;

                    // One torrent that cannot be read must not take the rest of the response with
                    // it. Without this, an exception raised while mapping torrent N escapes the
                    // loop and is caught only by the handler below, so torrents N..end are dropped
                    // while the poll still reports itself as a healthy live snapshot: the queue
                    // simply appears shorter, with nothing to say a row was lost.
                    try
                    {
                        List<Dictionary<string, JsonElement>> files = [];
                        using var filesResp = await httpClient.GetAsync($"{baseUrl}/api/v2/torrents/files?hash={hash}", ct);
                        if (filesResp.IsSuccessStatusCode)
                        {
                            var filesJson = await filesResp.Content.ReadAsStringAsync(ct);
                            files = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(filesJson) ?? [];
                        }

                        items.Add(QbittorrentResponseMapper.MapQueueItem(torrent, client, files));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        logger.LogWarning(
                            ex,
                            "Skipping unreadable qBittorrent torrent {TorrentHash} for client {ClientId}; the rest of the queue is unaffected",
                            LogRedaction.SanitizeText(hash),
                            LogRedaction.SanitizeText(client.Id));
                    }
                }
            }
            catch (DownloadClientAdapterPollingException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Error getting qBittorrent queue - client may be unreachable");
                if (isMonitorPoll)
                {
                    throw new DownloadClientAdapterPollingException("Error polling qBittorrent queue.", ex);
                }
            }

            return FilterByIds(items, ids);
        }

        private static List<QueueItem> FilterByIds(List<QueueItem> items, List<string> ids)
        {
            if (ids.Count == 0)
            {
                return items;
            }

            var idSet = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return [.. items.Where(item => !string.IsNullOrWhiteSpace(item.Id) && idSet.Contains(item.Id))];
        }
    }
}
