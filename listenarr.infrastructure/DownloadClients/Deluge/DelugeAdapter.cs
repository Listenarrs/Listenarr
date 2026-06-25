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
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Listenarr.Infrastructure.Torrents;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Deluge
{
    /// <summary>
    /// Deluge Web JSON-RPC adapter, modelled after the Servarr Deluge integration.
    /// </summary>
    public class DelugeAdapter : IDownloadClientAdapter
    {
        public string ClientId => "deluge";
        public string ClientType => "deluge";
        public DownloadProtocol Protocol => DownloadProtocol.Torrent;

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<DelugeAdapter> _logger;

        private static readonly string[] StatusKeys =
        [
            "name", "total_size", "total_done", "progress", "download_payload_rate", "eta", "state",
            "save_path", "label", "ratio", "num_seeds", "num_peers", "time_added", "files", "message"
        ];

        public DelugeAdapter(IHttpClientFactory httpClientFactory, ITorrentFileDownloader torrentFileDownloader, ILogger<DelugeAdapter> logger)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _ = torrentFileDownloader ?? throw new ArgumentNullException(nameof(torrentFileDownloader));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            try
            {
                using var http = _httpClientFactory.CreateClient(ClientType);
                await AuthenticateAsync(http, client, ct);
                await EnsureDaemonConnectedAsync(http, client, ct);
                var connected = await RpcAsync(http, client, "web.connected", [], ct);
                if (connected.ValueKind == JsonValueKind.True)
                    return (true, "Deluge: connected to Web UI and daemon");
                return (false, "Deluge: unexpected JSON-RPC response");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogDebug(ex, "Deluge authentication failed for client {ClientId}", LogRedaction.SanitizeText(client?.Id ?? client?.Name ?? client?.Type));
                return (false, "Deluge: authentication failed (check Web UI password)");
            }
            catch (TaskCanceledException)
            {
                return (false, "Deluge: connection timed out");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Deluge test failed for client {ClientId}", LogRedaction.SanitizeText(client?.Id ?? client?.Name ?? client?.Type));
                return (false, $"Deluge: connection failed ({ex.Message})");
            }
        }

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

            using var http = _httpClientFactory.CreateClient(ClientType);
            await AuthenticateAsync(http, client, ct);
            await EnsureDaemonConnectedAsync(http, client, ct);

            var options = BuildTorrentOptions(client);
            string? id = null;

            if (torrent.TorrentBytes != null && torrent.TorrentBytes.Length > 0)
            {
                var filename = SanitizeTorrentFileName(torrent.FileName ?? torrent.Title) + ".torrent";
                var res = await RpcAsync(http, client, "core.add_torrent_file", [filename, Convert.ToBase64String(torrent.TorrentBytes), options], ct);
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
                    var res = await RpcAsync(http, client, "core.add_torrent_magnet", [uri, options], ct);
                    id = res.ValueKind == JsonValueKind.String ? res.GetString() : TryExtractHashFromMagnet(uri);
                }
                else
                {
                    var tempPath = await RpcAsync(http, client, "web.download_torrent_from_url", [uri], ct);
                    var path = tempPath.GetString();
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        var res = await RpcAsync(http, client, "web.add_torrents", [new[] { new { path, options } }], ct);
                        id = TryExtractAddedId(res);
                    }
                }
            }

            var category = GetSetting(client, "category");
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(category))
                await TrySetLabelAsync(http, client, id, category, ct);

            if (string.IsNullOrWhiteSpace(id))
            {
                throw new DownloadClientSubmissionException("Deluge did not return a verified torrent identifier.");
            }

            return new DownloadClientSubmissionResult(id, torrent.InfoHash);
        }

        public async Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default)
        {
            using var http = _httpClientFactory.CreateClient(ClientType);
            await AuthenticateAsync(http, client, ct);
            await EnsureDaemonConnectedAsync(http, client, ct);
            var res = await RpcAsync(http, client, "core.remove_torrent", [id, deleteFiles], ct);
            return res.ValueKind == JsonValueKind.True || res.ValueKind == JsonValueKind.Null || res.ValueKind == JsonValueKind.Undefined;
        }

        public async Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, CancellationToken ct = default)
            => (await GetItemsAsync(client, ct)).Select(ToQueueItem).ToList();

        public async Task<List<DownloadClientItem>> GetItemsAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            using var http = _httpClientFactory.CreateClient(ClientType);
            await AuthenticateAsync(http, client, ct);
            await EnsureDaemonConnectedAsync(http, client, ct);
            var res = await RpcAsync(http, client, "web.update_ui", [StatusKeys, new Dictionary<string, object>()], ct);
            var list = new List<DownloadClientItem>();
            var configuredCategory = DownloadClientCategoryFilter.GetConfiguredCategory(client);
            if (!res.TryGetProperty("torrents", out var torrents) || torrents.ValueKind != JsonValueKind.Object) return list;

            foreach (var torrent in torrents.EnumerateObject())
            {
                var t = torrent.Value;
                var label = GetString(t, "label");
                if (!DownloadClientCategoryFilter.Matches(configuredCategory, label)) continue;
                var total = GetLong(t, "total_size");
                var done = GetLong(t, "total_done");
                list.Add(new DownloadClientItem
                {
                    DownloadId = torrent.Name,
                    Title = GetString(t, "name"),
                    Category = label,
                    TotalSize = total,
                    RemainingSize = Math.Max(0, total - done),
                    OutputPath = BuildOutputPath(t),
                    Status = MapStatus(GetString(t, "state"), GetDouble(t, "progress")),
                    Message = GetString(t, "message"),
                    Progress = GetDouble(t, "progress"),
                    DownloadSpeed = GetDouble(t, "download_payload_rate"),
                    SeedRatio = GetDouble(t, "ratio"),
                    Seeders = (int)GetLong(t, "num_seeds"),
                    Leechers = (int)GetLong(t, "num_peers"),
                    AddedAt = FromUnix(GetDouble(t, "time_added")),
                    CanBeRemoved = true,
                    CanMoveFiles = false,
                    DownloadClientInfo = DownloadClientItemClientInfo.FromClient(client.Id, client.Name, client.Type, Protocol, client.RemoveCompletedDownloads != "none", false)
                });
            }
            return list;
        }

        public async Task<List<(string Id, string Name)>> GetRecentHistoryAsync(DownloadClientConfiguration client, int limit = 100, CancellationToken ct = default)
            => (await GetItemsAsync(client, ct)).Where(i => i.Status == DownloadItemStatus.Completed).Take(limit).Select(i => (i.DownloadId, i.Title)).ToList();

        public async Task<DownloadClientItem> GetImportItemAsync(DownloadClientConfiguration client, DownloadClientItem item, DownloadClientItem? previousAttempt = null, CancellationToken ct = default)
        {
            var current = (await GetItemsAsync(client, ct)).FirstOrDefault(i => string.Equals(i.DownloadId, item.DownloadId, StringComparison.OrdinalIgnoreCase));
            return current ?? item;
        }

        public async Task<QueueItem> GetImportItemAsync(DownloadClientConfiguration client, Download download, QueueItem queueItem, QueueItem? previousAttempt = null, CancellationToken ct = default)
        {
            var externalId = download.GetExternalId();
            var current = !string.IsNullOrWhiteSpace(externalId)
                ? (await GetQueueAsync(client, ct)).FirstOrDefault(i => string.Equals(i.Id, externalId, StringComparison.OrdinalIgnoreCase))
                : null;
            return current ?? queueItem;
        }

        public async Task<List<Download>> FetchDownloadsAsync(DownloadClientConfiguration client, List<Download> downloads, CancellationToken cancellationToken = default)
        {
            var items = await GetItemsAsync(client, cancellationToken);
            var byId = items.ToDictionary(i => i.DownloadId, StringComparer.OrdinalIgnoreCase);
            foreach (var d in downloads)
            {
                var externalId = d.GetExternalId();
                if (!string.IsNullOrWhiteSpace(externalId) && byId.TryGetValue(externalId, out var item))
                {
                    d.Progress = (decimal)item.Progress;
                    d.TotalSize = item.TotalSize;
                    d.DownloadedSize = Math.Max(0, item.TotalSize - item.RemainingSize);
                    d.DownloadPath = item.OutputPath;
                    if (d.Status is DownloadStatus.Moved or DownloadStatus.Processing or DownloadStatus.ImportPending)
                        continue;
                    if (item.Status == DownloadItemStatus.Completed) d.Status = DownloadStatus.Completed;
                    else if (item.Status == DownloadItemStatus.Failed) d.Status = DownloadStatus.Failed;
                    else if (item.Status == DownloadItemStatus.Paused) d.Status = DownloadStatus.Paused;
                    else d.Status = DownloadStatus.Downloading;
                }
            }
            return downloads;
        }

        private static string BuildBaseUrl(DownloadClientConfiguration client)
        {
            var scheme = client.UseSSL ? "https" : "http";
            var host = client.Host.Trim().TrimEnd('/');
            if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                host = new Uri(host).Authority;
            var urlBase = GetSetting(client, "urlBase") ?? GetSetting(client, "UrlBase") ?? string.Empty;
            urlBase = urlBase.Trim('/');
            return string.IsNullOrWhiteSpace(urlBase) ? $"{scheme}://{host}:{client.Port}/json" : $"{scheme}://{host}:{client.Port}/{urlBase}/json";
        }

        private async Task AuthenticateAsync(HttpClient http, DownloadClientConfiguration client, CancellationToken ct)
        {
            var res = await RpcAsync(http, client, "auth.login", [client.Password ?? string.Empty], ct);
            if (res.ValueKind != JsonValueKind.True) throw new UnauthorizedAccessException("Failed to authenticate with Deluge Web UI");
        }

        private async Task EnsureDaemonConnectedAsync(HttpClient http, DownloadClientConfiguration client, CancellationToken ct)
        {
            var connected = await RpcAsync(http, client, "web.connected", [], ct);
            if (connected.ValueKind == JsonValueKind.True) return;

            var hosts = await RpcAsync(http, client, "web.get_hosts", [], ct);
            if (hosts.ValueKind != JsonValueKind.Array || hosts.GetArrayLength() == 0)
                throw new InvalidOperationException("Deluge Web is not connected to a daemon and no daemon hosts are configured");

            string? hostId = null;
            foreach (var host in hosts.EnumerateArray())
            {
                if (host.ValueKind == JsonValueKind.Array && host.GetArrayLength() > 0)
                {
                    hostId = host[0].GetString();
                    if (!string.IsNullOrWhiteSpace(hostId)) break;
                }
            }

            if (string.IsNullOrWhiteSpace(hostId))
                throw new InvalidOperationException("Deluge Web returned no daemon host id");

            await RpcAsync(http, client, "web.connect", [hostId], ct);
        }

        private async Task<JsonElement> RpcAsync(HttpClient http, DownloadClientConfiguration client, string method, object[] parameters, CancellationToken ct)
        {
            var payload = JsonSerializer.Serialize(new { method, @params = parameters, id = 1 });
            using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(payload));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await http.PostAsync(BuildBaseUrl(client), content, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden) throw new UnauthorizedAccessException("Deluge rejected the request");
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                throw new InvalidOperationException($"Deluge JSON-RPC error calling {method}: {error}");
            if (doc.RootElement.TryGetProperty("result", out var result)) return result.Clone();
            return default;
        }

        private static Dictionary<string, object> BuildTorrentOptions(DownloadClientConfiguration client)
        {
            var options = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(client.DownloadPath)) options["download_location"] = client.DownloadPath;
            var addPaused = GetSetting(client, "addPaused") ?? GetSetting(client, "AddPaused");
            if (bool.TryParse(addPaused, out var paused)) options["add_paused"] = paused;
            return options;
        }

        private async Task TrySetLabelAsync(HttpClient http, DownloadClientConfiguration client, string id, string label, CancellationToken ct)
        {
            try { await RpcAsync(http, client, "label.set_torrent", [id, label], ct); }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            { _logger.LogDebug(ex, "Unable to set Deluge label/category. Is the Label plugin enabled?"); }
        }

        private static QueueItem ToQueueItem(DownloadClientItem item) => new()
        {
            Id = item.DownloadId,
            Title = item.Title,
            Status = item.Status.ToString().ToLowerInvariant(),
            Progress = item.Progress,
            Size = item.TotalSize,
            Downloaded = Math.Max(0, item.TotalSize - item.RemainingSize),
            DownloadSpeed = item.DownloadSpeed,
            Eta = item.RemainingTime.HasValue ? (int)item.RemainingTime.Value.TotalSeconds : null,
            DownloadClient = item.DownloadClientInfo.Name,
            DownloadClientId = item.DownloadClientInfo.Id,
            DownloadClientType = item.DownloadClientInfo.Type,
            AddedAt = item.AddedAt,
            ErrorMessage = item.Message,
            Seeders = item.Seeders,
            Leechers = item.Leechers,
            Ratio = item.SeedRatio,
            RemotePath = item.OutputPath,
            ContentPath = item.OutputPath,
            CanRemove = item.CanBeRemoved
        };

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

        private static string BuildOutputPath(JsonElement t)
        {
            var savePath = GetString(t, "save_path").TrimEnd('/', '\\');
            var name = GetString(t, "name");
            if (string.IsNullOrWhiteSpace(savePath)) return string.Empty;
            return string.IsNullOrWhiteSpace(name) ? savePath : Path.Combine(savePath, name);
        }
        private static string? GetSetting(DownloadClientConfiguration c, string key) => c.Settings != null && c.Settings.TryGetValue(key, out var v) ? v?.ToString() : null;
        private static string GetString(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        private static long GetLong(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.TryGetInt64(out var l) ? l : 0;
        private static double GetDouble(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.TryGetDouble(out var d) ? d : 0;
        private static DateTime FromUnix(double seconds) => seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)seconds).UtcDateTime : DateTime.UtcNow;
        private static string? NormalizeTorrentUrl(string? url) => string.IsNullOrWhiteSpace(url) ? null : url.Trim();
        private static string SanitizeTorrentFileName(string? title) => string.Join("_", (title ?? "listenarr").Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        private static string? TryExtractHashFromMagnet(string magnet) { var m = System.Text.RegularExpressions.Regex.Match(magnet, @"btih:([A-Fa-f0-9]{40})"); return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null; }
        private static string? TryExtractAddedId(JsonElement res) => res.ValueKind == JsonValueKind.Array && res.GetArrayLength() > 0 ? res[0].GetString() : null;
    }
}
