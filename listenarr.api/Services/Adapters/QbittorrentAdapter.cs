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

namespace Listenarr.Api.Services.Adapters
{
    /// <summary>
    /// qBittorrent protocol implementation.
    /// </summary>
    public class QbittorrentAdapter : IDownloadClientAdapter
    {
        public string ClientId => "qbittorrent";
        public string ClientType => "qbittorrent";
        public DownloadProtocol Protocol => DownloadProtocol.Torrent;

        private readonly IHttpClientFactory _httpFactory;
        private readonly ILogger<QbittorrentAdapter> _logger;
        private readonly IRemotePathMappingService _pathMappingService;
        private readonly ITorrentFileDownloader _torrentFileDownloader;

        public QbittorrentAdapter(IHttpClientFactory httpFactory, IRemotePathMappingService pathMappingService, ITorrentFileDownloader torrentFileDownloader, ILogger<QbittorrentAdapter> logger)
        {
            _httpFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
            _pathMappingService = pathMappingService ?? throw new ArgumentNullException(nameof(pathMappingService));
            _torrentFileDownloader = torrentFileDownloader ?? throw new ArgumentNullException(nameof(torrentFileDownloader));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            try
            {
                var baseUrl = DownloadClientUriBuilder.BuildAuthority(client);

                // Prefer the IHttpClientFactory-created client so unit tests can inject
                // a DelegatingHandler mock. Fall back to a local cookie-enabled client
                // only when required for real-world qBittorrent auth flows.
                HttpClient? http = null;
                bool disposeHttp = false;
                try
                {
                    http = _httpFactory?.CreateClient(client.Id ?? "qbittorrent");
                    if (http == null)
                    {
                        var cookieJar = new CookieContainer();
                        var handler = new HttpClientHandler
                        {
                            CookieContainer = cookieJar,
                            UseCookies = true,
                            AutomaticDecompression = DecompressionMethods.All
                        };
                        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
                        disposeHttp = true;
                    }
                    else
                    {
                        // ensure a reasonable timeout for factory clients
                        http.Timeout = TimeSpan.FromSeconds(30);
                    }

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

                                    // If we used a factory client, try a cookie-enabled HttpClient as a last resort
                                    if (!disposeHttp)
                                    {
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
                                            using var localLoginContent = new FormUrlEncodedContent(new[]
                                            {
                                            new KeyValuePair<string, string>("username", client.Username),
                                            new KeyValuePair<string, string>("password", client.Password)
                                        });

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

                                    return (false, "qBittorrent: Connection to download client successful but could not authenticate. Please check username/password.");
                                }
                                else
                                {
                                    var body = string.Empty;
                                    try { body = await loginResp.Content.ReadAsStringAsync(ct); }
                                    catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException)
                                    {
                                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
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
                            return (false, "Connection failed.");
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
                finally
                {
                    if (disposeHttp)
                    {
                        try { http?.Dispose(); }
                        catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException)
                        {
                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                        }
                    }
                }
            }
            catch (TaskCanceledException tce)
            {
                _logger.LogDebug(tce, "qBittorrent TestConnection timed out");
                return (false, "Connection timed out.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "qBittorrent TestConnection failed");
                return (false, "Connection failed.");
            }
        }

        public async Task<string?> AddAsync(DownloadClientConfiguration client, SearchResult result, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(result);

            var magnetLink = DownloadClientUriBuilder.NormalizeMagnetLink(result.MagnetLink);
            var httpTorrentUrl = NormalizeTorrentUrl(result.TorrentUrl);

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

                using var loginResponse = await httpClient.PostAsync($"{baseUrl}/api/v2/auth/login", loginData, ct);
                if (!loginResponse.IsSuccessStatusCode)
                {
                    var body = await loginResponse.Content.ReadAsStringAsync(ct);
                    var redacted = LogRedaction.RedactText(body, LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { client.Password ?? string.Empty }));

                    if (loginResponse.StatusCode == HttpStatusCode.Forbidden)
                    {
                        using var testResp = await httpClient.GetAsync($"{baseUrl}/api/v2/app/version", ct);
                        if (!testResp.IsSuccessStatusCode)
                        {
                            _logger.LogWarning("qBittorrent auth appears enabled and credentials are invalid for client {ClientId}", client.Id);
                            throw new Exception("qBittorrent authentication enabled but credentials are incorrect");
                        }
                        else
                        {
                            _logger.LogInformation("qBittorrent authentication disabled; proceeding without credentials for client {ClientId}", LogRedaction.SanitizeText(client.Id));
                        }
                    }
                    else
                    {
                        _logger.LogWarning("qBittorrent login failed: {Status} - {Body}", loginResponse.StatusCode, redacted);
                        throw new Exception($"qBittorrent login failed with status {loginResponse.StatusCode}");
                    }
                }
                else
                {
                    _logger.LogDebug("Authenticated to qBittorrent for client {ClientId}", LogRedaction.SanitizeText(client.Id));
                }

                // Request only the hash field before adding to minimize memory usage
                using var beforeResp = await httpClient.GetAsync($"{baseUrl}/api/v2/torrents/info?fields=hash", ct);
                var existingHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (beforeResp.IsSuccessStatusCode)
                {
                    var beforeJson = await beforeResp.Content.ReadAsStringAsync(ct);
                    if (!string.IsNullOrWhiteSpace(beforeJson))
                    {
                        try
                        {
                            var beforeList = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(beforeJson);
                            if (beforeList != null)
                            {
                                foreach (var t in beforeList.Where(t => t.TryGetValue("hash", out _)))
                                {
                                    existingHashes.Add(t["hash"].GetString() ?? string.Empty);
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogDebug(ex, "Failed to parse qBittorrent 'before' torrents list (non-fatal)");
                        }
                    }
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

                HttpResponseMessage addResponse;
                // Prefer a validated HTTP(S) torrent URL when one exists so we can add
                // authenticated/private-tracker content via bytes and only fall back to
                // a magnet when no file data can be obtained.
                byte[]? torrentFileData = result.TorrentFileContent;
                if (torrentFileData == null || torrentFileData.Length == 0)
                {
                    var downloadResult = await TryPredownloadTorrentFileAsync(httpTorrentUrl, result.Title, ct);
                    if (downloadResult.HasBytes)
                    {
                        torrentFileData = downloadResult.TorrentBytes;
                        _logger.LogInformation("Pre-downloaded torrent file ({Bytes} bytes) for '{Title}'",
                            torrentFileData!.Length, LogRedaction.SanitizeText(result.Title));
                    }
                    else if (downloadResult.HasMagnet)
                    {
                        // Indexer redirected to a magnet link — use it as the torrent URL instead
                        magnetLink = DownloadClientUriBuilder.NormalizeMagnetLink(downloadResult.MagnetUri);
                        _logger.LogInformation("Indexer redirected to magnet link for '{Title}'", LogRedaction.SanitizeText(result.Title));
                    }
                }

                var torrentUrl = new[] { magnetLink, httpTorrentUrl }
                    .FirstOrDefault(static url => !string.IsNullOrEmpty(url)) ?? string.Empty;

                if ((torrentFileData == null || torrentFileData.Length == 0) && string.IsNullOrEmpty(torrentUrl))
                    throw new ArgumentException("No magnet link or torrent URL provided", nameof(result));

                var extractedHash = TryExtractMagnetHash(torrentUrl);

                if (torrentFileData != null && torrentFileData.Length > 0)
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
                else
                {
                    var formData = new List<KeyValuePair<string, string>>
                    {
                        new("urls", torrentUrl),
                        new("savepath", savePath)
                    };

                    if (!string.IsNullOrEmpty(category))
                        formData.Add(new("category", category));
                    if (!string.IsNullOrEmpty(tags))
                        formData.Add(new("tags", tags));

                    using var addData = new FormUrlEncodedContent(formData);
                    addResponse = await httpClient.PostAsync($"{baseUrl}/api/v2/torrents/add", addData, ct);
                }

                using (addResponse)
                {
                    if (!addResponse.IsSuccessStatusCode)
                    {
                        var responseContent = await addResponse.Content.ReadAsStringAsync(ct);
                        var redacted = LogRedaction.RedactText(responseContent, LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { client.Password ?? string.Empty }));
                        _logger.LogError("Failed to add torrent to qBittorrent. Status: {Status}, Response: {Response}", addResponse.StatusCode, redacted);
                        throw new Exception($"Failed to add torrent to qBittorrent: {addResponse.StatusCode} - {redacted}");
                    }
                }
                _logger.LogInformation("Successfully sent torrent to qBittorrent");

                await Task.Delay(1000, ct);

                string? detectedHash = null;

                // Request only necessary fields (hash and name) to reduce response size
                using var afterResp = await httpClient.GetAsync($"{baseUrl}/api/v2/torrents/info?fields=hash,name", ct);
                if (afterResp.IsSuccessStatusCode)
                {
                    var afterJson = await afterResp.Content.ReadAsStringAsync(ct);
                    if (!string.IsNullOrWhiteSpace(afterJson))
                    {
                        try
                        {
                            var afterList = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(afterJson);
                            if (afterList != null)
                            {
                                foreach (var hash in afterList
                                    .Where(t => t.TryGetValue("hash", out _))
                                    .Select(t => t["hash"].GetString() ?? string.Empty)
                                    .Where(h => !existingHashes.Contains(h)))
                                {
                                    _logger.LogInformation("Detected new qBittorrent torrent: hash={Hash}", hash);
                                    detectedHash = hash;
                                    break;
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogDebug(ex, "Failed to parse qBittorrent 'after' torrents list (non-fatal)");
                        }
                    }
                }

                if (string.IsNullOrEmpty(detectedHash) && !string.IsNullOrEmpty(extractedHash))
                {
                    _logger.LogInformation("Using extracted magnet hash as fallback: {Hash}", extractedHash);
                    detectedHash = extractedHash;
                }

                // Inject tracker URLs via addTrackers API as a fallback to ensure the tracker
                // is registered even if qBittorrent didn't parse it from the torrent file.
                if (!string.IsNullOrEmpty(detectedHash) && torrentFileData != null && torrentFileData.Length > 0)
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
                                new KeyValuePair<string, string>("hash", detectedHash),
                                new KeyValuePair<string, string>("urls", trackerUrls)
                            });
                            using var trackersResp = await httpClient.PostAsync($"{baseUrl}/api/v2/torrents/addTrackers", addTrackersData, ct);
                            if (trackersResp.IsSuccessStatusCode)
                                _logger.LogInformation("Injected {Count} tracker(s) for torrent {Hash} via addTrackers API", trackerAnnounces.Count, detectedHash);
                            else
                                _logger.LogDebug("addTrackers API returned {Status} for torrent {Hash} (non-fatal)", trackersResp.StatusCode, detectedHash);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogDebug(ex, "Non-fatal failure injecting trackers via addTrackers API");
                    }
                }

                if (string.IsNullOrEmpty(detectedHash))
                    _logger.LogWarning("Unable to determine torrent hash after adding to qBittorrent for client {ClientId}", LogRedaction.SanitizeText(client.Id));

                return detectedHash;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "qBittorrent AddAsync failed for client {ClientId}", LogRedaction.SanitizeText(client.Id));
                throw;
            }
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

        private async Task<TorrentDownloadResult> TryPredownloadTorrentFileAsync(string? torrentUrl, string? title, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(torrentUrl))
            {
                return TorrentDownloadResult.Empty;
            }

            try
            {
                return await _torrentFileDownloader.DownloadAsync(torrentUrl, ct);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to pre-download torrent file for '{Title}', falling back to URL", LogRedaction.SanitizeText(title));
                return TorrentDownloadResult.Empty;
            }
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

                    var localPath = !string.IsNullOrEmpty(savePath)
                        ? await _pathMappingService.TranslatePathAsync(client.Id, savePath)
                        : savePath;

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
                        LocalPath = localPath
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

                    var localPath = !string.IsNullOrEmpty(savePath)
                        ? await _pathMappingService.TranslatePathAsync(client.Id, savePath)
                        : savePath;

                    var outputPath = !string.IsNullOrEmpty(contentPath)
                        ? await _pathMappingService.TranslatePathAsync(client.Id, contentPath)
                        : localPath;

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
                        DownloadId = hash.ToUpperInvariant(), // ✅ Uppercase SHA1 hash (standard format)
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
                result.OutputPath = await _pathMappingService.TranslatePathAsync(client.Id, outputPath);

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
                var localPath = await _pathMappingService.TranslatePathAsync(client.Id, result.ContentPath);
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
                    result.ContentPath = await _pathMappingService.TranslatePathAsync(client.Id, outputPath);
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
                var localPath = await _pathMappingService.TranslatePathAsync(clientId, sourceFile);
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

    }
}

