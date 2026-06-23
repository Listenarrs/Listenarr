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
    internal sealed class DelugeImportItemResolver(
        DelugeRpcClient rpcClient,
        ILogger logger)
    {
        private static readonly string[] StatusKeys =
        [
            "name", "total_size", "total_done", "progress", "download_payload_rate", "eta", "state",
            "save_path", "label", "ratio", "num_seeds", "num_peers", "time_added", "files", "message"
        ];

        public async Task<DownloadClientItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            DownloadClientItem item,
            CancellationToken ct = default)
        {
            var torrent = await TryGetTorrentAsync(client, item.DownloadId, ct);
            if (torrent == null)
            {
                return item;
            }

            var result = item.Clone();
            var savePath = torrent.Value.TryGetProperty("save_path", out var savePathProp) ? savePathProp.GetString() : null;
            var name = torrent.Value.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;

            if (!string.IsNullOrEmpty(savePath) && !string.IsNullOrEmpty(name))
            {
                result.OutputPath = Path.Combine(savePath, name);
            }

            return result;
        }

        public async Task<QueueItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            Download download,
            QueueItem queueItem,
            CancellationToken ct = default)
        {
            var result = queueItem.Clone();
            var externalId = download.GetExternalId();
            var hash = string.IsNullOrWhiteSpace(externalId) ? queueItem.Id : externalId;

            if (string.IsNullOrEmpty(hash))
            {
                return result;
            }

            var torrent = await TryGetTorrentAsync(client, hash, ct);
            if (torrent == null)
            {
                return result;
            }

            result.Id = hash;

            var savePath = torrent.Value.TryGetProperty("save_path", out var savePathProp) ? savePathProp.GetString() : null;
            var name = torrent.Value.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            var state = torrent.Value.TryGetProperty("state", out var stateProp) ? stateProp.GetString() : null;
            var progress = torrent.Value.TryGetProperty("progress", out var progressProp) && progressProp.TryGetDouble(out var d) ? d : 0.0;

            if (!string.IsNullOrEmpty(savePath) && !string.IsNullOrEmpty(name))
            {
                result.ContentPath = Path.Combine(savePath, name);
            }

            if (!string.IsNullOrEmpty(name))
            {
                result.Title = name;
            }

            if (!string.IsNullOrEmpty(state))
            {
                result.Status = MapStatus(state, progress).ToString().ToLowerInvariant();
            }

            if (!string.IsNullOrEmpty(savePath) && torrent.Value.TryGetProperty("files", out var filesProp) && filesProp.ValueKind == JsonValueKind.Array)
            {
                var filesList = new List<string>();
                foreach (var file in filesProp.EnumerateArray())
                {
                    if (file.TryGetProperty("path", out var pathProp) && pathProp.ValueKind == JsonValueKind.String)
                    {
                        var relPath = pathProp.GetString();
                        if (!string.IsNullOrWhiteSpace(relPath))
                        {
                            filesList.Add(Path.Combine(savePath, relPath));
                        }
                    }
                }
                result.SourceFiles = filesList;
            }

            return result;
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

        private async Task<JsonElement?> TryGetTorrentAsync(
            DownloadClientConfiguration client,
            string torrentId,
            CancellationToken ct)
        {
            try
            {
                var res = await rpcClient.InvokeAsync(client, "web.update_ui", [StatusKeys, new Dictionary<string, object>()], ct);
                if (res.TryGetProperty("torrents", out var torrents) && torrents.ValueKind == JsonValueKind.Object)
                {
                    if (torrents.TryGetProperty(torrentId, out var torrent))
                    {
                        return torrent.Clone();
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Error resolving import item for Deluge torrent {TorrentId}", torrentId);
            }

            return null;
        }
    }
}
