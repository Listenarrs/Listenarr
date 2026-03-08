using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Listenarr.Domain.Models;
using Listenarr.Api.Services.Adapters;

namespace Listenarr.Api.Tests
{
    /// <summary>
    /// Lightweight test implementation of IDownloadClientGateway used by unit tests.
    /// It attempts to use a provided HttpClient/IHttpClientFactory to handle simple SABnzbd
    /// queue/history requests so tests that register a DelegatingHandlerMock will work.
    /// For other operations it returns conservative defaults.
    /// </summary>
    public class TestDownloadClientGateway : Listenarr.Api.Services.IDownloadClientGateway, IDisposable
    {
        private readonly IHttpClientFactory? _httpFactory;
        private readonly HttpClient? _httpClient;
        private HttpClient? _ownedHttpClient;

        public TestDownloadClientGateway(IHttpClientFactory? httpFactory = null, HttpClient? httpClient = null)
        {
            _httpFactory = httpFactory;
            _httpClient = httpClient;
        }

        private HttpClient GetHttpClient()
        {
            if (_httpClient != null) return _httpClient;
            if (_httpFactory != null) return _httpFactory.CreateClient();
            return _ownedHttpClient ??= new HttpClient();
        }

        public void Dispose()
        {
            _ownedHttpClient?.Dispose();
        }

        public Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            return Task.FromResult((true, "ok"));
        }

        public Task<string?> AddAsync(DownloadClientConfiguration client, SearchResult result, CancellationToken ct = default)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default)
        {
            return Task.FromResult(false);
        }

        public async Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            try
            {
                if (string.Equals(client.Type, "sabnzbd", StringComparison.OrdinalIgnoreCase))
                {
                    var http = GetHttpClient();
                    var baseUrl = $"{(client.UseSSL ? "https" : "http")}://{client.Host}:{client.Port}/api";
                    var url = $"{baseUrl}?mode=queue&output=json&apikey={Uri.EscapeDataString(client.Settings?.TryGetValue("apiKey", out var v) is true ? v?.ToString() ?? string.Empty : string.Empty)}";
                    var resp = await http.GetAsync(url, ct);
                    if (!resp.IsSuccessStatusCode) return new List<QueueItem>();
                    var txt = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(txt);
                    var root = doc.RootElement;
                    var items = new List<QueueItem>();
                    if (root.TryGetProperty("queue", out var q) && q.TryGetProperty("slots", out var slots) && slots.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var s in slots.EnumerateArray())
                        {
                            var id = s.TryGetProperty("nzo_id", out var idEl) ? idEl.GetString() ?? Guid.NewGuid().ToString() : Guid.NewGuid().ToString();
                            var title = s.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "Unknown" : "Unknown";
                            items.Add(new QueueItem { Id = id, Title = title, DownloadClientId = client.Id });
                        }
                    }
                    return items;
                }
            }
            catch
            {
                // swallow and return conservative default
            }

            return new List<QueueItem>();
        }

        public async Task<List<(string Id, string Name)>> GetRecentHistoryAsync(DownloadClientConfiguration client, int limit = 100, CancellationToken ct = default)
        {
            var outList = new List<(string Id, string Name)>();
            try
            {
                if (string.Equals(client.Type, "sabnzbd", StringComparison.OrdinalIgnoreCase))
                {
                    var http = GetHttpClient();
                    var baseUrl = $"{(client.UseSSL ? "https" : "http")}://{client.Host}:{client.Port}/api";
                    var apiKey = client.Settings?.TryGetValue("apiKey", out var v) is true ? v?.ToString() ?? string.Empty : string.Empty;
                    var url = $"{baseUrl}?mode=history&output=json&limit={limit}&apikey={Uri.EscapeDataString(apiKey)}";
                    var resp = await http.GetAsync(url, ct);
                    if (!resp.IsSuccessStatusCode) return outList;
                    var txt = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(txt);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("history", out var history) && history.TryGetProperty("slots", out var slots) && slots.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var s in slots.EnumerateArray())
                        {
                            var nzo = s.TryGetProperty("nzo_id", out var nzoEl) ? nzoEl.GetString() ?? string.Empty : string.Empty;
                            var name = s.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                            outList.Add((nzo, name));
                        }
                    }
                }
            }
            catch
            {
                // ignore and return empty
            }

            return outList;
        }

        public Task<bool> MarkItemAsImportedAsync(DownloadClientConfiguration client, string downloadId, CancellationToken ct = default)
        {
            return Task.FromResult(true);
        }
    }
}
