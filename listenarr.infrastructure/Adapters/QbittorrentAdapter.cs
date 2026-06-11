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
using BencodeNET.Parsing;
using BencodeNET.Torrents;
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

        public QbittorrentAdapter(IHttpClientFactory httpFactory, ITorrentFileDownloader torrentFileDownloader, ILogger<QbittorrentAdapter> logger)
        {
            _httpClientFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
            _torrentFileDownloader = torrentFileDownloader ?? throw new ArgumentNullException(nameof(torrentFileDownloader));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            try
            {
                var baseUrl = DownloadClientUriBuilder.BuildAuthority(client);

                using var http = _httpClientFactory.CreateClient(ClientType);
                using var resp = await http.GetAsync($"{baseUrl}/api/v2/app/version", ct);
                if (resp.IsSuccessStatusCode)
                    return (true, "Successfully connected to qBittorrent.");

                // If we get Forbidden and credentials are provided, try to authenticate and retry
                if (resp.StatusCode == HttpStatusCode.Forbidden && !string.IsNullOrEmpty(client.Username))
                {
                    try
                    {
                        // Helper to POST login with optional User-Agent header
                        async Task<HttpResponseMessage> PostLoginWithAgent(string userAgent)
                        {
                            var content = new FormUrlEncodedContent(new[]
                            {
                                new KeyValuePair<string, string>("username", client.Username ?? string.Empty),
                                new KeyValuePair<string, string>("password", client.Password ?? string.Empty)
                            });

                            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v2/auth/login") { Content = content };
                            if (!string.IsNullOrEmpty(userAgent)) req.Headers.UserAgent.ParseAdd(userAgent);
                            req.Headers.Referrer = new Uri(baseUrl + "/");
                            return await http.SendAsync(req, ct);
                        }

                        // Try a minimal UA first, then a browser-like UA if Forbidden
                        var loginResp = await PostLoginWithAgent("Listenarr/1.0");
                        if (!loginResp.IsSuccessStatusCode && loginResp.StatusCode == HttpStatusCode.Forbidden)
                        {
                            _logger.LogDebug("qBittorrent TestConnection: initial login returned Forbidden, retrying with browser UA for client {ClientId}", LogRedaction.SanitizeText(client.Id));
                            loginResp.Dispose();
                            loginResp = await PostLoginWithAgent("Mozilla/5.0 (compatible; Listenarr)");
                        }
                        using (loginResp)
                        {
                            if (loginResp.IsSuccessStatusCode)
                            {
                                // Try to detect cookies via Set-Cookie header when using factory clients
                                try
                                {
                                    if (loginResp.Headers.TryGetValues("Set-Cookie", out var cookieHeaders))
                                    {
                                        _logger.LogDebug("qBittorrent TestConnection: login returned Set-Cookie header for client {ClientId}", LogRedaction.SanitizeText(client.Id));
                                    }
                                    else
                                    {
                                        _logger.LogDebug("qBittorrent TestConnection: login succeeded but no Set-Cookie header present for client {ClientId}", LogRedaction.SanitizeText(client.Id));
                                    }
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                {
                                    _logger.LogDebug(ex, "qBittorrent TestConnection: unable to inspect login response headers for client {ClientId}", LogRedaction.SanitizeText(client.Id));
                                }

                                // Retry using the same client first (this covers unit tests which
                                // simulate stateful behavior on the mocked handler). If the retry
                                // fails and we created a factory client that doesn't handle cookies,
                                // fall back to a local cookie-enabled client attempt.
                                using var retry = await http.GetAsync($"{baseUrl}/api/v2/app/version", ct);
                                if (retry.IsSuccessStatusCode)
                                    return (true, "Successfully connected to qBittorrent.");

                                _logger.LogWarning("qBittorrent TestConnection: authenticated but subsequent request returned {Status} for client {ClientId}", retry.StatusCode, LogRedaction.SanitizeText(client.Id));

                                // Try a cookie-enabled HttpClient as a last resort
                                try
                                {
                                    var cookieJar2 = new CookieContainer();
                                    var handler2 = new HttpClientHandler
                                    {
                                        CookieContainer = cookieJar2,
                                        UseCookies = true,
                                        AutomaticDecompression = DecompressionMethods.All
                                    };

                                    using var local = new HttpClient(handler2) { Timeout = TimeSpan.FromSeconds(30) };
                                    using var localLoginContent = new FormUrlEncodedContent(
                                    [
                                        new KeyValuePair<string, string>("username", client.Username),
                                        new KeyValuePair<string, string>("password", client.Password)
                                    ]);

                                    using var localLogin = await local.PostAsync($"{baseUrl}/api/v2/auth/login", localLoginContent, ct);
                                    if (localLogin.IsSuccessStatusCode)
                                    {
                                        using var final = await local.GetAsync($"{baseUrl}/api/v2/app/version", ct);
                                        if (final.IsSuccessStatusCode)
                                            return (true, "Successfully connected to qBittorrent.");
                                    }
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                {
                                    _logger.LogDebug(ex, "qBittorrent TestConnection: fallback local login attempt failed for client {ClientId}", LogRedaction.SanitizeText(client.Id));
                                }
                            }
                            else
                            {
                                var body = string.Empty;
                                try { body = await loginResp.Content.ReadAsStringAsync(ct); }
                                catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException)
                                {
                                    _logger.LogDebug("Suppressed non-fatal exception in catch block.");
                                }
                                var redacted = LogRedaction.RedactText(body, LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { client.Password ?? string.Empty }));
                                _logger.LogWarning("qBittorrent TestConnection: login failed with status {Status} for client {ClientId} - {Body}", loginResp.StatusCode, LogRedaction.SanitizeText(client.Id), redacted);
                                return (false, "qBittorrent: Connection to download client successful but could not authenticate. Please check username/password.");
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogDebug(ex, "qBittorrent TestConnection login attempt failed");
                        return (false, "Connection failed: login attempt failed.");
                    }
                }

                // Provide clearer, user-friendly messages for common HTTP statuses
                if (resp.StatusCode == HttpStatusCode.Forbidden || resp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    if (string.IsNullOrEmpty(client.Username))
                        return (false, "Forbidden: Authentication required.");

                    return (false, "Authentication Failed. Check your username and/or password.");
                }

                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    return (false, "Could not connect to the host and/or port.");
                }

                return (false, $"qBittorrent: network error ({resp.StatusCode})");
            }
            catch (TaskCanceledException)
            {
                return (false, "Connection timed out.");
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                return (false, "Connection failed.");
            }
        }

        /// <summary>
        /// Perform the login operation with qBittorrent API
        /// </summary>
        /// <param name="httpClient"></param>
        /// <param name="client"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        /// <exception cref="QbittorrentException"></exception>
        private async Task<bool> LoginAsync(HttpClient httpClient, DownloadClientConfiguration client, CancellationToken cancellationToken = default)
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
                var body = await loginResponse.Content.ReadAsStringAsync(cancellationToken);

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

        public async Task<string?> AddAsync(DownloadClientConfiguration client, SearchResult result, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(result);

            var magnetLink = DownloadClientUriBuilder.NormalizeMagnetLink(result.MagnetLink);
            var httpTorrentUrl = NormalizeTorrentUrl(result.TorrentUrl);

            var baseUrl = DownloadClientUriBuilder.BuildAuthority(client);
            using var httpClient = _httpClientFactory.CreateClient(ClientType);

            try
            {
                await LoginAsync(httpClient, client, ct);
            }
            catch (QbittorrentException exception)
            {
                _logger.LogError(exception.Message);
                return null;
            }

            var savePath = client.DownloadPath ?? string.Empty;
            string? category = null;
            string? tags = null;

            if (client.Settings != null)
            {
                if (client.Settings.TryGetValue("category", out var categoryObj))
                    category = categoryObj?.ToString();
                if (client.Settings.TryGetValue("tags", out var tagsObj))
                    tags = tagsObj?.ToString();
            }

            var hash = string.Empty;

            byte[]? torrentFileData = result.TorrentFileContent;
            if (torrentFileData == null && !string.IsNullOrEmpty(httpTorrentUrl))
            {
                // Try to get torrent file with the torrent URL
                var downloadResult = await _torrentFileDownloader.DownloadAsync(httpTorrentUrl, ct);
                if (downloadResult.TorrentBytes != null)
                {
                    torrentFileData = downloadResult.TorrentBytes;
                    _logger.LogInformation($"Pre-downloaded torrent file ({torrentFileData!.Length} bytes) for '{LogRedaction.SanitizeText(result.Title)}'");
                }
                else if (downloadResult.HasMagnet && string.IsNullOrEmpty(magnetLink))
                {
                    magnetLink = DownloadClientUriBuilder.NormalizeMagnetLink(downloadResult.MagnetUri);
                    _logger.LogInformation($"Indexer redirected to magnet link for '{LogRedaction.SanitizeText(result.Title)}'");
                }
            }

            if (torrentFileData == null && string.IsNullOrEmpty(httpTorrentUrl) && string.IsNullOrEmpty(magnetLink))
            {
                _logger.LogError($"No torrent URL, no magnet link and no torrent file given, nothing can be added for search result {result.Title}");
                return null;
            }

            // Compute hash from torrent file
            if (torrentFileData != null)
            {
                using (var stream = new MemoryStream(torrentFileData))
                {
                    var parser = new BencodeParser();
                    Torrent torrent = parser.Parse<Torrent>(stream);
                    hash = torrent.GetInfoHash();
                }
            }
            // Get hash in magnet link
            else if (!string.IsNullOrEmpty(magnetLink))
            {
                hash = TryExtractMagnetHash(magnetLink);
            }

            if (string.IsNullOrEmpty(hash))
            {
                _logger.LogError($"Unable to compute hash for the given torrent: {result.Title} with torrent URL: {result.TorrentUrl} and magnet link: {result.MagnetLink}");
                return null;
            }

            // Add download using torrent file
            HttpResponseMessage addResponse;
            if (torrentFileData != null)
            {
                using var multipart = new MultipartFormDataContent();
                multipart.Add(new StringContent(savePath), "savepath");
                if (!string.IsNullOrEmpty(category))
                    multipart.Add(new StringContent(category), "category");
                if (!string.IsNullOrEmpty(tags))
                    multipart.Add(new StringContent(tags), "tags");

                var torrentFileName = string.IsNullOrEmpty(result.TorrentFileName) ? "download.torrent" : result.TorrentFileName;
                var torrentContent = new ByteArrayContent(torrentFileData);
                torrentContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-bittorrent");
                multipart.Add(torrentContent, "torrents", torrentFileName);

                addResponse = await httpClient.PostAsync($"{baseUrl}/api/v2/torrents/add", multipart, ct);
            }
            // Add using magnet link or torrent url
            else
            {
                var url = new[] { magnetLink, httpTorrentUrl }
                    .FirstOrDefault(static url => !string.IsNullOrEmpty(url)) ?? string.Empty;

                var formData = new List<KeyValuePair<string, string>>
                {
                    new("urls", url),
                    new("savepath", savePath)
                };

                if (!string.IsNullOrEmpty(category))
                    formData.Add(new("category", category));
                if (!string.IsNullOrEmpty(tags))
                    formData.Add(new("tags", tags));

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
            if (torrentFileData != null)
            {
                try
                {
                    var announces = MyAnonamouseHelper.ExtractAnnounceUrls(torrentFileData);
                    // Filter to only actual tracker announce URLs — exclude file/web-seed URLs
                    var trackerAnnounces = announces?.Where(a =>
                        a.Contains("/announce", StringComparison.OrdinalIgnoreCase) ||
                        a.Contains("/tracker", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (trackerAnnounces != null && trackerAnnounces.Count > 0)
                    {
                        var trackerUrls = string.Join("\n", trackerAnnounces.Distinct());
                        using var addTrackersData = new FormUrlEncodedContent(new[]
                        {
                            new KeyValuePair<string, string>("hash", hash),
                            new KeyValuePair<string, string>("urls", trackerUrls)
                        });
                        using var trackersResp = await httpClient.PostAsync($"{baseUrl}/api/v2/torrents/addTrackers", addTrackersData, ct);
                        if (trackersResp.IsSuccessStatusCode)
                            _logger.LogInformation($"Injected {trackerAnnounces.Count} tracker(s) for torrent {hash} via addTrackers API");
                        else
                            _logger.LogDebug($"addTrackers API returned {trackersResp.StatusCode} for torrent {hash} (non-fatal)");
                    }
                }
                catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                {
                    _logger.LogDebug(exception, "Non-fatal failure injecting trackers via addTrackers API");
                }
            }

            return hash;
        }

        private static string? TryExtractMagnetHash(string? torrentUrl)
        {
            if (string.IsNullOrEmpty(torrentUrl) ||
                !torrentUrl.Contains("xt=urn:btih:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var start = torrentUrl.IndexOf("xt=urn:btih:", StringComparison.OrdinalIgnoreCase) + "xt=urn:btih:".Length;
            var end = torrentUrl.IndexOf('&', start);
            if (end == -1) end = torrentUrl.Length;
            return torrentUrl[start..end].ToLowerInvariant();
        }

        private static string? NormalizeTorrentUrl(string? torrentUrl)
        {
            var trimmed = (torrentUrl ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                return null;
            }

            if (!DownloadClientUriBuilder.TryParseHttpOrHttpsAbsoluteUri(trimmed, out var torrentUri))
            {
                throw new ArgumentException("Torrent URL must be an absolute HTTP or HTTPS URL.", nameof(torrentUrl));
            }

            return torrentUri!.ToString();
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
                    var name = torrent.TryGetValue("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                    var progress = torrent.TryGetValue("progress", out var progressEl) ? progressEl.GetDouble() * 100 : 0;
                    var size = torrent.TryGetValue("size", out var sizeEl) ? sizeEl.GetInt64() : 0;
                    var downloaded = torrent.TryGetValue("downloaded", out var downloadedEl) ? downloadedEl.GetInt64() : 0;
                    var dlspeed = torrent.TryGetValue("dlspeed", out var dlspeedEl) ? dlspeedEl.GetDouble() : 0;
                    var eta = torrent.TryGetValue("eta", out var etaEl) ? (int?)etaEl.GetInt32() : null;
                    var state = torrent.TryGetValue("state", out var stateEl) ? stateEl.GetString() ?? "unknown" : "unknown";
                    var hash = torrent.TryGetValue("hash", out var hashEl) ? hashEl.GetString() ?? string.Empty : string.Empty;
                    var addedOn = torrent.TryGetValue("added_on", out var addedOnEl) ? addedOnEl.GetInt64() : 0;
                    var numSeeds = torrent.TryGetValue("num_seeds", out var numSeedsEl) ? (int?)numSeedsEl.GetInt32() : null;
                    var numLeechs = torrent.TryGetValue("num_leechs", out var numLeechsEl) ? (int?)numLeechsEl.GetInt32() : null;
                    var ratio = torrent.TryGetValue("ratio", out var ratioEl) ? (double?)ratioEl.GetDouble() : null;
                    var savePath = torrent.TryGetValue("save_path", out var savePathEl) ? savePathEl.GetString() ?? string.Empty : string.Empty;

                    List<Dictionary<string, JsonElement>> files = [];
                    using var filesResp = await httpClient.GetAsync($"{baseUrl}/api/v2/torrents/files?hash={hash}", ct);
                    if (filesResp.IsSuccessStatusCode)
                    {
                        var filesJson = await filesResp.Content.ReadAsStringAsync(ct);
                        files = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(filesJson) ?? [];
                    }

                    var localPath = savePath;
                    var outputPath = ResolveTorrentContentPath(savePath, files);

                    var status = state switch
                    {
                        "downloading" => "downloading",
                        "metaDL" => "downloading",
                        "forcedDL" => "downloading",
                        "forcedMetaDL" => "downloading",
                        "stalledDL" => "downloading",
                        "checkingDL" => "downloading",
                        "stoppedDL" => "paused",
                        "stoppedUP" => "paused",
                        "queuedDL" => "queued",
                        "queuedUP" => "queued",
                        "uploading" => "seeding",
                        "stalledUP" => "seeding",
                        "checkingUP" => "seeding",
                        "forcedUP" => "seeding",
                        "checkingResumeData" => "downloading",
                        "moving" => "downloading",
                        "error" => "failed",
                        "missingFiles" => "failed",
                        _ => "unknown"
                    };

                    if (progress >= 100.0 && (status == "seeding" || state == "uploading" || state == "stalledUP" || state == "checkingUP" || state == "forcedUP" || state == "stoppedUP"))
                    {
                        status = "completed";
                    }

                    items.Add(new QueueItem
                    {
                        Id = hash,
                        Title = name,
                        Quality = "Unknown",
                        Status = status,
                        Progress = progress,
                        Size = size,
                        Downloaded = downloaded,
                        DownloadSpeed = dlspeed,
                        Eta = eta >= 8640000 ? null : eta,
                        DownloadClient = client.Name,
                        DownloadClientId = client.Id,
                        DownloadClientType = "qbittorrent",
                        AddedAt = DateTimeOffset.FromUnixTimeSeconds(addedOn).DateTime,
                        Seeders = numSeeds,
                        Leechers = numLeechs,
                        Ratio = ratio,
                        CanPause = status == "downloading" || status == "queued",
                        CanRemove = true,
                        RemotePath = savePath,
                        LocalPath = localPath,
                        SourceFiles = BuildTorrentSourceFiles(savePath, files),
                        ContentPath = outputPath
                    });
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
                    var name = torrent.TryGetValue("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                    var progress = torrent.TryGetValue("progress", out var progressEl) ? progressEl.GetDouble() * 100 : 0;
                    var size = torrent.TryGetValue("size", out var sizeEl) ? sizeEl.GetInt64() : 0;
                    var downloaded = torrent.TryGetValue("downloaded", out var downloadedEl) ? downloadedEl.GetInt64() : 0;
                    var dlspeed = torrent.TryGetValue("dlspeed", out var dlspeedEl) ? dlspeedEl.GetDouble() : 0;
                    var eta = torrent.TryGetValue("eta", out var etaEl) ? (int?)etaEl.GetInt32() : null;
                    var state = torrent.TryGetValue("state", out var stateEl) ? stateEl.GetString() ?? "unknown" : "unknown";
                    var hash = torrent.TryGetValue("hash", out var hashEl) ? hashEl.GetString() ?? string.Empty : string.Empty;
                    var numSeeds = torrent.TryGetValue("num_seeds", out var numSeedsEl) ? (int?)numSeedsEl.GetInt32() : null;
                    var numLeechs = torrent.TryGetValue("num_leechs", out var numLeechsEl) ? (int?)numLeechsEl.GetInt32() : null;
                    var ratio = torrent.TryGetValue("ratio", out var ratioEl) ? (double?)ratioEl.GetDouble() : null;
                    // Per-torrent seed limit overrides (-1 = use global, -2 = use global, >=0 = per-torrent limit)
                    var ratioLimit = torrent.TryGetValue("ratio_limit", out var ratioLimitEl) ? (float)ratioLimitEl.GetDouble() : -2f;
                    var seedingTimeLimit = torrent.TryGetValue("seeding_time_limit", out var stlEl) ? stlEl.GetInt64() : -2L;
                    var seedingTime = torrent.TryGetValue("seeding_time", out var seedTimeEl) ? (long?)seedTimeEl.GetInt64() : null;
                    var savePath = torrent.TryGetValue("save_path", out var savePathEl) ? savePathEl.GetString() ?? string.Empty : string.Empty;
                    var category = torrent.TryGetValue("category", out var categoryEl) ? categoryEl.GetString() ?? string.Empty : string.Empty;
                    var contentPath = torrent.TryGetValue("content_path", out var contentPathEl) ? contentPathEl.GetString() ?? string.Empty : string.Empty;

                    // ✅ Map qBittorrent status to DownloadItemStatus enum
                    var status = state switch
                    {
                        "downloading" => DownloadItemStatus.Downloading,
                        "metaDL" => DownloadItemStatus.Downloading,
                        "forcedDL" => DownloadItemStatus.Downloading,
                        "forcedMetaDL" => DownloadItemStatus.Downloading,
                        "stalledDL" => DownloadItemStatus.Downloading,
                        "checkingDL" => DownloadItemStatus.Downloading,
                        "stoppedDL" => DownloadItemStatus.Paused,
                        "stoppedUP" => DownloadItemStatus.Paused,
                        "queuedDL" => DownloadItemStatus.Queued,
                        "queuedUP" => DownloadItemStatus.Queued,
                        "uploading" => DownloadItemStatus.Downloading, // Still seeding after completion
                        "stalledUP" => DownloadItemStatus.Downloading,
                        "checkingUP" => DownloadItemStatus.Downloading,
                        "forcedUP" => DownloadItemStatus.Downloading,
                        "checkingResumeData" => DownloadItemStatus.Downloading,
                        "moving" => DownloadItemStatus.Downloading,
                        "error" => DownloadItemStatus.Failed,
                        "missingFiles" => DownloadItemStatus.Failed,
                        _ => DownloadItemStatus.Warning
                    };

                    // If completed, override status
                    if (progress >= 100.0 && (status == DownloadItemStatus.Downloading || state == "uploading" || state == "stalledUP" || state == "checkingUP" || state == "forcedUP" || state == "stoppedUP"))
                    {
                        status = DownloadItemStatus.Completed;
                    }

                    var localPath = savePath;

                    var outputPath = localPath;

                    TimeSpan? remainingTime = eta.HasValue && eta.Value < 8640000 ? TimeSpan.FromSeconds(eta.Value) : null;

                    // qBittorrent can remove completed torrents while still seeding; file moves
                    // still require the torrent to be stopped so we don't break the payload.
                    var isStopped = state is "pausedUP" or "stoppedUP";
                    var seedLimitReached = HasReachedSeedLimit(
                        ratio ?? 0, ratioLimit, seedingTime, seedingTimeLimit,
                        globalMaxRatioEnabled, globalMaxRatio,
                        globalMaxSeedingTimeEnabled, globalMaxSeedingTime);
                    var canBeRemoved = removeCompletedDownloads && seedLimitReached;
                    var canMoveFiles = canBeRemoved && isStopped;

                    items.Add(new DownloadClientItem
                    {
                        DownloadId = hash,
                        Title = name,
                        Category = category,
                        Status = status,
                        TotalSize = size,
                        RemainingSize = size - downloaded,
                        RemainingTime = remainingTime,
                        SeedRatio = ratio,
                        OutputPath = outputPath,
                        Message = state,
                        Progress = progress,
                        DownloadSpeed = dlspeed,
                        Seeders = numSeeds ?? 0,
                        Leechers = numLeechs ?? 0,
                        CanBeRemoved = canBeRemoved,
                        CanMoveFiles = canMoveFiles,
                        DownloadClientInfo = DownloadClientItemClientInfo.FromClient(
                            clientId: client.Id,
                            clientName: client.Name,
                            clientType: "qbittorrent",
                            protocol: DownloadProtocol.Torrent,
                            removeCompletedDownloads: removeCompletedDownloads,
                            hasPostImportCategory: !string.IsNullOrEmpty(client.Settings?.GetValueOrDefault("postImportCategory")?.ToString())
                        )
                    });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Error getting qBittorrent items - client may be unreachable");
            }

            return items;
        }

        /// <summary>
        /// Determines whether a qBittorrent torrent has reached its seed limit (ratio or time).
        /// Mirrors Sonarr's HasReachedSeedLimit logic for qBittorrent.
        /// </summary>
        /// <param name="ratio">Current torrent ratio</param>
        /// <param name="ratioLimit">Per-torrent ratio limit (-2 = use global, -1 = no limit, >=0 = per-torrent)</param>
        /// <param name="seedingTime">Torrent seeding time in seconds (null if unknown)</param>
        /// <param name="seedingTimeLimit">Per-torrent seeding time limit in minutes (-2 = use global, -1 = no limit, >=0 = per-torrent)</param>
        /// <param name="globalMaxRatioEnabled">Whether global max ratio is enabled in qBit preferences</param>
        /// <param name="globalMaxRatio">Global max ratio from qBit preferences</param>
        /// <param name="globalMaxSeedingTimeEnabled">Whether global max seeding time is enabled in qBit preferences</param>
        /// <param name="globalMaxSeedingTime">Global max seeding time from qBit preferences (in minutes)</param>
        private static bool HasReachedSeedLimit(
            double ratio,
            float ratioLimit,
            long? seedingTime,
            long seedingTimeLimit,
            bool globalMaxRatioEnabled,
            float globalMaxRatio,
            bool globalMaxSeedingTimeEnabled,
            long globalMaxSeedingTime)
        {
            var hasEffectiveRatioLimit =
                ratioLimit >= 0 ||
                (ratioLimit <= -2 && globalMaxRatioEnabled && globalMaxRatio > 0);
            var hasEffectiveSeedingTimeLimit =
                seedingTimeLimit >= 0 ||
                (seedingTimeLimit <= -2 && globalMaxSeedingTimeEnabled && globalMaxSeedingTime > 0);

            if (!hasEffectiveRatioLimit && !hasEffectiveSeedingTimeLimit)
            {
                return true;
            }

            // Check ratio limit (per-torrent override takes precedence)
            if (ratioLimit >= 0 && ratioLimit - ratio <= 0.001)
            {
                // Per-torrent ratio limit set
                return true;
            }

            if (ratioLimit <= -2 && globalMaxRatioEnabled && globalMaxRatio - ratio <= 0.001)
            {
                // Use global ratio limit (-2 means inherit global)
                return true;
            }

            // Check seeding time limit (per-torrent override takes precedence)
            if (seedingTimeLimit >= 0 &&
                seedingTime is long currentSeedingTime &&
                currentSeedingTime >= seedingTimeLimit * 60)
            {
                // Per-torrent seeding time limit set (in minutes, convert to seconds for comparison)
                return true;
            }

            if (seedingTimeLimit <= -2 &&
                globalMaxSeedingTimeEnabled &&
                seedingTime is long inheritedSeedingTime &&
                inheritedSeedingTime >= globalMaxSeedingTime * 60)
            {
                // Use global seeding time limit (in minutes, convert to seconds)
                return true;
            }

            return false;
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

                var outputPath = ResolveTorrentContentPath(savePath, files);
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

                var outputPath = ResolveTorrentContentPath(savePath, files);
                if (string.IsNullOrEmpty(outputPath) && string.IsNullOrWhiteSpace(resolvedExistingContentPath))
                {
                    _logger.LogWarning("Unable to resolve content path from torrent files for hash {Hash}", hash);
                    return result;
                }

                // ✅ Apply remote path mapping
                result.SourceFiles = await TranslateSourceFilesAsync(client.Id, BuildTorrentSourceFiles(savePath, files));
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

        private static string CombineWithOptionalBase(string? basePath, string candidatePath)
        {
            var normalizedPath = candidatePath.Trim();

            if (string.IsNullOrEmpty(normalizedPath))
            {
                return normalizedPath;
            }

            if (Path.IsPathRooted(normalizedPath) || string.IsNullOrWhiteSpace(basePath))
            {
                return normalizedPath;
            }

            var relativePath = normalizedPath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Path.IsPathRooted(relativePath))
            {
                return relativePath;
            }

            var normalizedBasePath = basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrEmpty(normalizedBasePath)
                ? relativePath
                : normalizedBasePath + Path.DirectorySeparatorChar + relativePath;
        }

        private static List<string> BuildTorrentSourceFiles(
            string savePath,
            List<Dictionary<string, JsonElement>> files)
        {
            if (string.IsNullOrWhiteSpace(savePath) || files == null || files.Count == 0)
            {
                return new List<string>();
            }

            return files
                .Select(file => file.TryGetValue("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => CombineWithOptionalBase(savePath, name.Replace('/', Path.DirectorySeparatorChar)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private async Task<List<string>> TranslateSourceFilesAsync(string clientId, IEnumerable<string> sourceFiles)
        {
            var translated = new List<string>();
            foreach (var sourceFile in sourceFiles.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                var localPath = sourceFile;
                translated.Add(localPath);
            }

            return translated
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static string ResolveTorrentContentPath(
            string savePath,
            List<Dictionary<string, JsonElement>> files)
        {
            if (string.IsNullOrWhiteSpace(savePath) || files == null || files.Count == 0)
            {
                return string.Empty;
            }

            var fileNames = files
                .Select(f => f.TryGetValue("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();

            if (fileNames.Count == 0)
            {
                return string.Empty;
            }

            var firstFile = fileNames[0];
            var firstParts = firstFile.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var hasNestedPath = firstParts.Length > 1;

            if (fileNames.Count == 1)
            {
                return hasNestedPath
                    ? CombineWithOptionalBase(savePath, firstParts[0])
                    : CombineWithOptionalBase(savePath, firstFile);
            }

            if (!hasNestedPath)
            {
                return savePath;
            }

            var topLevel = firstParts[0];
            var allShareTopLevel = fileNames.All(name =>
            {
                var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length > 1 && string.Equals(parts[0], topLevel, StringComparison.Ordinal);
            });

            return allShareTopLevel
                ? CombineWithOptionalBase(savePath, topLevel)
                : savePath;
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
                    var tSeedLimitReached = QBitHasReachedSeedLimit(
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
                                    ? CombineWithOptionalBase(matched.SavePath, matched.Name)
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

        /// <summary>
        /// Determines whether a qBittorrent torrent has reached its seed limit.
        /// Used by the qBittorrent poller to compute CanMoveFiles/CanBeRemoved per-torrent.
        /// Mirrors Sonarr's HasReachedSeedLimit logic.
        /// </summary>
        private static bool QBitHasReachedSeedLimit(
            double ratio,
            float ratioLimit,
            long? seedingTime,
            long seedingTimeLimit,
            bool globalMaxRatioEnabled,
            float globalMaxRatio,
            bool globalMaxSeedingTimeEnabled,
            long globalMaxSeedingTime)
        {
            var hasEffectiveRatioLimit =
                ratioLimit >= 0 ||
                (ratioLimit <= -2 && globalMaxRatioEnabled && globalMaxRatio > 0);
            var hasEffectiveSeedingTimeLimit =
                seedingTimeLimit >= 0 ||
                (seedingTimeLimit <= -2 && globalMaxSeedingTimeEnabled && globalMaxSeedingTime > 0);

            if (!hasEffectiveRatioLimit && !hasEffectiveSeedingTimeLimit)
                return true;

            // Check ratio limit (per-torrent override takes precedence)
            if (ratioLimit >= 0 && ratioLimit - ratio <= 0.001)
                return true;

            if (ratioLimit <= -2 && globalMaxRatioEnabled && globalMaxRatio - ratio <= 0.001)
                return true;

            // Check seeding time limit (per-torrent override takes precedence)
            if (seedingTimeLimit >= 0 &&
                seedingTime is long currentSeedingTime &&
                currentSeedingTime >= seedingTimeLimit * 60)
                return true;

            if (seedingTimeLimit <= -2 &&
                globalMaxSeedingTimeEnabled &&
                seedingTime is long inheritedSeedingTime &&
                inheritedSeedingTime >= globalMaxSeedingTime * 60)
                return true;

            return false;
        }
    }
}

