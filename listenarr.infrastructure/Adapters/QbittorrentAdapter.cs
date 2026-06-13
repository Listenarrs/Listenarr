/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using System.Net;
using System.Text.Json;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Security;
using Listenarr.Domain.Common;
using Listenarr.Domain.Models;
using Listenarr.Domain.Models.Exceptions;
using Listenarr.Infrastructure.Adapters.Exceptions;
using Listenarr.Infrastructure.Torrents;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Adapters
{
    /// <summary>
    /// qBittorrent protocol implementation.
    /// </summary>
    public class QbittorrentAdapter : IDownloadClientAdapter
    {
        public string ClientId => "qbittorrent";
        public string ClientType => "qbittorrent";
        public DownloadProtocol Protocol => DownloadProtocol.Torrent;

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<QbittorrentAdapter> _logger;
        private readonly ITorrentFileDownloader _torrentFileDownloader;
        private readonly QbittorrentTorrentAddPlanner _torrentAddPlanner;
        private readonly QbittorrentAuthSession _authSession;
        private readonly QbittorrentConnectionTester _connectionTester;

        public QbittorrentAdapter(IHttpClientFactory httpFactory, ITorrentFileDownloader torrentFileDownloader, ILogger<QbittorrentAdapter> logger)
        {
            _httpClientFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
            _torrentFileDownloader = torrentFileDownloader ?? throw new ArgumentNullException(nameof(torrentFileDownloader));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _torrentAddPlanner = new QbittorrentTorrentAddPlanner(_torrentFileDownloader, _logger);
            _authSession = new QbittorrentAuthSession(_logger);
            _connectionTester = new QbittorrentConnectionTester(_httpClientFactory, _logger, ClientType);
        }

        public async Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            return await _connectionTester.TestConnectionAsync(client, ct);
        }

        public async Task<string?> AddAsync(DownloadClientConfiguration client, SearchResult result, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(result);

            var baseUrl = DownloadClientUriBuilder.BuildAuthority(client);
            using var httpClient = _httpClientFactory.CreateClient(ClientType);

            try
            {
                await _authSession.LoginAsync(httpClient, client, ct);
            }
            catch (QbittorrentException exception)
            {
                _logger.LogError(exception.Message);
                return null;
            }

            var addPlan = await _torrentAddPlanner.CreateAsync(client, result, ct);
            if (addPlan == null)
            {
                return null;
            }

            // Add download using torrent file
            HttpResponseMessage addResponse;
            if (addPlan.TorrentFileData != null)
            {
                using var multipart = new MultipartFormDataContent();
                multipart.Add(new StringContent(addPlan.SavePath), "savepath");
                if (!string.IsNullOrEmpty(addPlan.Category))
                    multipart.Add(new StringContent(addPlan.Category), "category");
                if (!string.IsNullOrEmpty(addPlan.Tags))
                    multipart.Add(new StringContent(addPlan.Tags), "tags");

                var torrentFileName = string.IsNullOrEmpty(result.TorrentFileName) ? "download.torrent" : result.TorrentFileName;
                var torrentContent = new ByteArrayContent(addPlan.TorrentFileData);
                torrentContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-bittorrent");
                multipart.Add(torrentContent, "torrents", torrentFileName);

                addResponse = await httpClient.PostAsync($"{baseUrl}/api/v2/torrents/add", multipart, ct);
            }
            // Add using magnet link or torrent url
            else
            {
                var url = new[] { addPlan.MagnetLink, addPlan.HttpTorrentUrl }
                    .FirstOrDefault(static url => !string.IsNullOrEmpty(url)) ?? string.Empty;

                var formData = new List<KeyValuePair<string, string>>
                {
                    new("urls", url),
                    new("savepath", addPlan.SavePath)
                };

                if (!string.IsNullOrEmpty(addPlan.Category))
                    formData.Add(new("category", addPlan.Category));
                if (!string.IsNullOrEmpty(addPlan.Tags))
                    formData.Add(new("tags", addPlan.Tags));

                using var addData = new FormUrlEncodedContent(formData);
                addResponse = await httpClient.PostAsync($"{baseUrl}/api/v2/torrents/add", addData, ct);
            }

            if (!addResponse.IsSuccessStatusCode)
            {
                var responseContent = await addResponse.Content.ReadAsStringAsync(ct);
                var redacted = LogRedaction.RedactText(responseContent, LogRedaction.GetSensitiveValuesFromEnvironment().Concat([client.Password ?? string.Empty]));

                _logger.LogError($"Failed to add torrent to qBittorrent. Status: {addResponse.StatusCode}, Response: {redacted}");
                return null;
            }

            _logger.LogInformation("Successfully sent torrent to qBittorrent");

            await Task.Delay(1000, ct);

            // Inject tracker URLs via addTrackers API as a fallback to ensure the tracker
            // is registered even if qBittorrent didn't parse it from the torrent file.
            if (addPlan.TorrentFileData != null)
            {
                try
                {
                    var announces = MyAnonamouseHelper.ExtractAnnounceUrls(addPlan.TorrentFileData);
                    // Filter to only actual tracker announce URLs — exclude file/web-seed URLs
                    var trackerAnnounces = announces?.Where(a =>
                        a.Contains("/announce", StringComparison.OrdinalIgnoreCase) ||
                        a.Contains("/tracker", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (trackerAnnounces != null && trackerAnnounces.Count > 0)
                    {
                        var trackerUrls = string.Join("\n", trackerAnnounces.Distinct());
                        using var addTrackersData = new FormUrlEncodedContent(new[]
                        {
                            new KeyValuePair<string, string>("hash", addPlan.Hash),
                            new KeyValuePair<string, string>("urls", trackerUrls)
                        });
                        using var trackersResp = await httpClient.PostAsync($"{baseUrl}/api/v2/torrents/addTrackers", addTrackersData, ct);
                        if (trackersResp.IsSuccessStatusCode)
                            _logger.LogInformation($"Injected {trackerAnnounces.Count} tracker(s) for torrent {addPlan.Hash} via addTrackers API");
                        else
                            _logger.LogDebug($"addTrackers API returned {trackersResp.StatusCode} for torrent {addPlan.Hash} (non-fatal)");
                    }
                }
                catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                {
                    _logger.LogDebug(exception, "Non-fatal failure injecting trackers via addTrackers API");
                }
            }

            return addPlan.Hash;
        }

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
                _logger.LogDebug("No postImportCategory configured for qBittorrent client {ClientId}, skipping MarkItemAsImported", client.Id);
                return true; // No-op is success
            }

            var baseUrl = DownloadClientUriBuilder.BuildAuthority(client);
            try
            {
                var cookieJar = new CookieContainer();
                var handler = new HttpClientHandler { CookieContainer = cookieJar, UseCookies = true, AutomaticDecompression = DecompressionMethods.All };
                using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

                // Authenticate
                using var loginData = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("username", client.Username ?? string.Empty),
                    new KeyValuePair<string, string>("password", client.Password ?? string.Empty)
                });
                using (await httpClient.PostAsync($"{baseUrl}/api/v2/auth/login", loginData, ct)) { }

                // Set category
                using var setCategoryData = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("hashes", downloadId.ToLowerInvariant()),
                    new KeyValuePair<string, string>("category", postImportCategory)
                });

                using var resp = await httpClient.PostAsync($"{baseUrl}/api/v2/torrents/setCategory", setCategoryData, ct);
                if (resp.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Marked torrent {Hash} as imported (category: {Category}) in qBittorrent", downloadId, postImportCategory);
                    return true;
                }

                _logger.LogWarning("Failed to mark torrent {Hash} as imported in qBittorrent: {StatusCode}", downloadId, resp.StatusCode);
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Error marking torrent {Hash} as imported in qBittorrent", downloadId);
                return false;
            }
        }

        public async Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(client);
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            var baseUrl = DownloadClientUriBuilder.BuildAuthority(client);

            try
            {
                var cookieJar = new CookieContainer();
                var handler = new HttpClientHandler { CookieContainer = cookieJar, UseCookies = true, AutomaticDecompression = DecompressionMethods.All };

                using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

                using var loginData = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("username", client.Username ?? string.Empty),
                    new KeyValuePair<string, string>("password", client.Password ?? string.Empty)
                });

                using var loginResp = await httpClient.PostAsync($"{baseUrl}/api/v2/auth/login", loginData, ct);
                if (!loginResp.IsSuccessStatusCode)
                {
                    if (loginResp.StatusCode == HttpStatusCode.Forbidden)
                    {
                        // 403 may mean auth is disabled — probe a version endpoint to confirm
                        using var testResp = await httpClient.GetAsync($"{baseUrl}/api/v2/app/version", ct);
                        if (!testResp.IsSuccessStatusCode)
                        {
                            _logger.LogWarning("qBittorrent auth appears enabled and credentials are invalid for client {ClientId}", client.Id);
                            return false;
                        }
                        // Auth is disabled; fall through to the delete call
                    }
                    else
                    {
                        _logger.LogWarning("qBittorrent login failed with status {Status} for client {ClientId}", loginResp.StatusCode, client.Id);
                        return false;
                    }
                }

                using var deleteData = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("hashes", id),
                    new KeyValuePair<string, string>("deleteFiles", deleteFiles ? "true" : "false")
                });

                using var deleteResp = await httpClient.PostAsync($"{baseUrl}/api/v2/torrents/delete", deleteData, ct);
                if (!deleteResp.IsSuccessStatusCode)
                {
                    var body = await deleteResp.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("qBittorrent delete returned {Status}: {Body}", deleteResp.StatusCode, LogRedaction.RedactText(body, LogRedaction.GetSensitiveValuesFromEnvironment()));
                    return false;
                }

                _logger.LogInformation("Removed torrent {Id} from qBittorrent (deleteFiles={DeleteFiles})", LogRedaction.SanitizeText(id), deleteFiles);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error removing torrent from qBittorrent: {Id}", LogRedaction.SanitizeText(id));
                return false;
            }
        }

        public async Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var items = new List<QueueItem>();
            if (client == null) return items;

            var baseUrl = DownloadClientUriBuilder.BuildAuthority(client);

            try
            {
                var cookieJar = new CookieContainer();
                var handler = new HttpClientHandler
                {
                    CookieContainer = cookieJar,
                    UseCookies = true,
                    AutomaticDecompression = DecompressionMethods.All
                };

                using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

                using var loginData = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("username", client.Username ?? string.Empty),
                    new KeyValuePair<string, string>("password", client.Password ?? string.Empty)
                });

                using var loginResp = await httpClient.PostAsync($"{baseUrl}/api/v2/auth/login", loginData, ct);
                if (loginResp.StatusCode == HttpStatusCode.Forbidden)
                {
                    using var testResp = await httpClient.GetAsync($"{baseUrl}/api/v2/app/version", ct);
                    if (!testResp.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("qBittorrent authentication appears to be enabled and credentials are invalid for client {ClientId}", LogRedaction.SanitizeText(client.Id));
                        return items;
                    }
                }
                else if (!loginResp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("qBittorrent login failed with status {Status} for client {ClientId}", loginResp.StatusCode, LogRedaction.SanitizeText(client.Id));
                    return items;
                }

                // Limit fields returned to reduce memory usage
                var fields = "name,progress,size,downloaded,dlspeed,eta,state,hash,added_on,num_seeds,num_leechs,ratio,save_path";

                // Build category filter parameter if configured
                var categoryFilter = QBittorrentHelpers.BuildCategoryParameter(client.Settings, "&");

                // Extract category for logging
                var category = client.Settings?.TryGetValue("category", out var categoryObj) is true
                    ? categoryObj?.ToString()
                    : null;
                QBittorrentHelpers.LogCategoryFiltering(_logger, category);

                using var torrentsResp = await httpClient.GetAsync($"{baseUrl}/api/v2/torrents/info?fields={Uri.EscapeDataString(fields)}{categoryFilter}", ct);
                if (!torrentsResp.IsSuccessStatusCode) return items;

                var json = await torrentsResp.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(json)) return items;

                var torrents = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json);
                if (torrents == null) return items;

                foreach (var torrent in torrents)
                {
                    var hash = torrent.TryGetValue("hash", out var hashEl) ? hashEl.GetString() ?? string.Empty : string.Empty;

                    List<Dictionary<string, JsonElement>> files = [];
                    using var filesResp = await httpClient.GetAsync($"{baseUrl}/api/v2/torrents/files?hash={hash}", ct);
                    if (filesResp.IsSuccessStatusCode)
                    {
                        var filesJson = await filesResp.Content.ReadAsStringAsync(ct);
                        files = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(filesJson) ?? [];
                    }

                    items.Add(QbittorrentResponseMapper.MapQueueItem(torrent, client, files));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Error getting qBittorrent queue - client may be unreachable");
            }

            return items;
        }

        public Task<List<(string Id, string Name)>> GetRecentHistoryAsync(DownloadClientConfiguration client, int limit = 100, CancellationToken ct = default)
        {
            return Task.FromResult(new List<(string Id, string Name)>());
        }

        /// <summary>
        /// Get all downloads as standardized DownloadClientItem objects
        /// </summary>
        public async Task<List<DownloadClientItem>> GetItemsAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var items = new List<DownloadClientItem>();
            if (client == null) return items;

            var baseUrl = DownloadClientUriBuilder.BuildAuthority(client);
            var categoryFilter = QBittorrentHelpers.BuildCategoryParameter(client.Settings, "&");

            try
            {
                var cookieJar = new CookieContainer();
                var handler = new HttpClientHandler
                {
                    CookieContainer = cookieJar,
                    UseCookies = true,
                    AutomaticDecompression = DecompressionMethods.All
                };

                using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

                using var loginData = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("username", client.Username ?? string.Empty),
                    new KeyValuePair<string, string>("password", client.Password ?? string.Empty)
                });

                using var loginResp = await httpClient.PostAsync($"{baseUrl}/api/v2/auth/login", loginData, ct);
                if (loginResp.StatusCode == HttpStatusCode.Forbidden)
                {
                    using var testResp = await httpClient.GetAsync($"{baseUrl}/api/v2/app/version", ct);
                    if (!testResp.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("qBittorrent authentication appears to be enabled and credentials are invalid for client {ClientId}", LogRedaction.SanitizeText(client.Id));
                        return items;
                    }
                }
                else if (!loginResp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("qBittorrent login failed with status {Status} for client {ClientId}", loginResp.StatusCode, LogRedaction.SanitizeText(client.Id));
                    return items;
                }

                // Fetch qBittorrent global preferences for seed limit evaluation (Sonarr parity)
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
                    _logger.LogDebug(ex, "Failed to fetch qBittorrent preferences for seed limit evaluation, will use conservative defaults");
                }

                // Resolve removeCompletedDownloads setting once for all torrents
                var removeCompletedDownloads = !string.IsNullOrEmpty(client.RemoveCompletedDownloads) &&
                    client.RemoveCompletedDownloads != "none";

                // Limit fields returned to reduce memory usage
                var fields = "name,progress,size,downloaded,dlspeed,eta,state,hash,added_on,num_seeds,num_leechs,ratio,save_path,category,content_path,ratio_limit,seeding_time_limit,seeding_time";
                using var torrentsResp = await httpClient.GetAsync($"{baseUrl}/api/v2/torrents/info?fields={Uri.EscapeDataString(fields)}{categoryFilter}", ct);
                if (!torrentsResp.IsSuccessStatusCode) return items;

                var json = await torrentsResp.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(json)) return items;

                var torrents = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json);
                if (torrents == null) return items;

                foreach (var torrent in torrents)
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
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Error getting qBittorrent items - client may be unreachable");
            }

            return items;
        }

        /// <summary>
        /// Get import item from DownloadClientItem
        /// </summary>
        public async Task<DownloadClientItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            DownloadClientItem item,
            DownloadClientItem? previousAttempt = null,
            CancellationToken ct = default)
        {
            // Clone to avoid modifying original
            var result = item.Clone();

            // If OutputPath is already set, use it directly
            if (!string.IsNullOrEmpty(result.OutputPath))
            {
                _logger.LogDebug("Using existing OutputPath for import: {Path}", result.OutputPath);
                return result;
            }

            // Otherwise, resolve path from qBittorrent API
            var hash = result.DownloadId.ToLowerInvariant();
            var baseUrl = DownloadClientUriBuilder.BuildAuthority(client);

            try
            {
                var cookieJar = new CookieContainer();
                var handler = new HttpClientHandler
                {
                    CookieContainer = cookieJar,
                    UseCookies = true,
                    AutomaticDecompression = DecompressionMethods.All
                };

                using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

                // Login
                using var loginData = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("username", client.Username ?? string.Empty),
                    new KeyValuePair<string, string>("password", client.Password ?? string.Empty)
                });

                using var loginResp = await httpClient.PostAsync($"{baseUrl}/api/v2/auth/login", loginData, ct);
                if (!loginResp.IsSuccessStatusCode && loginResp.StatusCode != HttpStatusCode.Forbidden)
                {
                    _logger.LogWarning("qBittorrent login failed for import resolution");
                    return result;
                }

                // Query files API to determine base folder
                using var filesResp = await httpClient.GetAsync($"{baseUrl}/api/v2/torrents/files?hash={hash}", ct);
                if (!filesResp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to query torrent files for hash {Hash}", hash);
                    return result;
                }

                var filesJson = await filesResp.Content.ReadAsStringAsync(ct);
                var files = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(filesJson);

                if (files == null || !files.Any())
                {
                    _logger.LogDebug("No files found for torrent {Hash}", hash);
                    return result;
                }

                // Get torrent properties to find save_path
                using var propsResp = await httpClient.GetAsync($"{baseUrl}/api/v2/torrents/properties?hash={hash}", ct);
                if (!propsResp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to query torrent properties for hash {Hash}", hash);
                    return result;
                }

                var propsJson = await propsResp.Content.ReadAsStringAsync(ct);
                var props = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(propsJson);
                var savePath = props?.TryGetValue("save_path", out var savePathEl) is true
                    ? savePathEl.GetString() ?? string.Empty
                    : string.Empty;

                if (string.IsNullOrEmpty(savePath))
                {
                    _logger.LogWarning("No save_path found for torrent {Hash}", hash);
                    return result;
                }

                var outputPath = QbittorrentImportPathResolver.ResolveContentPath(savePath, files);
                if (string.IsNullOrEmpty(outputPath))
                {
                    _logger.LogWarning("Unable to resolve content path from torrent files for hash {Hash}", hash);
                    return result;
                }

                // Apply remote path mapping
                result.OutputPath = outputPath;

                _logger.LogInformation("Resolved import path for {Hash}: {Path}", hash, result.OutputPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error resolving import item for torrent {Hash}", hash);
            }

            return result;
        }

        /// <summary>
        /// LEGACY: Resolves the actual import item for a completed download.
        /// Matches GetImportItem pattern.
        /// </summary>
        public async Task<QueueItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            Download download,
            QueueItem queueItem,
            QueueItem? previousAttempt = null,
            CancellationToken ct = default)
        {
            // ✅ Clone to avoid modifying original
            var result = queueItem.Clone();
            string? resolvedExistingContentPath = null;

            // On API >= 2.6.1, ContentPath/OutputPath is already set correctly from content_path field
            if (!string.IsNullOrEmpty(result.ContentPath))
            {
                var localPath = result.ContentPath;
                if (!string.IsNullOrWhiteSpace(localPath))
                {
                    result.ContentPath = localPath;
                    resolvedExistingContentPath = localPath;
                }

                _logger.LogDebug("Using existing ContentPath for import: {Path}", result.ContentPath);
            }

            var hash = download.Metadata?.GetValueOrDefault("TorrentHash")?.ToString();
            if (string.IsNullOrWhiteSpace(hash))
            {
                hash = queueItem.Id;
            }
            if (string.IsNullOrEmpty(hash))
            {
                _logger.LogWarning("No torrent hash found in download metadata for download {DownloadId}", download.Id);
                return result;
            }

            var baseUrl = DownloadClientUriBuilder.BuildAuthority(client);

            try
            {
                var cookieJar = new CookieContainer();
                var handler = new HttpClientHandler
                {
                    CookieContainer = cookieJar,
                    UseCookies = true,
                    AutomaticDecompression = DecompressionMethods.All
                };

                using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

                // Login
                using var loginData = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("username", client.Username ?? string.Empty),
                    new KeyValuePair<string, string>("password", client.Password ?? string.Empty)
                });

                using var loginResp = await httpClient.PostAsync($"{baseUrl}/api/v2/auth/login", loginData, ct);
                if (!loginResp.IsSuccessStatusCode && loginResp.StatusCode != HttpStatusCode.Forbidden)
                {
                    _logger.LogWarning("qBittorrent login failed for import resolution");
                    return result;
                }

                // ✅ Query files API to determine base folder
                using var filesResp = await httpClient.GetAsync($"{baseUrl}/api/v2/torrents/files?hash={hash}", ct);
                if (!filesResp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to query torrent files for hash {Hash}", hash);
                    return result;
                }

                var filesJson = await filesResp.Content.ReadAsStringAsync(ct);
                var files = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(filesJson);

                if (files == null || !files.Any())
                {
                    _logger.LogDebug("No files found for torrent {Hash}", hash);
                    return result;
                }

                // Get torrent properties to find save_path
                using var propsResp = await httpClient.GetAsync($"{baseUrl}/api/v2/torrents/properties?hash={hash}", ct);
                if (!propsResp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to query torrent properties for hash {Hash}", hash);
                    return result;
                }

                var propsJson = await propsResp.Content.ReadAsStringAsync(ct);
                var props = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(propsJson);
                var savePath = props?.TryGetValue("save_path", out var savePathEl) is true
                    ? savePathEl.GetString() ?? string.Empty
                    : string.Empty;

                if (string.IsNullOrEmpty(savePath))
                {
                    _logger.LogWarning("No save_path found for torrent {Hash}", hash);
                    return result;
                }

                var outputPath = QbittorrentImportPathResolver.ResolveContentPath(savePath, files);
                if (string.IsNullOrEmpty(outputPath) && string.IsNullOrWhiteSpace(resolvedExistingContentPath))
                {
                    _logger.LogWarning("Unable to resolve content path from torrent files for hash {Hash}", hash);
                    return result;
                }

                // ✅ Apply remote path mapping
                result.SourceFiles = QbittorrentImportPathResolver.TranslateSourceFiles(QbittorrentImportPathResolver.BuildSourceFiles(savePath, files));
                if (!string.IsNullOrWhiteSpace(outputPath))
                {
                    result.ContentPath = outputPath;
                }

                _logger.LogInformation("Resolved import path for {Hash}: {Path}", hash, result.ContentPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error resolving import item for torrent {Hash}", hash);
            }

            return result;
        }

        internal static string ResolveTorrentContentPath(
            string savePath,
            List<Dictionary<string, JsonElement>> files)
        {
            return QbittorrentImportPathResolver.ResolveContentPath(savePath, files);
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

                // Create an HttpClient with its own CookieContainer so the qBittorrent
                // SID cookie from login is stored and sent with subsequent requests.
                // The factory "DownloadClient" has UseCookies=false which breaks qBit auth.
                var cookieJar = new System.Net.CookieContainer();
                using var handler = new HttpClientHandler
                {
                    CookieContainer = cookieJar,
                    UseCookies = true,
                    AutomaticDecompression = System.Net.DecompressionMethods.All
                };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

                // Login
                using var loginData = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("username", client.Username ?? string.Empty),
                    new KeyValuePair<string, string>("password", client.Password ?? string.Empty)
                });
                using var loginResp = await http.PostAsync($"{baseUrl}/api/v2/auth/login", loginData, cancellationToken);
                if (!loginResp.IsSuccessStatusCode)
                {
                    var loginError = await loginResp.Content.ReadAsStringAsync(cancellationToken);
                    throw new DownloadClientAdapterPollingException($"qBittorrent login failed for client {client.Name} at {baseUrl} - StatusCode={loginResp.StatusCode}, Response={loginError}");
                }
                _logger.LogDebug("qBittorrent login successful for client {ClientName}", client.Name);

                // Fetch qBittorrent global preferences for seed limit evaluation (Sonarr parity)
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
                            var prefs = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(prefsJson);
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

                // Request all necessary fields from torrents/info to avoid additional API calls per torrent
                // This single call replaces the need for individual /properties calls per download
                var fields = "hash,name,save_path,content_path,progress,amount_left,state,size,category,completion_on,seeding_time,ratio,ratio_limit,seeding_time_limit";

                // Prefer querying only the hashes we are tracking (if available) to avoid fetching all torrents
                var trackedHashes = downloads
                    .Select(d => d.Metadata != null && d.Metadata.TryGetValue("TorrentHash", out var h) ? h?.ToString() : null)
                    .Where(h => !string.IsNullOrEmpty(h))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // If we have tracked hashes, chunk them into batches to avoid very large queries and to allow
                // slight delays between requests to prevent overwhelming qBittorrent.
                List<Dictionary<string, System.Text.Json.JsonElement>> allTorrents = new();

                if (trackedHashes.Any())
                {
                    const int batchSize = 100; // safe default batch size
                    _logger.LogDebug("Querying qBittorrent for specific hashes (total={Count}), using batches of {BatchSize}", trackedHashes.Count, batchSize);

                    var batches = Enumerable.Range(0, (trackedHashes.Count + batchSize - 1) / batchSize)
                        .Select(i => trackedHashes.Skip(i * batchSize).Take(batchSize).ToList())
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
                        var torrents = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, System.Text.Json.JsonElement>>>(json);
                        if (torrents != null)
                        {
                            allTorrents.AddRange(torrents);
                        }

                        // Small delay between batches to avoid hammering the client
                        await Task.Delay(150, cancellationToken);
                    }
                }
                else
                {
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
                        if (torrents == null) return [];

                        allTorrents.AddRange(torrents);
                    }
                    else
                    {
                        // Default: fetch a limited set of recent torrents
                        var query = $"?fields={Uri.EscapeDataString(fields)}";
                        using var torrentsResp = await http.GetAsync($"{baseUrl}/api/v2/torrents/info{query}", cancellationToken);
                        if (!torrentsResp.IsSuccessStatusCode)
                        {
                            throw new DownloadClientAdapterPollingException($"Failed to fetch torrents from qBittorrent for {client.Name}");
                        }

                        var json = await torrentsResp.Content.ReadAsStringAsync(cancellationToken);
                        var torrents = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json);
                        if (torrents == null) return [];

                        allTorrents.AddRange(torrents);
                    }
                }

                // Build comprehensive lookup with all torrent info we need from single API call
                var torrentLookup = new List<(string Hash, string Name, string SavePath, string ContentPath, double Progress, long AmountLeft, string State, long Size, string Category, long? SeedingTime, double Ratio, float RatioLimit, long SeedingTimeLimit, bool CanMoveFiles, bool CanBeRemoved)>();
                foreach (var t in allTorrents)
                {
                    var hash = t.TryGetValue("hash", out var hashElement) ? hashElement.GetString() ?? "" : "";
                    var name = t.TryGetValue("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
                    var savePath = t.TryGetValue("save_path", out var savePathElement) ? savePathElement.GetString() ?? "" : "";
                    var contentPath = t.TryGetValue("content_path", out var contentPathElement) ? contentPathElement.GetString() ?? "" : "";
                    var progress = t.TryGetValue("progress", out var progressElement) ? progressElement.GetDouble() : 0.0;
                    var amountLeft = t.TryGetValue("amount_left", out var amountLeftElement) ? amountLeftElement.GetInt64() : 0L;
                    var state = t.TryGetValue("state", out var stateElement) ? stateElement.GetString() ?? "" : "";
                    var size = t.TryGetValue("size", out var sizeElement) ? sizeElement.GetInt64() : 0L;
                    var category = t.TryGetValue("category", out var categoryElement) ? categoryElement.GetString() ?? "" : "";
                    var seedingTime = t.TryGetValue("seeding_time", out var seedingTimeElement) ? seedingTimeElement.GetInt64() : (long?)null;
                    var tRatio = t.TryGetValue("ratio", out var ratioElement) ? ratioElement.GetDouble() : 0.0;
                    var tRatioLimit = t.TryGetValue("ratio_limit", out var ratioLimitElement) ? (float)ratioLimitElement.GetDouble() : -2f;
                    var tSeedingTimeLimit = t.TryGetValue("seeding_time_limit", out var seedingTimeLimitElement) ? seedingTimeLimitElement.GetInt64() : -2L;

                    // Sonarr parity: compute CanMoveFiles/CanBeRemoved per-torrent
                    var tIsStopped = state is "pausedUP" or "stoppedUP";
                    var tSeedLimitReached = QbittorrentSeedLimitEvaluator.HasReachedSeedLimit(
                        tRatio, tRatioLimit, seedingTime, tSeedingTimeLimit,
                        qbtGlobalMaxRatioEnabled, qbtGlobalMaxRatio,
                        qbtGlobalMaxSeedingTimeEnabled, qbtGlobalMaxSeedingTime);
                    var tCanBeRemoved = qbtRemoveCompletedDownloads && tSeedLimitReached;
                    var tCanMoveFiles = tCanBeRemoved && tIsStopped;

                    torrentLookup.Add((hash, name, savePath, contentPath, progress, amountLeft, state, size, category, seedingTime, tRatio, tRatioLimit, tSeedingTimeLimit, tCanMoveFiles, tCanBeRemoved));
                }


                _logger.LogDebug("Found {TorrentCount} torrents in qBittorrent for client {ClientName}", torrentLookup.Count, client.Name);

                // Log all torrents for diagnostics
                foreach (var t in torrentLookup.Take(10))
                {
                    _logger.LogDebug("qBittorrent torrent: Name={Name}, Hash={Hash}, Progress={Progress:P2}, State={State}, Size={Size}",
                        t.Name, t.Hash, t.Progress, t.State, t.Size);
                }

                // For each DB download associated with this client, try to find matching torrent
                _logger.LogInformation("Checking {DownloadCount} downloads against qBittorrent torrents for client {ClientName}",
                    downloads.Count, client.Name);

                foreach (var dl in downloads)
                {
                    try
                    {
                        _logger.LogDebug("Looking for qBittorrent match for download {DownloadId}: {Title}", dl.Id, dl.Title);

                        // Try hash-based matching first (most reliable for qBittorrent)
                        var matched = (Hash: "", Name: "", SavePath: "", ContentPath: "", Progress: 0.0, AmountLeft: 0L, State: "", Size: 0L, Category: "", SeedingTime: (long?)null, Ratio: 0.0, RatioLimit: -2f, SeedingTimeLimit: -2L, CanMoveFiles: false, CanBeRemoved: false);

                        // Check if we have a stored torrent hash for this download
                        if (dl.Metadata != null && dl.Metadata.TryGetValue("TorrentHash", out var hashObj))
                        {
                            var storedHash = hashObj?.ToString();
                            if (!string.IsNullOrEmpty(storedHash))
                            {
                                matched = torrentLookup.FirstOrDefault(t =>
                                    string.Equals(t.Hash, storedHash, StringComparison.OrdinalIgnoreCase));

                                if (!string.IsNullOrEmpty(matched.Hash))
                                {
                                    _logger.LogDebug("Found qBittorrent torrent by hash match: {Hash} for download {DownloadId}", storedHash, dl.Id);
                                }
                            }
                        }

                        // Fallback to deterministic matching if hash matching failed.
                        // Following Sonarr's pattern: only match on exact identifiers
                        // (name or content path), never on fuzzy title similarity.
                        // Fuzzy matching caused cross-contamination (e.g. importing
                        // "Mr. Mercedes" files into "One Hundred Years of Solitude").
                        if (string.IsNullOrEmpty(matched.Hash))
                        {
                            _logger.LogInformation("Hash matching failed for download {DownloadId}, trying exact name/path matching", dl.Id);

                            // 1. Exact torrent name == download title
                            matched = torrentLookup.FirstOrDefault(t =>
                                string.Equals(t.Name, dl.Title, StringComparison.OrdinalIgnoreCase));

                            // 2. Exact normalized title match (strip brackets/quality tags only)
                            if (string.IsNullOrEmpty(matched.Hash))
                            {
                                var dlNorm = TitleUtils.NormalizeTitle(dl.Title);
                                matched = torrentLookup.FirstOrDefault(t =>
                                    string.Equals(TitleUtils.NormalizeTitle(t.Name), dlNorm, StringComparison.OrdinalIgnoreCase));

                                if (!string.IsNullOrEmpty(matched.Hash))
                                {
                                    _logger.LogInformation("Normalized title match: '{DbTitle}' <-> '{TorrentTitle}'", dl.Title, matched.Name);
                                }
                            }

                            // 3. Exact content path match
                            if (string.IsNullOrEmpty(matched.Hash) && !string.IsNullOrEmpty(dl.DownloadPath))
                            {
                                var dlPathNorm = Path.GetFullPath(dl.DownloadPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                                matched = torrentLookup.FirstOrDefault(t =>
                                {
                                    if (string.IsNullOrEmpty(t.ContentPath)) return false;
                                    var contentNorm = Path.GetFullPath(t.ContentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                                    return string.Equals(dlPathNorm, contentNorm, StringComparison.OrdinalIgnoreCase);
                                });
                            }
                        }

                        if (string.IsNullOrEmpty(matched.Hash))
                        {
                            _logger.LogWarning("No matching qBittorrent torrent found for download {DownloadId}: {Title}", dl.Id, dl.Title);
                            continue;
                        }

                        _logger.LogDebug("Found matching qBittorrent torrent for {DownloadId}: {TorrentName} (Hash: {Hash}, State: {State}, Progress: {Progress:P2}, SavePath: {SavePath}, ContentPath: {ContentPath})",
                            dl.Id, matched.Name, matched.Hash, matched.State, matched.Progress, matched.SavePath, matched.ContentPath);

                        // DIAGNOSTIC: Log detailed completion check values
                        _logger.LogInformation("Completion diagnostic for {DownloadId}: Progress={Progress:F4} (>= 1.0? {ProgressCheck}), AmountLeft={AmountLeft} (== 0? {AmountCheck}), State={State}",
                            dl.Id, matched.Progress, matched.Progress >= 1.0, matched.AmountLeft, matched.AmountLeft == 0, matched.State);

                        if (!string.IsNullOrEmpty(matched.SavePath) && dl.DownloadPath != matched.SavePath)
                        {
                            dl.DownloadPath = matched.SavePath;
                        }

                        if (dl.Metadata == null) dl.Metadata = new Dictionary<string, object>();

                        if (!string.IsNullOrEmpty(matched.ContentPath))
                        {
                            dl.Metadata["ClientContentPath"] = matched.ContentPath;
                        }

                        if (matched.SeedingTime.HasValue)
                        {
                            dl.Metadata["SeedingTimeSeconds"] = matched.SeedingTime.Value;
                        }

                        dl.Metadata["CanMoveFiles"] = matched.CanMoveFiles;
                        dl.Metadata["CanBeRemoved"] = matched.CanBeRemoved;

                        AdapterUtils.MapDownloadProgress(dl, matched.Progress * 100, matched.AmountLeft, matched.State);

                        // Skip finalization/progress logic for downloads that are already
                        // being processed, awaiting import, or fully imported. Re-entering
                        // finalization for these would cause duplicate notifications and
                        // potentially import the wrong files a second time.
                        if (dl.Status == DownloadStatus.Moved ||
                            dl.Status == DownloadStatus.Processing ||
                            dl.Status == DownloadStatus.ImportPending)
                        {
                            _logger.LogDebug("Skipping finalization for {Status} download {DownloadId}", dl.Status, dl.Id);
                            continue;
                        }

                        var normalizedState = (matched.State ?? string.Empty).ToLowerInvariant();
                        if (normalizedState == "error" || normalizedState == "missingfiles")
                        {
                            dl.Failed($"qBittorrent state: {matched.State}");
                            continue;
                        }

                        // Lenient completion detection for qBittorrent
                        // A torrent is complete when progress >= 100% OR amount left is 0
                        // The stability window below ensures we don't immediately import a torrent
                        // that just hit 100% - we wait for the configured delay period
                        var isComplete = matched.Progress >= 1.0 || matched.AmountLeft == 0;

                        _logger.LogDebug("Completion check for {DownloadId}: IsComplete={IsComplete}, Progress={Progress:P2}, AmountLeft={AmountLeft}, State={State}",
                            dl.Id, isComplete, matched.Progress, matched.AmountLeft, matched.State);

                        if (isComplete)
                        {
                            // Determine the best path to use for file discovery
                            // Priority: content_path (actual file/folder) > save_path + name (torrent root) > save_path (download directory)
                            var completionPath = !string.IsNullOrEmpty(matched.ContentPath)
                                ? matched.ContentPath
                                : (!string.IsNullOrEmpty(matched.SavePath) && !string.IsNullOrEmpty(matched.Name)
                                    ? FileUtils.CombineWithOptionalBase(matched.SavePath, matched.Name)
                                    : matched.SavePath);

                            _logger.LogInformation("Download {DownloadId} observed as complete candidate (qBittorrent). Torrent: {TorrentName}, Path: {Path}. Waiting for stability window.",
                                dl.Id, matched.Name, completionPath);

                            dl.Completed();
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Error processing download {DownloadId} while polling qBittorrent", dl.Id);
                    }
                }

                return downloads;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                throw new DownloadClientAdapterPollingException($"Error polling qBittorrent client {client.Name}");
            }
        }

    }
}
