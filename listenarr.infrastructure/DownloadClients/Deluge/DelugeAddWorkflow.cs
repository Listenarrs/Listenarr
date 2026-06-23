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
using System.Text.RegularExpressions;
using Listenarr.Infrastructure.Torrents;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Deluge
{
    internal sealed class DelugeAddWorkflow(
        DelugeRpcClient rpcClient,
        ILogger logger)
    {
        public async Task<DownloadClientSubmissionResult> AddAsync(
            DownloadClientConfiguration client,
            PreparedDownloadSubmission submission,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(client);
            if (submission is not PreparedTorrentSubmission torrent)
            {
                throw new DownloadClientSubmissionException("Deluge requires a prepared torrent submission.");
            }

            var options = BuildTorrentOptions(client);
            string? id = null;

            if (torrent.TorrentBytes != null && torrent.TorrentBytes.Length > 0)
            {
                var filename = SanitizeTorrentFileName(torrent.FileName ?? torrent.Title) + ".torrent";
                var res = await rpcClient.InvokeAsync(client, "core.add_torrent_file", [filename, Convert.ToBase64String(torrent.TorrentBytes), options], ct);
                id = res.ValueKind == JsonValueKind.String ? res.GetString() : null;
            }
            else
            {
                var uri = !string.IsNullOrWhiteSpace(torrent.MagnetUri)
                    ? DownloadClientUriBuilder.NormalizeMagnetLink(torrent.MagnetUri)
                    : NormalizeTorrentUrl(torrent.OriginalLocator);
                if (string.IsNullOrWhiteSpace(uri))
                {
                    throw new DownloadClientSubmissionException("No magnet link, torrent URL, or cached torrent file provided.");
                }

                if (uri.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    var res = await rpcClient.InvokeAsync(client, "core.add_torrent_magnet", [uri, options], ct);
                    id = res.ValueKind == JsonValueKind.String ? res.GetString() : TryExtractHashFromMagnet(uri);
                }
                else
                {
                    var tempPath = await rpcClient.InvokeAsync(client, "web.download_torrent_from_url", [uri], ct);
                    var path = tempPath.GetString();
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        var res = await rpcClient.InvokeAsync(client, "web.add_torrents", [new[] { new { path, options } }], ct);
                        id = TryExtractAddedId(res);
                    }
                }
            }

            var category = GetSetting(client, "category");
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(category))
            {
                await TrySetLabelAsync(client, id, category, ct);
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                throw new DownloadClientSubmissionException("Deluge did not return a verified torrent identifier.");
            }

            return new DownloadClientSubmissionResult(id, torrent.InfoHash);
        }

        private static Dictionary<string, object> BuildTorrentOptions(DownloadClientConfiguration client)
        {
            var options = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(client.DownloadPath))
            {
                options["download_location"] = client.DownloadPath;
            }
            var addPaused = GetSetting(client, "addPaused") ?? GetSetting(client, "AddPaused");
            if (bool.TryParse(addPaused, out var paused))
            {
                options["add_paused"] = paused;
            }
            return options;
        }

        private async Task TrySetLabelAsync(
            DownloadClientConfiguration client,
            string id,
            string label,
            CancellationToken ct)
        {
            try
            {
                await rpcClient.InvokeAsync(client, "label.set_torrent", [id, label], ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogDebug(ex, "Unable to set Deluge label/category. Is the Label plugin enabled?");
            }
        }

        private static string? GetSetting(DownloadClientConfiguration c, string key) =>
            c.Settings != null && c.Settings.TryGetValue(key, out var v) ? v?.ToString() : null;

        private static string SanitizeTorrentFileName(string? title) =>
            string.Join("_", (title ?? "listenarr").Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();

        private static string? NormalizeTorrentUrl(string? url) =>
            string.IsNullOrWhiteSpace(url) ? null : url.Trim();

        private static string? TryExtractHashFromMagnet(string magnet)
        {
            var m = Regex.Match(magnet, @"btih:([A-Fa-f0-9]{40})");
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
        }

        private static string? TryExtractAddedId(JsonElement res) =>
            res.ValueKind == JsonValueKind.Array && res.GetArrayLength() > 0 ? res[0].GetString() : null;
    }
}
