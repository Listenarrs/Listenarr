/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Deluge
{
    internal sealed class DelugeRpcClient
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _clientType;
        private readonly ILogger _logger;

        public DelugeRpcClient(IHttpClientFactory httpClientFactory, string clientType, ILogger logger)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _clientType = clientType ?? throw new ArgumentNullException(nameof(clientType));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<JsonElement> InvokeAsync(
            DownloadClientConfiguration client,
            string method,
            object[] parameters,
            CancellationToken ct)
        {
            using var http = _httpClientFactory.CreateClient(_clientType);
            await AuthenticateAsync(http, client, ct);
            await EnsureDaemonConnectedAsync(http, client, ct);
            return await SendRequestAsync(http, client, method, parameters, ct);
        }

        private async Task AuthenticateAsync(HttpClient http, DownloadClientConfiguration client, CancellationToken ct)
        {
            var res = await SendRequestAsync(http, client, "auth.login", [client.Password ?? string.Empty], ct);
            if (res.ValueKind != JsonValueKind.True)
            {
                throw new UnauthorizedAccessException("Failed to authenticate with Deluge Web UI");
            }
        }

        private async Task EnsureDaemonConnectedAsync(HttpClient http, DownloadClientConfiguration client, CancellationToken ct)
        {
            var connected = await SendRequestAsync(http, client, "web.connected", [], ct);
            if (connected.ValueKind == JsonValueKind.True)
            {
                return;
            }

            var hosts = await SendRequestAsync(http, client, "web.get_hosts", [], ct);
            if (hosts.ValueKind != JsonValueKind.Array || hosts.GetArrayLength() == 0)
            {
                throw new InvalidOperationException("Deluge Web is not connected to a daemon and no daemon hosts are configured");
            }

            string? hostId = null;
            foreach (var host in hosts.EnumerateArray())
            {
                if (host.ValueKind == JsonValueKind.Array && host.GetArrayLength() > 0)
                {
                    hostId = host[0].GetString();
                    if (!string.IsNullOrWhiteSpace(hostId))
                    {
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(hostId))
            {
                throw new InvalidOperationException("Deluge Web returned no daemon host id");
            }

            await SendRequestAsync(http, client, "web.connect", [hostId], ct);
        }

        private async Task<JsonElement> SendRequestAsync(
            HttpClient http,
            DownloadClientConfiguration client,
            string method,
            object[] parameters,
            CancellationToken ct)
        {
            var payload = JsonSerializer.Serialize(new { method, @params = parameters, id = 1 });
            using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(payload));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            var url = BuildBaseUrl(client);
            using var response = await http.PostAsync(url, content, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new UnauthorizedAccessException("Deluge rejected the request");
            }

            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseBody);

            if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            {
                throw new InvalidOperationException($"Deluge JSON-RPC error calling {method}: {error}");
            }

            if (doc.RootElement.TryGetProperty("result", out var result))
            {
                return result.Clone();
            }

            return default;
        }

        private static string BuildBaseUrl(DownloadClientConfiguration client)
        {
            var scheme = client.UseSSL ? "https" : "http";
            var host = client.Host.Trim().TrimEnd('/');
            if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                host = new Uri(host).Authority;
            }
            var urlBase = GetSetting(client, "urlBase") ?? GetSetting(client, "UrlBase") ?? string.Empty;
            urlBase = urlBase.Trim('/');
            return string.IsNullOrWhiteSpace(urlBase) ? $"{scheme}://{host}:{client.Port}/json" : $"{scheme}://{host}:{client.Port}/{urlBase}/json";
        }

        private static string? GetSetting(DownloadClientConfiguration c, string key) =>
            c.Settings != null && c.Settings.TryGetValue(key, out var v) ? v?.ToString() : null;
    }
}
