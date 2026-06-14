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
using System.Text.Encodings.Web;
using System.Text.Json;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Security;
using Listenarr.Domain.Common;
using Listenarr.Domain.Models;
using Listenarr.Domain.Models.Exceptions;
using Listenarr.Infrastructure.Torrents;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Adapters
{
    public class TransmissionAdapter : IDownloadClientAdapter
    {
        public string ClientId => "transmission";
        public string ClientType => "transmission";
        public DownloadProtocol Protocol => DownloadProtocol.Torrent;

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ITorrentFileDownloader _torrentFileDownloader;
        private readonly ILogger<TransmissionAdapter> _logger;
        private readonly TransmissionTorrentAddPlanner _torrentAddPlanner;
        private readonly TransmissionRpcClient _rpcClient;

        public TransmissionAdapter(IHttpClientFactory httpClientFactory, ITorrentFileDownloader torrentFileDownloader, ILogger<TransmissionAdapter> logger)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _torrentFileDownloader = torrentFileDownloader ?? throw new ArgumentNullException(nameof(torrentFileDownloader));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _torrentAddPlanner = new TransmissionTorrentAddPlanner(_torrentFileDownloader, _logger);
            _rpcClient = new TransmissionRpcClient(_httpClientFactory, ClientType, _logger);
        }

        public async Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            try
            {
                // Use old format for compatibility with Transmission < 4.1.0
                var payload = new
                {
                    method = "session-get",
                    arguments = new { },
                    tag = 1
                };
                var response = await _rpcClient.InvokeAsync(client, payload, ct);

                // Validate that the RPC endpoint actually responded with a successful session-get.
                // Without this check, a non-Transmission service on the same port (or Transmission's
                // web UI returning HTML) would falsely pass the test.
                if (!response.TryGetProperty("result", out var resultProp) ||
                    !string.Equals(resultProp.GetString(), "success", StringComparison.OrdinalIgnoreCase))
                {
                    var hint = resultProp.ValueKind == JsonValueKind.String ? resultProp.GetString() : "unexpected response";
                    return (false, $"Transmission: RPC endpoint did not return a valid session response ({hint})");
                }

                return (true, "Transmission: connected");
            }
            catch (HttpRequestException httpEx) when (httpEx.StatusCode == HttpStatusCode.Unauthorized || httpEx.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.LogDebug(httpEx, "Transmission authentication failed for client {ClientId}", LogRedaction.SanitizeText(client?.Id ?? client?.Name ?? client?.Type));
                return (false, "Transmission: authentication failed (check username/password)");
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogDebug(httpEx, "Transmission test failed for client {ClientId}", LogRedaction.SanitizeText(client?.Id ?? client?.Name ?? client?.Type));
                return (false, $"Transmission: network error ({httpEx.StatusCode?.ToString() ?? "unavailable"})");
            }
            catch (TaskCanceledException tce)
            {
                _logger.LogDebug(tce, "Transmission test timed out for client {ClientId}", LogRedaction.SanitizeText(client?.Id ?? client?.Name ?? client?.Type));
                return (false, "Transmission: connection timed out");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Transmission test failed for client {ClientId}", LogRedaction.SanitizeText(client?.Id ?? client?.Name ?? client?.Type));
                return (false, "Transmission: connection failed");
            }
        }

        public async Task<string?> AddAsync(DownloadClientConfiguration client, SearchResult result, CancellationToken ct = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (result == null) throw new ArgumentNullException(nameof(result));

            var labels = TransmissionRequestPlanner.CollectLabels(client);
            var arguments = await _torrentAddPlanner.BuildArgumentsAsync(client, result, labels, ct);

            // Use old format for compatibility with Transmission < 4.1.0
            var payload = new
            {
                method = "torrent-add",
                arguments,
                tag = 1
            };

            try
            {
                var response = await _rpcClient.InvokeAsync(client, payload, ct);

                // Log the full response for debugging
                _logger.LogDebug("Transmission add torrent response: {Response}", response.GetRawText());

                // Check result field
                if (!response.TryGetProperty("result", out var resultProp) || !string.Equals(resultProp.GetString(), "success", StringComparison.OrdinalIgnoreCase))
                {
                    var errorMsg = resultProp.ValueKind == JsonValueKind.String ? resultProp.GetString() : "Unknown error";
                    throw new Exception($"Transmission RPC error: {errorMsg}");
                }

                if (response.TryGetProperty("arguments", out var args))
                {
                    if (args.TryGetProperty("torrent-added", out var added) && added.ValueKind == JsonValueKind.Object)
                    {
                        var torrentId = TransmissionRequestPlanner.ExtractTorrentIdentifier(added);
                        _logger.LogInformation("Transmission successfully added torrent '{Title}' with id/hash: {Id}", LogRedaction.SanitizeText(result.Title), LogRedaction.SanitizeText(torrentId));
                        return torrentId;
                    }

                    if (args.TryGetProperty("torrent-duplicate", out var duplicate) && duplicate.ValueKind == JsonValueKind.Object)
                    {
                        var existingId = TransmissionRequestPlanner.ExtractTorrentIdentifier(duplicate);
                        _logger.LogInformation("Transmission reported duplicate torrent for '{Title}' with id/hash {Id}", LogRedaction.SanitizeText(result.Title), LogRedaction.SanitizeText(existingId));
                        return existingId;
                    }
                }

                _logger.LogWarning("Transmission AddAsync returning null - torrent may not have been added");
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to add torrent to Transmission for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
                throw;
            }
        }

        public async Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            var idsPayload = TransmissionRequestPlanner.ParseTransmissionIds(id);
            var arguments = new Dictionary<string, object>
            {
                ["ids"] = idsPayload,
                ["delete-local-data"] = deleteFiles
            };

            // Use old format for compatibility with Transmission < 4.1.0
            var payload = new
            {
                method = "torrent-remove",
                arguments,
                tag = 2
            };

            try
            {
                var response = await _rpcClient.InvokeAsync(client, payload, ct);
                if (response.TryGetProperty("result", out var resultProp) && string.Equals(resultProp.GetString(), "success", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Removed torrent {Id} from Transmission (deleteFiles={DeleteFiles})", LogRedaction.SanitizeText(id), deleteFiles);
                    return true;
                }

                var errorMsg = resultProp.ValueKind == JsonValueKind.String ? resultProp.GetString() ?? "Unknown error" : "Unknown error";
                _logger.LogWarning("Transmission failed to remove torrent {Id}: {Message}", LogRedaction.SanitizeText(id), LogRedaction.SanitizeText(errorMsg));
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error removing torrent {Id} from Transmission", LogRedaction.SanitizeText(id));
                return false;
            }
        }

        public async Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var items = new List<QueueItem>();
            if (client == null) return items;

            var configuredCategory = DownloadClientCategoryFilter.GetConfiguredCategory(client);

            // Use old format for compatibility with Transmission < 4.1.0
            var payload = new
            {
                method = "torrent-get",
                arguments = new
                {
                    fields = new[]
                    {
                        "id", "hashString", "name", "percentDone", "status", "totalSize", "rateDownload", "rateUpload",
                        "leftUntilDone", "eta", "downloadDir", "addedDate", "uploadedEver", "uploadRatio", "labels"
                    }
                },
                tag = 3
            };

            try
            {
                var response = await _rpcClient.InvokeAsync(client, payload, ct);
                if (!response.TryGetProperty("arguments", out var args) || !args.TryGetProperty("torrents", out var torrents) || torrents.ValueKind != JsonValueKind.Array)
                {
                    return items;
                }

                foreach (var torrent in torrents.EnumerateArray())
                {
                    try
                    {
                        var labels = TransmissionResponseMapper.ExtractLabels(torrent);
                        if (!DownloadClientCategoryFilter.MatchesAny(configuredCategory, labels))
                        {
                            continue;
                        }

                        var queueItem = TransmissionResponseMapper.MapQueueItem(client, torrent);
                        items.Add(queueItem);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogDebug(ex, "Failed to map Transmission torrent entry (non-fatal)");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to retrieve Transmission queue for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
            }

            return items;
        }

        public Task<List<(string Id, string Name)>> GetRecentHistoryAsync(DownloadClientConfiguration client, int limit = 100, CancellationToken ct = default)
        {
            // Transmission does not expose a dedicated history endpoint via RPC.
            return Task.FromResult(new List<(string Id, string Name)>());
        }

        /// <summary>
        /// Get all downloads as standardized DownloadClientItem objects
        /// </summary>
        public async Task<List<DownloadClientItem>> GetItemsAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var items = new List<DownloadClientItem>();
            if (client == null) return items;

            var configuredCategory = DownloadClientCategoryFilter.GetConfiguredCategory(client);

            // Fetch session-level seed config for Sonarr-parity seed limit evaluation
            bool sessionSeedRatioLimited = false;
            double sessionSeedRatioLimit = 0;
            bool sessionIdleSeedingLimitEnabled = false;
            int sessionIdleSeedingLimit = 0;
            try
            {
                var sessionPayload = new { method = "session-get", arguments = new { }, tag = 99 };
                var sessionResp = await _rpcClient.InvokeAsync(client, sessionPayload, ct);
                if (sessionResp.TryGetProperty("arguments", out var sessionArgs))
                {
                    sessionSeedRatioLimited = (sessionArgs.TryGetProperty("seedRatioLimited", out var srl) || sessionArgs.TryGetProperty("seed_ratio_limited", out srl)) && srl.GetBoolean();
                    sessionSeedRatioLimit = (sessionArgs.TryGetProperty("seedRatioLimit", out var srlv) || sessionArgs.TryGetProperty("seed_ratio_limit", out srlv)) ? srlv.GetDouble() : 0;
                    sessionIdleSeedingLimitEnabled = (sessionArgs.TryGetProperty("idle-seeding-limit-enabled", out var isle) || sessionArgs.TryGetProperty("idle_seeding_limit_enabled", out isle)) && isle.GetBoolean();
                    sessionIdleSeedingLimit = (sessionArgs.TryGetProperty("idle-seeding-limit", out var isl) || sessionArgs.TryGetProperty("idle_seeding_limit", out isl)) ? isl.GetInt32() : 0;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to fetch Transmission session config for seed limit evaluation, will use conservative defaults");
            }

            var sessionConfig = (sessionSeedRatioLimited, sessionSeedRatioLimit, sessionIdleSeedingLimitEnabled, sessionIdleSeedingLimit);

            var payload = new
            {
                method = "torrent-get",
                arguments = new
                {
                    fields = new[]
                    {
                        "id", "hashString", "name", "percentDone", "status", "totalSize", "rateDownload", "rateUpload",
                        "leftUntilDone", "eta", "downloadDir", "addedDate", "uploadedEver", "uploadRatio", "labels",
                        "seedRatioMode", "seedRatioLimit", "seedIdleMode", "seedIdleLimit", "secondsSeeding"
                    }
                },
                tag = 3
            };

            try
            {
                var response = await _rpcClient.InvokeAsync(client, payload, ct);
                if (!response.TryGetProperty("arguments", out var args) || !args.TryGetProperty("torrents", out var torrents) || torrents.ValueKind != JsonValueKind.Array)
                {
                    return items;
                }

                foreach (var torrent in torrents.EnumerateArray())
                {
                    try
                    {
                        var labels = TransmissionResponseMapper.ExtractLabels(torrent);
                        if (!DownloadClientCategoryFilter.MatchesAny(configuredCategory, labels))
                        {
                            continue;
                        }

                        var downloadClientItem = await MapToDownloadClientItemAsync(client, torrent, sessionConfig, ct);
                        items.Add(downloadClientItem);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogDebug(ex, "Failed to map Transmission torrent entry (non-fatal)");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to retrieve Transmission items for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
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
            // Clone to avoid mutating the original
            var result = item.Clone();

            // If OutputPath is already set and exists, use it
            if (!string.IsNullOrEmpty(result.OutputPath))
            {
                var localPath = result.OutputPath;
                if (TransmissionImportPathResolver.IsExistingLocalPath(localPath))
                {
                    result.OutputPath = localPath;
                    return result;
                }
            }

            // Query Transmission for the torrent details
            var payload = new
            {
                method = "torrent-get",
                arguments = new
                {
                    ids = TransmissionRequestPlanner.ParseTransmissionIds(item.DownloadId),
                    fields = new[] { "id", "name", "downloadDir" }
                },
                tag = 5
            };

            try
            {
                var response = await _rpcClient.InvokeAsync(client, payload, ct);
                if (!response.TryGetProperty("arguments", out var args) ||
                    !args.TryGetProperty("torrents", out var torrents) ||
                    torrents.ValueKind != JsonValueKind.Array)
                {
                    _logger.LogWarning("Failed to query Transmission for torrent {TorrentId}", item.DownloadId);
                    return result;
                }

                var torrent = torrents.EnumerateArray().FirstOrDefault();
                if (torrent.ValueKind == JsonValueKind.Undefined)
                {
                    _logger.LogWarning("Torrent {TorrentId} not found in Transmission", item.DownloadId);
                    return result;
                }

                var downloadDir = torrent.TryGetProperty("downloadDir", out var dirProp) ? dirProp.GetString() : null;
                var name = torrent.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;

                if (string.IsNullOrEmpty(downloadDir) || string.IsNullOrEmpty(name))
                {
                    _logger.LogWarning("Missing downloadDir or name for torrent {TorrentId}", item.DownloadId);
                    return result;
                }

                // Transmission stores files as: downloadDir/name.
                var contentPath = TransmissionImportPathResolver.BuildContentPath(downloadDir, name)!;

                // Apply path mapping
                // FIXME: Path mapping should be the responsability of the download processors
                var localContentPath = contentPath;
                result.OutputPath = localContentPath;

                _logger.LogDebug(
                    "Resolved Transmission content path for {TorrentId}: {ContentPath}",
                    item.DownloadId,
                    localContentPath);

                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Error resolving import item for Transmission torrent {TorrentId}", item.DownloadId);
                return result;
            }
        }

        /// <summary>
        /// LEGACY: Resolves the actual import item for a completed download.
        /// Queries Transmission API for downloadDir and builds the content path.
        /// Matches Transmission.GetImportItem pattern.
        /// </summary>
        public async Task<QueueItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            Download download,
            QueueItem queueItem,
            QueueItem? previousAttempt = null,
            CancellationToken ct = default)
        {
            // Clone to avoid mutating the original
            var result = queueItem.Clone();
            string? resolvedExistingContentPath = null;

            // If ContentPath is already set and exists, use it
            if (!string.IsNullOrEmpty(result.ContentPath))
            {
                var localPath = result.ContentPath;
                if (TransmissionImportPathResolver.IsExistingLocalPath(localPath))
                {
                    result.ContentPath = localPath;
                    resolvedExistingContentPath = localPath;
                }
            }

            // Query Transmission for the torrent details
            var payload = new
            {
                method = "torrent-get",
                arguments = new
                {
                    ids = TransmissionRequestPlanner.ParseTransmissionIds(queueItem.Id),
                    fields = new[] { "id", "name", "downloadDir", "files" }
                },
                tag = 5
            };

            try
            {
                var response = await _rpcClient.InvokeAsync(client, payload, ct);
                if (!response.TryGetProperty("arguments", out var args) ||
                    !args.TryGetProperty("torrents", out var torrents) ||
                    torrents.ValueKind != JsonValueKind.Array)
                {
                    _logger.LogWarning("Failed to query Transmission for torrent {TorrentId}", queueItem.Id);
                    return result;
                }

                var torrent = torrents.EnumerateArray().FirstOrDefault();
                if (torrent.ValueKind == JsonValueKind.Undefined)
                {
                    _logger.LogWarning("Torrent {TorrentId} not found in Transmission", queueItem.Id);
                    return result;
                }

                var downloadDir = torrent.TryGetProperty("downloadDir", out var dirProp) ? dirProp.GetString() : null;
                var name = torrent.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;

                if ((string.IsNullOrEmpty(downloadDir) || string.IsNullOrEmpty(name)) && string.IsNullOrWhiteSpace(resolvedExistingContentPath))
                {
                    _logger.LogWarning("Missing downloadDir or name for torrent {TorrentId}", queueItem.Id);
                    return result;
                }

                // Transmission stores files as: downloadDir/name
                var contentPath = TransmissionImportPathResolver.BuildContentPath(downloadDir, name, resolvedExistingContentPath);
                string? localContentPath = resolvedExistingContentPath;
                if (!string.IsNullOrWhiteSpace(contentPath))
                {
                    localContentPath = contentPath;
                    result.ContentPath = localContentPath;
                }

                if (torrent.TryGetProperty("files", out var filesElement))
                {
                    result.SourceFiles = TransmissionImportPathResolver.BuildSourceFiles(downloadDir, filesElement);
                }

                _logger.LogDebug(
                    "Resolved Transmission content path for {TorrentId}: {ContentPath}",
                    queueItem.Id,
                    localContentPath);

                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Error resolving import item for Transmission torrent {TorrentId}", queueItem.Id);
                return result;
            }
        }

        private Task<DownloadClientItem> MapToDownloadClientItemAsync(
            DownloadClientConfiguration client,
            JsonElement torrent,
            (bool SeedRatioLimited, double SeedRatioLimit, bool IdleSeedingLimitEnabled, int IdleSeedingLimit) sessionConfig,
            CancellationToken ct)
        {
            _ = ct;
            return Task.FromResult(TransmissionResponseMapper.MapDownloadClientItem(client, torrent, sessionConfig));
        }

        private async Task<byte[]?> PreDownloadTorrentFileAsync(string torrentUrl, CancellationToken ct)
        {
            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            downloadCts.CancelAfter(TimeSpan.FromSeconds(60));

            // Use a dedicated handler with redirects disabled so we can follow them manually
            using var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = false
            };
            using var httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(60)
            };

            var currentUrl = torrentUrl;
            for (var hop = 0; hop < 10; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, currentUrl);
                request.Headers.Accept.ParseAdd("application/x-bittorrent, application/octet-stream, */*");
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

                var response = await httpClient.SendAsync(request, downloadCts.Token);

                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
                    or HttpStatusCode.SeeOther)
                {
                    var location = response.Headers.Location;
                    if (location == null)
                    {
                        _logger.LogWarning("Pre-download got {StatusCode} with no Location header from {Url}",
                            response.StatusCode, LogRedaction.SanitizeUrl(currentUrl));
                        return null;
                    }

                    // Resolve relative redirects
                    var nextUri = location.IsAbsoluteUri ? location : new Uri(new Uri(currentUrl), location);
                    _logger.LogDebug("Pre-download following {StatusCode} redirect: {From} → {To}",
                        response.StatusCode, LogRedaction.SanitizeUrl(currentUrl), LogRedaction.SanitizeUrl(nextUri.ToString()));
                    currentUrl = nextUri.ToString();
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Pre-download failed ({StatusCode}) from {Url}",
                        response.StatusCode, LogRedaction.SanitizeUrl(currentUrl));
                    return null;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(downloadCts.Token);
                _logger.LogDebug("Pre-download fetched {Bytes} bytes from {Url} (hops: {Hops})",
                    bytes.Length, LogRedaction.SanitizeUrl(currentUrl), hop);
                return bytes;
            }

            _logger.LogWarning("Pre-download exceeded maximum redirects (10) starting from {Url}", LogRedaction.SanitizeUrl(torrentUrl));
            return null;
        }

        public async Task<List<Download>> FetchDownloadsAsync(
            DownloadClientConfiguration client,
            List<Download> downloads,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("Polling Transmission client {ClientName} for {Count} downloads", client.Name, downloads.Count);
            try
            {
                var rpcPath = "/transmission/rpc";
                if (client.Settings?.TryGetValue("urlBase", out var urlBaseObj) is true)
                {
                    var custom = urlBaseObj?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(custom))
                    {
                        rpcPath = custom.StartsWith('/') ? custom : "/" + custom;
                    }
                }
                var baseUrl = DownloadClientUriBuilder.BuildUri(client, rpcPath).ToString();
                using var http = _httpClientFactory.CreateClient(ClientType);

                // Resolve removeCompletedDownloads for CanMoveFiles/CanBeRemoved evaluation
                bool txRemoveCompletedDownloads = !string.IsNullOrEmpty(client.RemoveCompletedDownloads) &&
                    client.RemoveCompletedDownloads != "none";

                // Prepare RPC payload for torrent-get (includes seed limit fields for Sonarr parity)
                var rpc = new
                {
                    method = "torrent-get",
                    arguments = new
                    {
                        fields = new[] { "id", "hashString", "name", "percentDone", "leftUntilDone", "isFinished", "status", "downloadDir",
                            "uploadRatio", "seedRatioMode", "seedRatioLimit", "seedIdleMode", "seedIdleLimit", "secondsSeeding" }
                    },
                    tag = 4
                };

                var serializedPayload = System.Text.Json.JsonSerializer.Serialize(rpc, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                string? sessionId = null;

                _logger.LogDebug("PollTransmission RPC request to {BaseUrl}", baseUrl);

                // Transmission CSRF protection: first request gets 409 with session-id, retry with that session-id
                // This mirrors TransmissionAdapter.InvokeRpcAsync pattern
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl)
                    {
                        Content = new StringContent(serializedPayload, System.Text.Encoding.UTF8, "application/json")
                    };

                    // Add session-id header if we have one (from previous 409 retry)
                    if (!string.IsNullOrEmpty(sessionId))
                    {
                        request.Headers.Add("X-Transmission-Session-Id", sessionId);
                        _logger.LogDebug("PollTransmission using X-Transmission-Session-Id: {SessionId}", sessionId);
                    }

                    // Add Basic auth header if configured
                    if (!string.IsNullOrWhiteSpace(client.Username))
                    {
                        var credentials = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{client.Username}:{client.Password}"));
                        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
                    }

                    var resp = await http.SendAsync(request, cancellationToken);
                    var respText = await resp.Content.ReadAsStringAsync(cancellationToken);

                    // Handle 409 Conflict (CSRF session-id flow)
                    if (resp.StatusCode == System.Net.HttpStatusCode.Conflict && attempt == 0)
                    {
                        if (resp.Headers.TryGetValues("X-Transmission-Session-Id", out var values))
                        {
                            sessionId = values.FirstOrDefault();
                            _logger.LogDebug("PollTransmission received 409 Conflict, retrying with session-id: {SessionId}", sessionId);
                            continue; // Retry with session-id
                        }
                    }

                    // Check for success
                    _logger.LogInformation("PollTransmission HTTP response: {StatusCode}", resp.StatusCode);
                    if (!resp.IsSuccessStatusCode)
                    {
                        throw new DownloadClientAdapterPollingException($"PollTransmission early-return: non-success HTTP status {resp.StatusCode} from {baseUrl} for client {client.Id}");
                    }

                    // Process successful response
                    _logger.LogDebug("PollTransmission response text length: {Length}", respText?.Length ?? 0);
                    if (string.IsNullOrWhiteSpace(respText))
                    {
                        throw new DownloadClientAdapterPollingException($"PollTransmission early-return: empty response content for client {client.Id}");
                    }

                    // Parse response and continue with torrent processing
                    JsonElement doc;
                    try
                    {
                        doc = JsonSerializer.Deserialize<JsonElement>(respText)!;
                    }
                    catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                    {
                        throw new DownloadClientAdapterPollingException($"PollTransmission failed to parse JSON response for client {client.Id}", exception);
                    }

                    if (!doc.TryGetProperty("arguments", out var args))
                    {
                        throw new DownloadClientAdapterPollingException($"PollTransmission early-return: missing 'arguments' in response for client {client.Id}");
                    }
                    if (!args.TryGetProperty("torrents", out var torrents))
                    {
                        throw new DownloadClientAdapterPollingException($"PollTransmission early-return: missing 'torrents' in 'arguments' for client {client.Id}");
                    }
                    if (torrents.ValueKind != JsonValueKind.Array)
                    {
                        throw new DownloadClientAdapterPollingException($"PollTransmission early-return: 'torrents' not an array (Kind={torrents.ValueKind}) for client {client.Id}");
                    }
                    _logger.LogInformation("PollTransmission found {Count} torrents in response", torrents.GetArrayLength());

                    // Fetch session config for seed limit evaluation (Sonarr parity)
                    bool txSessionSeedRatioLimited = false;
                    double txSessionSeedRatioLimit = 0;
                    bool txSessionIdleSeedingLimitEnabled = false;
                    int txSessionIdleSeedingLimit = 0;
                    try
                    {
                        var sessionPayload = System.Text.Json.JsonSerializer.Serialize(new { method = "session-get", arguments = new { }, tag = 99 }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                        using var sessionReq = new HttpRequestMessage(HttpMethod.Post, baseUrl)
                        {
                            Content = new StringContent(sessionPayload, System.Text.Encoding.UTF8, "application/json")
                        };
                        if (!string.IsNullOrEmpty(sessionId))
                            sessionReq.Headers.Add("X-Transmission-Session-Id", sessionId);
                        if (!string.IsNullOrWhiteSpace(client.Username))
                        {
                            var creds = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{client.Username}:{client.Password}"));
                            sessionReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", creds);
                        }
                        using var sessionResp = await http.SendAsync(sessionReq, cancellationToken);
                        if (sessionResp.IsSuccessStatusCode)
                        {
                            var sessionText = await sessionResp.Content.ReadAsStringAsync(cancellationToken);
                            var sessionDoc = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(sessionText);
                            if (sessionDoc.TryGetProperty("arguments", out var sessionArgs))
                            {
                                txSessionSeedRatioLimited = (sessionArgs.TryGetProperty("seedRatioLimited", out var srl) || sessionArgs.TryGetProperty("seed_ratio_limited", out srl)) && srl.GetBoolean();
                                txSessionSeedRatioLimit = (sessionArgs.TryGetProperty("seedRatioLimit", out var srlv) || sessionArgs.TryGetProperty("seed_ratio_limit", out srlv)) ? srlv.GetDouble() : 0;
                                txSessionIdleSeedingLimitEnabled = (sessionArgs.TryGetProperty("idle-seeding-limit-enabled", out var isle) || sessionArgs.TryGetProperty("idle_seeding_limit_enabled", out isle)) && isle.GetBoolean();
                                txSessionIdleSeedingLimit = (sessionArgs.TryGetProperty("idle-seeding-limit", out var isl) || sessionArgs.TryGetProperty("idle_seeding_limit", out isl)) ? isl.GetInt32() : 0;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogDebug(ex, "Failed to fetch Transmission session config for seed limit evaluation");
                    }

                    // Process torrents (continue with existing logic below)
                    foreach (var dl in downloads)
                    {
                        try
                        {
                            // Attempt to match by hashString (preferred) or name
                            var matching = torrents.EnumerateArray().FirstOrDefault(t =>
                            {
                                // First try matching by hash (most reliable)
                                if (dl.Metadata != null && dl.Metadata.TryGetValue("TorrentHash", out var hashObj))
                                {
                                    var downloadHash = hashObj?.ToString() ?? string.Empty;
                                    if (!string.IsNullOrEmpty(downloadHash))
                                    {
                                        var hash = t.TryGetProperty("hashString", out var h) ? h.GetString() ?? string.Empty : string.Empty;
                                        if (string.Equals(hash, downloadHash, StringComparison.OrdinalIgnoreCase))
                                            return true;
                                    }
                                }

                                // Fallback to exact name or normalized title match only.
                                // No fuzzy/path-based matching to avoid cross-contamination.
                                var name = t.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                                if (string.Equals(name, dl.Title, StringComparison.OrdinalIgnoreCase))
                                    return true;
                                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(dl.Title) &&
                                    string.Equals(TitleUtils.NormalizeTitle(name), TitleUtils.NormalizeTitle(dl.Title), StringComparison.OrdinalIgnoreCase))
                                    return true;
                                return false;
                            });

                            if (matching.ValueKind == System.Text.Json.JsonValueKind.Undefined)
                            {
                                _logger.LogDebug("Could not find matching torrent for download {DownloadId} ({Title}) in Transmission", dl.Id, dl.Title);
                                continue;
                            }

                            _logger.LogDebug("Matched download {DownloadId} to Transmission torrent", dl.Id);

                            var percent = matching.TryGetProperty("percentDone", out var p) ? p.GetDouble() : 0.0;
                            var left = matching.TryGetProperty("leftUntilDone", out var l) ? l.GetInt64() : 0L;
                            var statusCode = matching.TryGetProperty("status", out var statusProp) ? statusProp.GetInt32() : 0;

                            // Map Transmission status code to status string (same as TransmissionAdapter)
                            var status = statusCode switch
                            {
                                0 => "paused",          // TR_STATUS_STOPPED
                                1 => "queued",          // TR_STATUS_CHECK_WAIT
                                2 => "downloading",     // TR_STATUS_CHECK
                                3 => "queued",          // TR_STATUS_DOWNLOAD_WAIT
                                4 => "downloading",     // TR_STATUS_DOWNLOAD
                                5 => "queued",          // TR_STATUS_SEED_WAIT
                                6 => "seeding",         // TR_STATUS_SEED
                                7 => "failed",          // TR_STATUS_ISOLATED
                                _ => "unknown"
                            };

                            AdapterUtils.MapDownloadProgress(dl, percent * 100, left, status);

                            // Compute and persist CanMoveFiles/CanBeRemoved (Sonarr parity)
                            try
                            {
                                var txUploadRatio = (matching.TryGetProperty("uploadRatio", out var txRatP) || matching.TryGetProperty("upload_ratio", out txRatP)) ? txRatP.GetDouble() : 0d;
                                var txSeedRatioMode = (matching.TryGetProperty("seedRatioMode", out var txSrmP) || matching.TryGetProperty("seed_ratio_mode", out txSrmP)) ? txSrmP.GetInt32() : 0;
                                var txSeedRatioLimit = (matching.TryGetProperty("seedRatioLimit", out var txSrlP) || matching.TryGetProperty("seed_ratio_limit", out txSrlP)) ? txSrlP.GetDouble() : 0d;
                                var txSeedIdleMode = (matching.TryGetProperty("seedIdleMode", out var txSimP) || matching.TryGetProperty("seed_idle_mode", out txSimP)) ? txSimP.GetInt32() : 0;
                                var txSeedIdleLimit = (matching.TryGetProperty("seedIdleLimit", out var txSilP) || matching.TryGetProperty("seed_idle_limit", out txSilP)) ? txSilP.GetInt32() : 0;
                                var txSecondsSeeding = (matching.TryGetProperty("secondsSeeding", out var txSsP) || matching.TryGetProperty("seconds_seeding", out txSsP)) ? txSsP.GetInt64() : 0L;

                                var txIsStopped = statusCode == 0;
                                var txIsSeeding = statusCode == 6;
                                var txSeedLimitReached = TransmissionSeedLimitEvaluator.HasReachedSeedLimit(
                                    txIsStopped, txIsSeeding, txUploadRatio,
                                    txSeedRatioMode, txSeedRatioLimit,
                                    txSeedIdleMode, txSeedIdleLimit, txSecondsSeeding,
                                    txSessionSeedRatioLimited, txSessionSeedRatioLimit,
                                    txSessionIdleSeedingLimitEnabled, txSessionIdleSeedingLimit);
                                var txCanBeRemoved = txRemoveCompletedDownloads && txSeedLimitReached;
                                var txCanMoveFiles = txCanBeRemoved && txIsStopped;

                                if (dl.Metadata == null) dl.Metadata = new Dictionary<string, object>();
                                dl.Metadata["CanMoveFiles"] = txCanMoveFiles;
                                dl.Metadata["CanBeRemoved"] = txCanBeRemoved;
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                _logger.LogDebug(ex, "Failed to persist CanMoveFiles/CanBeRemoved for Transmission download {DownloadId}", dl.Id);
                            }

                            // Skip finalization/progress logic for downloads that are already
                            // being processed, awaiting import, or fully imported.
                            if (dl.Status == DownloadStatus.Moved ||
                                dl.Status == DownloadStatus.Processing ||
                                dl.Status == DownloadStatus.ImportPending)
                            {
                                _logger.LogDebug("Skipping finalization for {Status} download {DownloadId}", dl.Status, dl.Id);
                                continue;
                            }

                            // Check for completion using same logic as TransmissionAdapter
                            var isComplete = percent >= 1.0 && (status == "seeding" || status == "queued" || status == "paused");
                            _logger.LogInformation("PollTransmission download {DownloadId}: percent={Percent}, status={Status}, isComplete={IsComplete}", dl.Id, percent, status, isComplete);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogWarning(ex, "Error processing download {DownloadId} while polling Transmission", dl.Id);
                        }
                    }

                    return downloads;
                }

                // If we reach here, session-id flow failed after retries
                throw new DownloadClientAdapterPollingException($"PollTransmission failed to establish session after retries for client {client.Id}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Error polling Transmission client {ClientName}", client.Name);
                throw new DownloadClientAdapterPollingException($"Error polling Transmission client {client.Id}");
            }
        }

    }
}
