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
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Qbittorrent
{
    internal sealed class QbittorrentDownloadPollingWorkflow
    {
        private readonly ILogger _logger;

        public QbittorrentDownloadPollingWorkflow(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<List<Download>> FetchDownloadsAsync(
            DownloadClientConfiguration client,
            List<Download> downloads,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug("Polling qBittorrent client {ClientName}", client.Name);
            try
            {
                var baseUrl = DownloadClientUriBuilder.BuildAuthority(client);
                _logger.LogInformation("Polling qBittorrent client {ClientName} at {BaseUrl}", client.Name, baseUrl);

                using var http = QbittorrentCookieSession.CreateClient();

                using var loginData = QbittorrentCookieSession.CreateLoginContent(client);
                using var loginResp = await http.PostAsync($"{baseUrl}/api/v2/auth/login", loginData, cancellationToken);
                if (!loginResp.IsSuccessStatusCode)
                {
                    var loginError = await loginResp.Content.ReadAsStringAsync(cancellationToken);
                    throw new DownloadClientAdapterPollingException($"qBittorrent login failed for client {client.Name} at {baseUrl} - StatusCode={loginResp.StatusCode}, Response={loginError}");
                }
                _logger.LogDebug("qBittorrent login successful for client {ClientName}", client.Name);

                bool qbtGlobalMaxRatioEnabled = false;
                float qbtGlobalMaxRatio = -1f;
                bool qbtGlobalMaxSeedingTimeEnabled = false;
                long qbtGlobalMaxSeedingTime = -1;
                bool qbtRemoveCompletedDownloads = !string.IsNullOrEmpty(client.RemoveCompletedDownloads) &&
                    client.RemoveCompletedDownloads != "none";
                try
                {
                    using var prefsResp = await http.GetAsync($"{baseUrl}/api/v2/app/preferences", cancellationToken);
                    if (prefsResp.IsSuccessStatusCode)
                    {
                        var prefsJson = await prefsResp.Content.ReadAsStringAsync(cancellationToken);
                        if (!string.IsNullOrWhiteSpace(prefsJson))
                        {
                            var prefs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(prefsJson);
                            if (prefs != null)
                            {
                                qbtGlobalMaxRatioEnabled = prefs.TryGetValue("max_ratio_enabled", out var mre) && mre.GetBoolean();
                                qbtGlobalMaxRatio = prefs.TryGetValue("max_ratio", out var mr) ? (float)mr.GetDouble() : -1f;
                                qbtGlobalMaxSeedingTimeEnabled = prefs.TryGetValue("max_seeding_time_enabled", out var mste) && mste.GetBoolean();
                                qbtGlobalMaxSeedingTime = prefs.TryGetValue("max_seeding_time", out var mst) ? mst.GetInt64() : -1;
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogDebug(ex, "Failed to fetch qBittorrent preferences for seed limit evaluation");
                }

                var fields = "hash,name,save_path,content_path,progress,amount_left,state,size,category,completion_on,seeding_time,ratio,ratio_limit,seeding_time_limit";
                var allTorrents = await FetchTorrentsAsync(http, baseUrl, client, downloads, fields, cancellationToken);
                var torrentLookup = QbittorrentTorrentLookupBuilder.Build(
                    allTorrents,
                    qbtRemoveCompletedDownloads,
                    qbtGlobalMaxRatioEnabled,
                    qbtGlobalMaxRatio,
                    qbtGlobalMaxSeedingTimeEnabled,
                    qbtGlobalMaxSeedingTime);

                _logger.LogDebug("Found {TorrentCount} torrents in qBittorrent for client {ClientName}", torrentLookup.Count, client.Name);

                foreach (var torrent in torrentLookup.Take(10))
                {
                    _logger.LogDebug("qBittorrent torrent: Name={Name}, Hash={Hash}, Progress={Progress:P2}, State={State}, Size={Size}",
                        torrent.Name, torrent.Hash, torrent.Progress, torrent.State, torrent.Size);
                }

                _logger.LogInformation("Checking {DownloadCount} downloads against qBittorrent torrents for client {ClientName}",
                    downloads.Count, client.Name);

                foreach (var download in downloads)
                {
                    try
                    {
                        ReconcileDownload(download, torrentLookup);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Error processing download {DownloadId} while polling qBittorrent", download.Id);
                    }
                }

                return downloads;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                throw new DownloadClientAdapterPollingException($"Error polling qBittorrent client {client.Name}");
            }
        }

        private async Task<List<Dictionary<string, JsonElement>>> FetchTorrentsAsync(
            HttpClient http,
            string baseUrl,
            DownloadClientConfiguration client,
            List<Download> downloads,
            string fields,
            CancellationToken cancellationToken)
        {
            var trackedHashes = downloads
                .Select(download => download.Metadata != null && download.Metadata.TryGetValue("TorrentHash", out var hash) ? hash?.ToString() : null)
                .Where(hash => !string.IsNullOrEmpty(hash))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var allTorrents = new List<Dictionary<string, JsonElement>>();

            if (trackedHashes.Any())
            {
                const int batchSize = 100;
                _logger.LogDebug("Querying qBittorrent for specific hashes (total={Count}), using batches of {BatchSize}", trackedHashes.Count, batchSize);

                var batches = Enumerable.Range(0, (trackedHashes.Count + batchSize - 1) / batchSize)
                    .Select(index => trackedHashes.Skip(index * batchSize).Take(batchSize).ToList())
                    .ToList();

                foreach (var batch in batches)
                {
                    var hashesParam = Uri.EscapeDataString(string.Join("|", batch));
                    var query = $"?hashes={hashesParam}&fields={Uri.EscapeDataString(fields)}";

                    using var torrentsResp = await http.GetAsync($"{baseUrl}/api/v2/torrents/info{query}", cancellationToken);
                    if (!torrentsResp.IsSuccessStatusCode)
                    {
                        var errorContent = await torrentsResp.Content.ReadAsStringAsync(cancellationToken);
                        throw new DownloadClientAdapterPollingException($"Failed to fetch torrent batch from qBittorrent for {client.Name} (batch size={batch.Count}, URL={baseUrl}/api/v2/torrents/info{query}, StatusCode={torrentsResp.StatusCode}, Response={errorContent})");
                    }

                    var json = await torrentsResp.Content.ReadAsStringAsync(cancellationToken);
                    var torrents = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json);
                    if (torrents != null)
                    {
                        allTorrents.AddRange(torrents);
                    }

                    await Task.Delay(150, cancellationToken);
                }

                return allTorrents;
            }

            var configuredCategory = DownloadClientCategoryFilter.GetConfiguredCategory(client);
            if (!string.IsNullOrWhiteSpace(configuredCategory))
            {
                var cat = Uri.EscapeDataString(configuredCategory);
                var query = $"?category={cat}&fields={Uri.EscapeDataString(fields)}";
                _logger.LogDebug("Querying qBittorrent by category: {Category}", configuredCategory);

                using var torrentsResp = await http.GetAsync($"{baseUrl}/api/v2/torrents/info{query}", cancellationToken);
                if (!torrentsResp.IsSuccessStatusCode)
                {
                    throw new DownloadClientAdapterPollingException($"Failed to fetch torrents from qBittorrent for {client.Name}");
                }

                var json = await torrentsResp.Content.ReadAsStringAsync(cancellationToken);
                var torrents = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json);
                return torrents ?? [];
            }

            var defaultQuery = $"?fields={Uri.EscapeDataString(fields)}";
            using var defaultResp = await http.GetAsync($"{baseUrl}/api/v2/torrents/info{defaultQuery}", cancellationToken);
            if (!defaultResp.IsSuccessStatusCode)
            {
                throw new DownloadClientAdapterPollingException($"Failed to fetch torrents from qBittorrent for {client.Name}");
            }

            var defaultJson = await defaultResp.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(defaultJson) ?? [];
        }

        private void ReconcileDownload(
            Download download,
            List<(string Hash, string Name, string SavePath, string ContentPath, double Progress, long AmountLeft, string State, long Size, string Category, long? SeedingTime, double Ratio, float RatioLimit, long SeedingTimeLimit, bool CanMoveFiles, bool CanBeRemoved)> torrentLookup)
        {
            _logger.LogDebug("Looking for qBittorrent match for download {DownloadId}: {Title}", download.Id, download.Title);

            var matched = FindMatchingTorrent(download, torrentLookup);
            if (string.IsNullOrEmpty(matched.Hash))
            {
                _logger.LogWarning("No matching qBittorrent torrent found for download {DownloadId}: {Title}", download.Id, download.Title);
                return;
            }

            _logger.LogDebug("Found matching qBittorrent torrent for {DownloadId}: {TorrentName} (Hash: {Hash}, State: {State}, Progress: {Progress:P2}, SavePath: {SavePath}, ContentPath: {ContentPath})",
                download.Id, matched.Name, matched.Hash, matched.State, matched.Progress, matched.SavePath, matched.ContentPath);

            _logger.LogInformation("Completion diagnostic for {DownloadId}: Progress={Progress:F4} (>= 1.0? {ProgressCheck}), AmountLeft={AmountLeft} (== 0? {AmountCheck}), State={State}",
                download.Id, matched.Progress, matched.Progress >= 1.0, matched.AmountLeft, matched.AmountLeft == 0, matched.State);

            if (!string.IsNullOrEmpty(matched.SavePath) && download.DownloadPath != matched.SavePath)
            {
                download.DownloadPath = matched.SavePath;
            }

            download.Metadata ??= new Dictionary<string, object>();

            if (!string.IsNullOrEmpty(matched.ContentPath))
            {
                download.Metadata["ClientContentPath"] = matched.ContentPath;
            }

            if (matched.SeedingTime.HasValue)
            {
                download.Metadata["SeedingTimeSeconds"] = matched.SeedingTime.Value;
            }

            download.Metadata["CanMoveFiles"] = matched.CanMoveFiles;
            download.Metadata["CanBeRemoved"] = matched.CanBeRemoved;

            AdapterUtils.MapDownloadProgress(download, matched.Progress * 100, matched.AmountLeft, matched.State);

            if (download.Status == DownloadStatus.Moved ||
                download.Status == DownloadStatus.Processing ||
                download.Status == DownloadStatus.ImportPending)
            {
                _logger.LogDebug("Skipping finalization for {Status} download {DownloadId}", download.Status, download.Id);
                return;
            }

            var normalizedState = (matched.State ?? string.Empty).ToLowerInvariant();
            if (normalizedState == "error" || normalizedState == "missingfiles")
            {
                download.Failed($"qBittorrent state: {matched.State}");
                return;
            }

            var isComplete = matched.Progress >= 1.0 || matched.AmountLeft == 0;

            _logger.LogDebug("Completion check for {DownloadId}: IsComplete={IsComplete}, Progress={Progress:P2}, AmountLeft={AmountLeft}, State={State}",
                download.Id, isComplete, matched.Progress, matched.AmountLeft, matched.State);

            if (isComplete)
            {
                var completionPath = !string.IsNullOrEmpty(matched.ContentPath)
                    ? matched.ContentPath
                    : (!string.IsNullOrEmpty(matched.SavePath) && !string.IsNullOrEmpty(matched.Name)
                        ? FileUtils.CombineWithOptionalBase(matched.SavePath, matched.Name)
                        : matched.SavePath);

                _logger.LogInformation("Download {DownloadId} observed as complete candidate (qBittorrent). Torrent: {TorrentName}, Path: {Path}. Waiting for stability window.",
                    download.Id, matched.Name, completionPath);

                download.Completed();
            }
        }

        private (string Hash, string Name, string SavePath, string ContentPath, double Progress, long AmountLeft, string State, long Size, string Category, long? SeedingTime, double Ratio, float RatioLimit, long SeedingTimeLimit, bool CanMoveFiles, bool CanBeRemoved) FindMatchingTorrent(
            Download download,
            List<(string Hash, string Name, string SavePath, string ContentPath, double Progress, long AmountLeft, string State, long Size, string Category, long? SeedingTime, double Ratio, float RatioLimit, long SeedingTimeLimit, bool CanMoveFiles, bool CanBeRemoved)> torrentLookup)
        {
            var matched = (Hash: "", Name: "", SavePath: "", ContentPath: "", Progress: 0.0, AmountLeft: 0L, State: "", Size: 0L, Category: "", SeedingTime: (long?)null, Ratio: 0.0, RatioLimit: -2f, SeedingTimeLimit: -2L, CanMoveFiles: false, CanBeRemoved: false);

            if (download.Metadata != null && download.Metadata.TryGetValue("TorrentHash", out var hashObj))
            {
                var storedHash = hashObj?.ToString();
                if (!string.IsNullOrEmpty(storedHash))
                {
                    matched = torrentLookup.FirstOrDefault(t =>
                        string.Equals(t.Hash, storedHash, StringComparison.OrdinalIgnoreCase));

                    if (!string.IsNullOrEmpty(matched.Hash))
                    {
                        _logger.LogDebug("Found qBittorrent torrent by hash match: {Hash} for download {DownloadId}", storedHash, download.Id);
                    }
                }
            }

            if (!string.IsNullOrEmpty(matched.Hash))
            {
                return matched;
            }

            _logger.LogInformation("Hash matching failed for download {DownloadId}, trying exact name/path matching", download.Id);

            matched = torrentLookup.FirstOrDefault(t =>
                string.Equals(t.Name, download.Title, StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrEmpty(matched.Hash))
            {
                var downloadNormalized = TitleUtils.NormalizeTitle(download.Title);
                matched = torrentLookup.FirstOrDefault(t =>
                    string.Equals(TitleUtils.NormalizeTitle(t.Name), downloadNormalized, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrEmpty(matched.Hash))
                {
                    _logger.LogInformation("Normalized title match: '{DbTitle}' <-> '{TorrentTitle}'", download.Title, matched.Name);
                }
            }

            if (string.IsNullOrEmpty(matched.Hash) && !string.IsNullOrEmpty(download.DownloadPath))
            {
                var downloadPathNormalized = Path.GetFullPath(download.DownloadPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                matched = torrentLookup.FirstOrDefault(t =>
                {
                    if (string.IsNullOrEmpty(t.ContentPath)) return false;
                    var contentNormalized = Path.GetFullPath(t.ContentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    return string.Equals(downloadPathNormalized, contentNormalized, StringComparison.OrdinalIgnoreCase);
                });
            }

            return matched;
        }
    }
}
