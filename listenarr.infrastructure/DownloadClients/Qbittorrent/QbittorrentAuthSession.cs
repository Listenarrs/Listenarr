/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Qbittorrent
{
    internal sealed class QbittorrentAuthSession
    {
        // qBittorrent counts every rejected login toward a per-IP ban, so polling with bad
        // credentials eventually locks Listenarr out. After a rejection, the same settings are
        // not retried until the cooldown passes; editing the client or a successful Test clears it.
        // Kept in memory only, so a restart also retries immediately.
        internal static readonly TimeSpan RejectedLoginCooldown = TimeSpan.FromHours(1);
        private static readonly ConcurrentDictionary<string, (int SettingsHash, DateTimeOffset RetryAfter)> RejectedLogins = new();

        private readonly ILogger _logger;

        public QbittorrentAuthSession(ILogger logger)
        {
            _logger = logger;
        }

        internal static void ClearRejectedLogin(DownloadClientConfiguration client) => RejectedLogins.TryRemove(client.Id, out _);

        public async Task<bool> LoginAsync(HttpClient httpClient, DownloadClientConfiguration client, CancellationToken cancellationToken = default)
        {
            var baseUrl = DownloadClientUriBuilder.BuildAuthority(client);
            var settingsHash = HashCode.Combine(baseUrl, client.Username, client.Password);

            if (RejectedLogins.TryGetValue(client.Id, out var rejected)
                && rejected.SettingsHash == settingsHash
                && DateTimeOffset.UtcNow < rejected.RetryAfter)
            {
                throw new QbittorrentException($"qBittorrent rejected the last login for {client.Id}; not retrying until {rejected.RetryAfter:u} unless the client settings change");
            }

            using var loginData = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("username", client.Username ?? string.Empty),
                new KeyValuePair<string, string>("password", client.Password ?? string.Empty)
            ]);

            using var loginResponse = await httpClient.PostAsync($"{baseUrl}/api/v2/auth/login", loginData, cancellationToken);
            var body = (await loginResponse.Content.ReadAsStringAsync(cancellationToken)).Trim();

            // qBittorrent 4.x/5.x answers bad credentials with 200 "Fails.", newer builds with 401,
            // and a banned IP with 403.
            var loginRejected = loginResponse.IsSuccessStatusCode
                ? string.Equals(body, "Fails.", StringComparison.Ordinal)
                : loginResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

            if (loginResponse.IsSuccessStatusCode && !loginRejected)
            {
                ClearRejectedLogin(client);
                _logger.LogDebug("Authenticated to qBittorrent for client {ClientId}", LogRedaction.SanitizeText(client.Id));
                return true;
            }

            if (!loginRejected)
            {
                throw new QbittorrentException($"qBittorrent login failed with status {loginResponse.StatusCode}");
            }

            using var testResp = await httpClient.GetAsync($"{baseUrl}/api/v2/app/version", cancellationToken);
            if (testResp.IsSuccessStatusCode)
            {
                _logger.LogDebug($"qBittorrent authentication disabled; proceeding without credentials for client {client.Id}");
                return true;
            }

            var retryAfter = DateTimeOffset.UtcNow + RejectedLoginCooldown;
            RejectedLogins[client.Id] = (settingsHash, retryAfter);

            var reason = IsIpBan(loginResponse.StatusCode, body)
                ? $"qBittorrent has banned this IP address after too many failed login attempts for {client.Id}. Fix the credentials, then wait for the ban to expire or restart qBittorrent"
                : $"qBittorrent authentication enabled but credentials are incorrect for {client.Id}";
            throw new QbittorrentException($"{reason}. Not retrying until {retryAfter:u} unless the client settings change");
        }

        // qBittorrent answers a banned IP with 403 "Your IP address has been banned after too many
        // failed authentication attempts."
        internal static bool IsIpBan(HttpStatusCode status, string body)
            => status == HttpStatusCode.Forbidden && body.Contains("banned", StringComparison.OrdinalIgnoreCase);
    }
}
