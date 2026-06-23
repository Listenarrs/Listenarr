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
    internal sealed class DelugeItemFetchWorkflow(
        DelugeRpcClient rpcClient,
        ILogger<DelugeAdapter> logger)
    {
        private static readonly string[] StatusKeys =
        [
            "name", "total_size", "total_done", "progress", "download_payload_rate", "eta", "state",
            "save_path", "label", "ratio", "num_seeds", "num_peers", "time_added", "files", "message"
        ];

        public async Task<List<DownloadClientItem>> GetItemsAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var list = new List<DownloadClientItem>();
            if (client == null)
            {
                return list;
            }

            try
            {
                var res = await rpcClient.InvokeAsync(client, "web.update_ui", [StatusKeys, new Dictionary<string, object>()], ct);
                var configuredCategory = DownloadClientCategoryFilter.GetConfiguredCategory(client);
                if (!res.TryGetProperty("torrents", out var torrents) || torrents.ValueKind != JsonValueKind.Object)
                {
                    return list;
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
                    list.Add(new DownloadClientItem
                    {
                        DownloadId = torrent.Name,
                        Title = GetString(t, "name"),
                        Category = label,
                        TotalSize = total,
                        RemainingSize = Math.Max(0, total - done),
                        OutputPath = BuildOutputPath(t),
                        Status = MapStatus(GetString(t, "state"), GetDouble(t, "progress")),
                        Message = GetString(t, "message"),
                        Progress = GetDouble(t, "progress"),
                        DownloadSpeed = GetDouble(t, "download_payload_rate"),
                        SeedRatio = GetDouble(t, "ratio"),
                        Seeders = (int)GetLong(t, "num_seeds"),
                        Leechers = (int)GetLong(t, "num_peers"),
                        AddedAt = FromUnix(GetDouble(t, "time_added")),
                        CanBeRemoved = true,
                        CanMoveFiles = false,
                        DownloadClientInfo = DownloadClientItemClientInfo.FromClient(
                            client.Id,
                            client.Name,
                            client.Type,
                            DownloadProtocol.Torrent,
                            client.RemoveCompletedDownloads != "none",
                            false)
                    });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Failed to retrieve Deluge items for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
            }

            return list;
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
