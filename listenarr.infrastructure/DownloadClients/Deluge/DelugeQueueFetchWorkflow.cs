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

namespace Listenarr.Infrastructure.DownloadClients.Deluge
{
    internal sealed class DelugeQueueFetchWorkflow(
        DelugeRpcClient rpcClient,
        ILogger<DelugeAdapter> logger)
    {
        private static readonly string[] StatusKeys =
        [
            "name", "total_size", "total_done", "progress", "download_payload_rate", "eta", "state",
            "save_path", "label", "ratio", "num_seeds", "num_peers", "time_added", "files", "message"
        ];

        public async Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, List<string> ids, CancellationToken ct = default)
        {
            var items = new List<QueueItem>();
            if (client == null)
            {
                return items;
            }

            var isMonitorPoll = ids.Count > 0;
            var configuredCategory = DownloadClientCategoryFilter.GetConfiguredCategory(client);

            try
            {
                var res = await rpcClient.InvokeAsync(client, "web.update_ui", [StatusKeys, new Dictionary<string, object>()], ct);
                if (!res.TryGetProperty("torrents", out var torrents) || torrents.ValueKind != JsonValueKind.Object)
                {
                    return items;
                }

                foreach (var torrent in torrents.EnumerateObject())
                {
                    var t = torrent.Value;
                    var label = GetString(t, "label");
                    if (!DownloadClientCategoryFilter.Matches(configuredCategory, label))
                    {
                        continue;
                    }

                    var total = GetLong(t, "total_size");
                    var done = GetLong(t, "total_done");
                    var state = GetString(t, "state");
                    var progress = GetDouble(t, "progress");

                    var queueItem = new QueueItem
                    {
                        Id = torrent.Name,
                        Title = GetString(t, "name"),
                        Status = MapStatus(state, progress).ToString().ToLowerInvariant(),
                        Progress = progress,
                        Size = total,
                        Downloaded = done,
                        DownloadSpeed = GetDouble(t, "download_payload_rate"),
                        Eta = GetLong(t, "eta") > 0 ? (int?)GetLong(t, "eta") : null,
                        DownloadClient = client.Name,
                        DownloadClientId = client.Id,
                        DownloadClientType = client.Type,
                        AddedAt = FromUnix(GetDouble(t, "time_added")),
                        ErrorMessage = GetString(t, "message"),
                        Seeders = (int)GetLong(t, "num_seeds"),
                        Leechers = (int)GetLong(t, "num_peers"),
                        Ratio = GetDouble(t, "ratio"),
                        RemotePath = BuildOutputPath(t),
                        ContentPath = BuildOutputPath(t),
                        CanRemove = true
                    };

                    items.Add(queueItem);
                }
            }
            catch (DownloadClientAdapterPollingException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Failed to retrieve Deluge queue for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
                if (isMonitorPoll)
                {
                    throw new DownloadClientAdapterPollingException("Error polling Deluge queue.", ex);
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

        private static DownloadItemStatus MapStatus(string state, double progress)
        {
            var normalizedState = (state ?? string.Empty).Trim().ToLowerInvariant();
            var payloadComplete = progress >= 100.0;

            return normalizedState switch
            {
                "seeding" or "finished" => DownloadItemStatus.Completed,
                "paused" when payloadComplete => DownloadItemStatus.Completed,
                "paused" => DownloadItemStatus.Paused,
                "queued" when payloadComplete => DownloadItemStatus.Completed,
                "queued" => DownloadItemStatus.Queued,
                "downloading" or "downloading metadata" => DownloadItemStatus.Downloading,
                "error" => DownloadItemStatus.Failed,
                "checking" => DownloadItemStatus.Checking,
                _ => payloadComplete ? DownloadItemStatus.Completed : DownloadItemStatus.Unknown
            };
        }

        private static string BuildOutputPath(JsonElement t)
        {
            var savePath = GetString(t, "save_path").TrimEnd('/', '\\');
            var name = GetString(t, "name");
            if (string.IsNullOrWhiteSpace(savePath))
            {
                return string.Empty;
            }
            return string.IsNullOrWhiteSpace(name) ? savePath : Path.Combine(savePath, name);
        }

        private static string GetString(JsonElement e, string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

        private static long GetLong(JsonElement e, string key) =>
            e.TryGetProperty(key, out var v) && v.TryGetInt64(out var l) ? l : 0;

        private static double GetDouble(JsonElement e, string key) =>
            e.TryGetProperty(key, out var v) && v.TryGetDouble(out var d) ? d : 0;

        private static DateTime FromUnix(double seconds) =>
            seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)seconds).UtcDateTime : DateTime.UtcNow;
    }
}
