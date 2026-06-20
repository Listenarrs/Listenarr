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
using Listenarr.Infrastructure.Torrents;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Transmission
{
    public class TransmissionAdapter : IDownloadClientAdapter
    {
        public string ClientId => "transmission";
        public string ClientType => "transmission";
        public DownloadProtocol Protocol => DownloadProtocol.Torrent;

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<TransmissionAdapter> _logger;
        private readonly TransmissionRpcClient _rpcClient;
        private readonly TransmissionDownloadPollingWorkflow _downloadPollingWorkflow;
        private readonly TransmissionRemovalWorkflow _removalWorkflow;
        private readonly TransmissionImportItemResolver _importItemResolver;

        public TransmissionAdapter(IHttpClientFactory httpClientFactory, ITorrentFileDownloader torrentFileDownloader, ILogger<TransmissionAdapter> logger)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _ = torrentFileDownloader ?? throw new ArgumentNullException(nameof(torrentFileDownloader));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _rpcClient = new TransmissionRpcClient(_httpClientFactory, ClientType, _logger);
            _downloadPollingWorkflow = new TransmissionDownloadPollingWorkflow(_httpClientFactory, _logger, ClientType);
            _removalWorkflow = new TransmissionRemovalWorkflow(_rpcClient, _logger);
            _importItemResolver = new TransmissionImportItemResolver(_rpcClient, _logger);
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

        public async Task<DownloadClientSubmissionResult> AddAsync(
            DownloadClientConfiguration client,
            PreparedDownloadSubmission submission,
            CancellationToken ct = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (submission is not PreparedTorrentSubmission torrent)
                throw new DownloadClientSubmissionException("Transmission requires a prepared torrent submission.");

            var labels = TransmissionRequestPlanner.CollectLabels(client);
            var arguments = TransmissionTorrentAddPlanner.BuildArguments(client, torrent, labels);

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
                        if (string.IsNullOrWhiteSpace(torrentId))
                            throw new DownloadClientSubmissionException("Transmission did not return a verified torrent identifier.");
                        _logger.LogInformation("Transmission successfully added torrent '{Title}' with id/hash: {Id}", LogRedaction.SanitizeText(torrent.Title), LogRedaction.SanitizeText(torrentId));
                        return new DownloadClientSubmissionResult(torrentId, torrent.InfoHash);
                    }

                    if (args.TryGetProperty("torrent-duplicate", out var duplicate) && duplicate.ValueKind == JsonValueKind.Object)
                    {
                        var existingId = TransmissionRequestPlanner.ExtractTorrentIdentifier(duplicate);
                        if (string.IsNullOrWhiteSpace(existingId))
                            throw new DownloadClientSubmissionException("Transmission did not return a verified duplicate torrent identifier.");
                        _logger.LogInformation("Transmission reported duplicate torrent for '{Title}' with id/hash {Id}", LogRedaction.SanitizeText(torrent.Title), LogRedaction.SanitizeText(existingId));
                        return new DownloadClientSubmissionResult(existingId, torrent.InfoHash, WasDuplicate: true);
                    }
                }

                throw new DownloadClientSubmissionException(
                    "Transmission did not return a verified torrent identifier.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to add torrent to Transmission for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
                throw;
            }
        }

        public async Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default)
        {
            return await _removalWorkflow.RemoveAsync(client, id, deleteFiles, ct);
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
            return await _importItemResolver.GetImportItemAsync(client, item, ct);
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
            return await _importItemResolver.GetImportItemAsync(client, queueItem, ct);
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
            return await _downloadPollingWorkflow.FetchDownloadsAsync(client, downloads, cancellationToken);
        }

    }
}
