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
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Qbittorrent
{
    internal sealed class QbittorrentConnectionTester
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger _logger;
        private readonly string _clientType;

        public QbittorrentConnectionTester(IHttpClientFactory httpClientFactory, ILogger logger, string clientType)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _clientType = clientType;
        }

        public async Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            try
            {
                var baseUrl = DownloadClientUriBuilder.BuildAuthority(client);

                using var http = _httpClientFactory.CreateClient(_clientType);
                using var resp = await http.GetAsync($"{baseUrl}/api/v2/app/version", ct);
                if (resp.IsSuccessStatusCode)
                    return (true, "Successfully connected to qBittorrent.");

                if (resp.StatusCode == HttpStatusCode.Forbidden && !string.IsNullOrEmpty(client.Username))
                {
                    return await TryAuthenticatedConnectionAsync(client, baseUrl, http, ct);
                }

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

        private async Task<(bool Success, string Message)> TryAuthenticatedConnectionAsync(
            DownloadClientConfiguration client,
            string baseUrl,
            HttpClient http,
            CancellationToken ct)
        {
            try
            {
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
                        return await VerifyAuthenticatedConnectionAsync(client, baseUrl, http, loginResp, ct);
                    }

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
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "qBittorrent TestConnection login attempt failed");
                return (false, "Connection failed: login attempt failed.");
            }
        }

        private async Task<(bool Success, string Message)> VerifyAuthenticatedConnectionAsync(
            DownloadClientConfiguration client,
            string baseUrl,
            HttpClient http,
            HttpResponseMessage loginResp,
            CancellationToken ct)
        {
            try
            {
                if (loginResp.Headers.TryGetValues("Set-Cookie", out _))
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

            using var retry = await http.GetAsync($"{baseUrl}/api/v2/app/version", ct);
            if (retry.IsSuccessStatusCode)
                return (true, "Successfully connected to qBittorrent.");

            _logger.LogWarning("qBittorrent TestConnection: authenticated but subsequent request returned {Status} for client {ClientId}", retry.StatusCode, LogRedaction.SanitizeText(client.Id));
            return await TryCookieEnabledConnectionAsync(client, baseUrl, ct);
        }

        private async Task<(bool Success, string Message)> TryCookieEnabledConnectionAsync(
            DownloadClientConfiguration client,
            string baseUrl,
            CancellationToken ct)
        {
            try
            {
                using var local = QbittorrentCookieSession.CreateClient();
                using var localLoginContent = QbittorrentCookieSession.CreateLoginContent(client);

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

            return (false, "Authentication Failed. Check your username and/or password.");
        }
    }
}
