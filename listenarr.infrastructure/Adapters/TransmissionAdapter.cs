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
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
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

        public TransmissionAdapter(IHttpClientFactory httpClientFactory, ITorrentFileDownloader torrentFileDownloader, ILogger<TransmissionAdapter> logger)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _torrentFileDownloader = torrentFileDownloader ?? throw new ArgumentNullException(nameof(torrentFileDownloader));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
                var response = await InvokeRpcAsync(client, payload, ct);

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

            var arguments = new Dictionary<string, object>();

            // Prefer cached torrent file data over URL (required for private trackers with authentication)
            byte[]? torrentFileData = result.TorrentFileContent;
            var magnetLink = DownloadClientUriBuilder.NormalizeMagnetLink(result.MagnetLink);
            var httpTorrentUrl = NormalizeTorrentUrl(result.TorrentUrl);
            var torrentUrl = magnetLink.Length > 0 ? magnetLink : httpTorrentUrl ?? string.Empty;
            var isMagnetTarget = magnetLink.Length > 0;

            _logger.LogDebug("AddAsync entry for '{Title}': TorrentFileContent={HasContent}, MagnetLink={HasMagnet}, TorrentUrl={Url}",
                LogRedaction.SanitizeText(result.Title),
                result.TorrentFileContent != null && result.TorrentFileContent.Length > 0 ? $"{result.TorrentFileContent.Length} bytes" : "null",
                isMagnetTarget ? "yes" : "no",
                LogRedaction.SanitizeUrl(torrentUrl));

            // Transmission's magnet link handling is less reliable than qBittorrent's — it
            // often stalls at "Downloading metadata..." because its DHT/tracker resolution is
            // weaker. When a separate TorrentUrl (HTTP) is available alongside a magnet link,
            // prefer fetching the .torrent file from TorrentUrl. The .torrent file contains
            // full tracker lists and piece hashes, giving Transmission everything it needs to
            // start immediately without metadata resolution.
            if ((torrentFileData == null || torrentFileData.Length == 0) &&
                isMagnetTarget &&
                !string.IsNullOrEmpty(httpTorrentUrl))
            {
                _logger.LogDebug("Magnet link available but TorrentUrl also present — attempting .torrent pre-download from {Url} for better Transmission compatibility",
                    LogRedaction.SanitizeUrl(httpTorrentUrl));
                try
                {
                    var altResult = await _torrentFileDownloader.DownloadAsync(httpTorrentUrl, ct);
                    if (altResult.HasBytes)
                    {
                        torrentFileData = altResult.TorrentBytes;
                        _logger.LogInformation("Pre-downloaded .torrent file ({Bytes} bytes) from TorrentUrl for '{Title}' — using instead of magnet link",
                            torrentFileData!.Length, LogRedaction.SanitizeText(result.Title));
                    }
                    else
                    {
                        _logger.LogDebug("TorrentUrl pre-download did not return file data for '{Title}', will use magnet link", LogRedaction.SanitizeText(result.Title));
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogDebug(ex, "TorrentUrl pre-download failed for '{Title}', will use magnet link", LogRedaction.SanitizeText(result.Title));
                }
            }

            // Pre-download torrent file if not cached and URL is HTTP(S) (not magnet).
            // Transmission's built-in HTTP client cannot always follow redirects from indexers
            // (e.g. Prowlarr returning 301), so we fetch the .torrent file ourselves and send
            // the raw bytes via the metainfo field instead.
            if ((torrentFileData == null || torrentFileData.Length == 0) &&
                !isMagnetTarget &&
                !string.IsNullOrEmpty(httpTorrentUrl))
            {
                _logger.LogDebug("Attempting pre-download of torrent file from {Url}", LogRedaction.SanitizeUrl(httpTorrentUrl));
                try
                {
                    var downloadResult = await _torrentFileDownloader.DownloadAsync(httpTorrentUrl, ct);
                    if (downloadResult.HasBytes)
                    {
                        torrentFileData = downloadResult.TorrentBytes;
                        _logger.LogInformation("Pre-downloaded torrent file ({Bytes} bytes) for '{Title}'",
                            torrentFileData!.Length, LogRedaction.SanitizeText(result.Title));
                    }
                    else if (downloadResult.HasMagnet)
                    {
                        // Indexer redirected to a magnet link — use it directly
                        torrentUrl = DownloadClientUriBuilder.NormalizeMagnetLink(downloadResult.MagnetUri);
                        _logger.LogInformation("Indexer redirected to magnet link for '{Title}'", LogRedaction.SanitizeText(result.Title));
                    }
                    else
                    {
                        _logger.LogWarning("Pre-download returned no data for '{Title}', falling back to URL", LogRedaction.SanitizeText(result.Title));
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to pre-download torrent file for '{Title}', falling back to URL", LogRedaction.SanitizeText(result.Title));
                }
            }
            else if (torrentFileData == null || torrentFileData.Length == 0)
            {
                _logger.LogDebug("Skipping pre-download: torrentFileData={HasData}, torrentUrl={Url}, isMagnet={IsMagnet}",
                    torrentFileData != null && torrentFileData.Length > 0 ? "has data" : "null/empty",
                    string.IsNullOrEmpty(torrentUrl) ? "(empty)" : LogRedaction.SanitizeUrl(torrentUrl),
                    torrentUrl?.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) == true ? "yes" : "no");
            }

            if (torrentFileData != null && torrentFileData.Length > 0)
            {
                // Use metainfo field for torrent file data (base64 encoded)
                arguments["metainfo"] = Convert.ToBase64String(torrentFileData);
                _logger.LogDebug("Using cached torrent file data ({Bytes} bytes) for '{Title}'", torrentFileData.Length, LogRedaction.SanitizeText(result.Title));
            }
            else
            {
                // Fall back to filename field for URLs/magnet links
                if (string.IsNullOrEmpty(torrentUrl))
                {
                    throw new ArgumentException("No magnet link, torrent URL, or cached torrent file provided", nameof(result));
                }

                // Transmission does not reliably decode percent-encoded magnet parameter
                // values, so decode safe values ahead of time. Leave values encoded when
                // decoding would introduce top-level separators like '&' or '#' and corrupt
                // the magnet payload.
                if (torrentUrl.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    var normalizedMagnetUrl = NormalizeMagnetUriForTransmission(torrentUrl);
                    if (!string.Equals(normalizedMagnetUrl, torrentUrl, StringComparison.Ordinal))
                    {
                        _logger.LogDebug("Normalized percent-encoded magnet link for Transmission compatibility");
                    }
                    torrentUrl = normalizedMagnetUrl;
                }

                arguments["filename"] = torrentUrl;
                _logger.LogDebug("Using torrent URL for '{Title}': {Url}", LogRedaction.SanitizeText(result.Title), LogRedaction.SanitizeUrl(torrentUrl));
            }

            // Only include download-dir if it's not empty (Transmission requires absolute path or omit)
            if (!string.IsNullOrWhiteSpace(client.DownloadPath))
            {
                arguments["download-dir"] = client.DownloadPath;
            }

            // Explicitly request that the torrent starts immediately. Without this,
            // Transmission uses its session setting `start-added-torrents` which
            // defaults to true but may be set to false by the user.
            arguments["paused"] = false;

            var labels = CollectLabels(client);
            if (labels.Count > 0)
            {
                arguments["labels"] = labels.ToArray();
            }

            // Use old format for compatibility with Transmission < 4.1.0
            var payload = new
            {
                method = "torrent-add",
                arguments,
                tag = 1
            };

            try
            {
                var response = await InvokeRpcAsync(client, payload, ct);

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
                        var torrentId = ExtractTorrentIdentifier(added);
                        _logger.LogInformation("Transmission successfully added torrent '{Title}' with id/hash: {Id}", LogRedaction.SanitizeText(result.Title), LogRedaction.SanitizeText(torrentId));
                        return torrentId;
                    }

                    if (args.TryGetProperty("torrent-duplicate", out var duplicate) && duplicate.ValueKind == JsonValueKind.Object)
                    {
                        var existingId = ExtractTorrentIdentifier(duplicate);
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

            var idsPayload = ParseTransmissionIds(id);
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
                var response = await InvokeRpcAsync(client, payload, ct);
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
                var response = await InvokeRpcAsync(client, payload, ct);
                if (!response.TryGetProperty("arguments", out var args) || !args.TryGetProperty("torrents", out var torrents) || torrents.ValueKind != JsonValueKind.Array)
                {
                    return items;
                }

                foreach (var torrent in torrents.EnumerateArray())
                {
                    try
                    {
                        var labels = ExtractLabels(torrent);
                        if (!DownloadClientCategoryFilter.MatchesAny(configuredCategory, labels))
                        {
                            continue;
                        }

                        var queueItem = await MapTorrentAsync(client, torrent, ct);
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
                var sessionResp = await InvokeRpcAsync(client, sessionPayload, ct);
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
                var response = await InvokeRpcAsync(client, payload, ct);
                if (!response.TryGetProperty("arguments", out var args) || !args.TryGetProperty("torrents", out var torrents) || torrents.ValueKind != JsonValueKind.Array)
                {
                    return items;
                }

                foreach (var torrent in torrents.EnumerateArray())
                {
                    try
                    {
                        var labels = ExtractLabels(torrent);
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
                if (!string.IsNullOrEmpty(localPath) && (File.Exists(localPath) || Directory.Exists(localPath)))
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
                    ids = ParseTransmissionIds(item.DownloadId),
                    fields = new[] { "id", "name", "downloadDir" }
                },
                tag = 5
            };

            try
            {
                var response = await InvokeRpcAsync(client, payload, ct);
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
                var contentPath = FileUtils.CombineWithOptionalBase(downloadDir, name);

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
                if (!string.IsNullOrEmpty(localPath) && (File.Exists(localPath) || Directory.Exists(localPath)))
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
                    ids = ParseTransmissionIds(queueItem.Id),
                    fields = new[] { "id", "name", "downloadDir", "files" }
                },
                tag = 5
            };

            try
            {
                var response = await InvokeRpcAsync(client, payload, ct);
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
                var contentPath = !string.IsNullOrWhiteSpace(downloadDir) && !string.IsNullOrWhiteSpace(name)
                    ? FileUtils.CombineWithOptionalBase(downloadDir, name)
                    : resolvedExistingContentPath;
                string? localContentPath = resolvedExistingContentPath;
                if (!string.IsNullOrWhiteSpace(contentPath))
                {
                    localContentPath = contentPath;
                    result.ContentPath = localContentPath;
                }

                if (torrent.TryGetProperty("files", out var filesElement))
                {
                    var sourceFiles = BuildTransmissionSourceFiles(downloadDir, filesElement);
                    result.SourceFiles = [.. sourceFiles.Where(path => !string.IsNullOrWhiteSpace(path))];
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

        private static List<string> BuildTransmissionSourceFiles(string? downloadDir, JsonElement filesElement)
        {
            if (string.IsNullOrWhiteSpace(downloadDir) || filesElement.ValueKind != JsonValueKind.Array)
            {
                return new List<string>();
            }

            var sourceFiles = new List<string>();
            foreach (var file in filesElement.EnumerateArray())
            {
                if (!file.TryGetProperty("name", out var nameProp))
                {
                    continue;
                }

                var relativePath = nameProp.GetString();
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    continue;
                }

                sourceFiles.Add(FileUtils.CombineWithOptionalBase(downloadDir, relativePath));
            }

            return sourceFiles;
        }

        private async Task<QueueItem> MapTorrentAsync(DownloadClientConfiguration client, JsonElement torrent, CancellationToken ct)
        {
            // Try snake_case (JSON-RPC 2.0 / Transmission 4.1+) first, fall back to camelCase for backwards compatibility
            var id = torrent.TryGetProperty("hash_string", out var hashProp) || torrent.TryGetProperty("hashString", out hashProp)
                ? hashProp.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrEmpty(id) && torrent.TryGetProperty("id", out var numericId))
            {
                id = numericId.GetInt32().ToString(CultureInfo.InvariantCulture);
            }

            var name = torrent.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
            var percentDone = (torrent.TryGetProperty("percent_done", out var percentProp) || torrent.TryGetProperty("percentDone", out percentProp))
                ? percentProp.GetDouble() * 100 : 0d;
            var totalSize = (torrent.TryGetProperty("total_size", out var sizeProp) || torrent.TryGetProperty("totalSize", out sizeProp))
                ? sizeProp.GetInt64() : 0L;
            var leftUntilDone = (torrent.TryGetProperty("left_until_done", out var leftProp) || torrent.TryGetProperty("leftUntilDone", out leftProp))
                ? leftProp.GetInt64() : 0L;
            var rateDownload = (torrent.TryGetProperty("rate_download", out var rateProp) || torrent.TryGetProperty("rateDownload", out rateProp))
                ? rateProp.GetDouble() : 0d;
            var eta = torrent.TryGetProperty("eta", out var etaProp) ? etaProp.GetInt32() : -1;
            var downloadDir = (torrent.TryGetProperty("download_dir", out var dirProp) || torrent.TryGetProperty("downloadDir", out dirProp))
                ? dirProp.GetString() ?? string.Empty : string.Empty;
            var statusCode = torrent.TryGetProperty("status", out var statusProp) ? statusProp.GetInt32() : 0;
            var addedDate = (torrent.TryGetProperty("added_date", out var addedProp) || torrent.TryGetProperty("addedDate", out addedProp))
                ? addedProp.GetInt64() : 0L;
            var uploadRatio = (torrent.TryGetProperty("upload_ratio", out var ratioProp) || torrent.TryGetProperty("uploadRatio", out ratioProp))
                ? ratioProp.GetDouble() : 0d;

            var downloaded = Math.Max(0, totalSize - leftUntilDone);

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

            _logger.LogDebug("Before completion check: hash={Hash}, percentDone={PercentDone}, status={Status}",
                id, percentDone, status);

            if (percentDone >= 100.0 && (status == "seeding" || status == "queued" || status == "paused"))
            {
                status = "completed";
            }

            _logger.LogDebug("After completion check: hash={Hash}, finalStatus={Status}", id, status);

            string? localPath = downloadDir;
            var addedAt = addedDate > 0 ? DateTimeOffset.FromUnixTimeSeconds(addedDate).UtcDateTime : DateTime.UtcNow;

            // For Transmission, construct ContentPath from downloadDir + name
            var contentPath = !string.IsNullOrEmpty(downloadDir) && !string.IsNullOrEmpty(name)
                ? FileUtils.CombineWithOptionalBase(downloadDir, name)
                : downloadDir;
            var localContentPath = contentPath;
            var primaryLabel = ExtractLabels(torrent).FirstOrDefault() ?? string.Empty;

            var queueItem = new QueueItem
            {
                Id = id,
                Title = name,
                Quality = string.IsNullOrWhiteSpace(primaryLabel) ? "Unknown" : primaryLabel,
                Status = status,
                Progress = percentDone,
                Size = totalSize,
                Downloaded = downloaded,
                DownloadSpeed = rateDownload,
                Eta = eta >= 0 ? eta : null,
                DownloadClient = client.Name ?? client.Id ?? "Transmission",
                DownloadClientId = client.Id ?? string.Empty,
                DownloadClientType = ClientType,
                AddedAt = addedAt,
                Ratio = uploadRatio,
                CanPause = status is "downloading" or "queued",
                CanRemove = true,
                RemotePath = downloadDir,
                LocalPath = localPath,
                ContentPath = localContentPath
            };

            return queueItem;
        }

        private async Task<DownloadClientItem> MapToDownloadClientItemAsync(
            DownloadClientConfiguration client,
            JsonElement torrent,
            (bool SeedRatioLimited, double SeedRatioLimit, bool IdleSeedingLimitEnabled, int IdleSeedingLimit) sessionConfig,
            CancellationToken ct)
        {
            // Try snake_case (JSON-RPC 2.0 / Transmission 4.1+) first, fall back to camelCase for backwards compatibility
            var hash = torrent.TryGetProperty("hash_string", out var hashProp) || torrent.TryGetProperty("hashString", out hashProp)
                ? hashProp.GetString() ?? string.Empty : string.Empty;
            var numericId = torrent.TryGetProperty("id", out var numericIdProp) ? numericIdProp.GetInt32() : 0;
            var name = torrent.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
            var percentDone = (torrent.TryGetProperty("percent_done", out var percentProp) || torrent.TryGetProperty("percentDone", out percentProp))
                ? percentProp.GetDouble() * 100 : 0d;
            var totalSize = (torrent.TryGetProperty("total_size", out var sizeProp) || torrent.TryGetProperty("totalSize", out sizeProp))
                ? sizeProp.GetInt64() : 0L;
            var leftUntilDone = (torrent.TryGetProperty("left_until_done", out var leftProp) || torrent.TryGetProperty("leftUntilDone", out leftProp))
                ? leftProp.GetInt64() : 0L;
            var rateDownload = (torrent.TryGetProperty("rate_download", out var rateProp) || torrent.TryGetProperty("rateDownload", out rateProp))
                ? rateProp.GetDouble() : 0d;
            var eta = torrent.TryGetProperty("eta", out var etaProp) ? etaProp.GetInt32() : -1;
            var downloadDir = (torrent.TryGetProperty("download_dir", out var dirProp) || torrent.TryGetProperty("downloadDir", out dirProp))
                ? dirProp.GetString() ?? string.Empty : string.Empty;
            var statusCode = torrent.TryGetProperty("status", out var statusProp) ? statusProp.GetInt32() : 0;
            var uploadRatio = (torrent.TryGetProperty("upload_ratio", out var ratioProp) || torrent.TryGetProperty("uploadRatio", out ratioProp))
                ? ratioProp.GetDouble() : 0d;

            // Seed limit fields for Sonarr-parity seed limit evaluation
            var seedRatioMode = (torrent.TryGetProperty("seed_ratio_mode", out var srmProp) || torrent.TryGetProperty("seedRatioMode", out srmProp))
                ? srmProp.GetInt32() : 0;
            var seedRatioLimit = (torrent.TryGetProperty("seed_ratio_limit", out var srlProp) || torrent.TryGetProperty("seedRatioLimit", out srlProp))
                ? srlProp.GetDouble() : 0d;
            var seedIdleMode = (torrent.TryGetProperty("seed_idle_mode", out var simProp) || torrent.TryGetProperty("seedIdleMode", out simProp))
                ? simProp.GetInt32() : 0;
            var seedIdleLimit = (torrent.TryGetProperty("seed_idle_limit", out var silProp) || torrent.TryGetProperty("seedIdleLimit", out silProp))
                ? silProp.GetInt32() : 0;
            var secondsSeeding = (torrent.TryGetProperty("seconds_seeding", out var ssProp) || torrent.TryGetProperty("secondsSeeding", out ssProp))
                ? ssProp.GetInt64() : 0L;

            // Map Transmission status codes to DownloadItemStatus
            var status = statusCode switch
            {
                0 => DownloadItemStatus.Paused,  // Stopped
                1 => DownloadItemStatus.Queued,  // Check waiting
                2 => DownloadItemStatus.Downloading, // Checking
                3 => DownloadItemStatus.Queued,  // Download waiting
                4 => DownloadItemStatus.Downloading, // Downloading
                5 => DownloadItemStatus.Queued,  // Seed waiting
                6 => DownloadItemStatus.Downloading, // Seeding
                _ => DownloadItemStatus.Warning
            };

            if (percentDone >= 100.0 && (statusCode is 0 or 3 or 5 or 6))
            {
                status = DownloadItemStatus.Completed;
            }

            // For Transmission, construct OutputPath from downloadDir + name
            var contentPath = !string.IsNullOrEmpty(downloadDir) && !string.IsNullOrEmpty(name)
                ? FileUtils.CombineWithOptionalBase(downloadDir, name)
                : downloadDir;
            var localContentPath = contentPath;
            var primaryLabel = ExtractLabels(torrent).FirstOrDefault() ?? string.Empty;

            TimeSpan? remainingTime = eta >= 0 ? TimeSpan.FromSeconds(eta) : null;

            // ✅ Use hash as DownloadId if available, otherwise fall back to numeric ID
            var downloadId = !string.IsNullOrEmpty(hash) ? hash.ToUpperInvariant() : numericId.ToString(CultureInfo.InvariantCulture);

            // Sonarr parity: CanBeRemoved = removeCompletedDownloads && HasReachedSeedLimit
            //                 CanMoveFiles = CanBeRemoved && status == Stopped (statusCode 0)
            // This prevents removing torrents before seed goals are met and prevents
            // moving files from active seeders (which breaks the torrent).
            var removeCompletedDownloads = client.Settings?.TryGetValue("removeCompletedDownloads", out var removeVal) is true &&
                (removeVal is bool boolVal && boolVal);
            var isStopped = statusCode == 0; // TR_STATUS_STOPPED
            var isSeeding = statusCode == 6; // TR_STATUS_SEED
            var seedLimitReached = HasReachedSeedLimit(
                isStopped, isSeeding, uploadRatio,
                seedRatioMode, seedRatioLimit,
                seedIdleMode, seedIdleLimit, secondsSeeding,
                sessionConfig);
            var canBeRemoved = removeCompletedDownloads && seedLimitReached;
            var canMoveFiles = canBeRemoved && isStopped;

            return new DownloadClientItem
            {
                DownloadId = downloadId,
                Title = name,
                Category = primaryLabel,
                Status = status,
                TotalSize = totalSize,
                RemainingSize = leftUntilDone,
                RemainingTime = remainingTime,
                SeedRatio = uploadRatio,
                OutputPath = localContentPath,
                Message = $"Status code: {statusCode}",
                Progress = percentDone,
                DownloadSpeed = rateDownload,
                CanBeRemoved = canBeRemoved,
                CanMoveFiles = canMoveFiles,
                DownloadClientInfo = DownloadClientItemClientInfo.FromClient(
                    clientId: client.Id,
                    clientName: client.Name,
                    clientType: "transmission",
                    protocol: DownloadProtocol.Torrent,
                    removeCompletedDownloads: removeCompletedDownloads,
                    hasPostImportCategory: false // Transmission doesn't support post-import categories
                )
            };
        }

        /// <summary>
        /// Determines whether a Transmission torrent has reached its seed limit (ratio or idle time).
        /// Mirrors Sonarr's HasReachedSeedLimit logic for Transmission.
        /// </summary>
        private static bool HasReachedSeedLimit(
            bool isStopped,
            bool isSeeding,
            double ratio,
            int seedRatioMode,
            double seedRatioLimit,
            int seedIdleMode,
            int seedIdleLimit,
            long secondsSeeding,
            (bool SeedRatioLimited, double SeedRatioLimit, bool IdleSeedingLimitEnabled, int IdleSeedingLimit) sessionConfig)
        {
            var hasEffectiveRatioLimit =
                (seedRatioMode == 1 && seedRatioLimit > 0) ||
                (seedRatioMode == 0 && sessionConfig.SeedRatioLimited && sessionConfig.SeedRatioLimit > 0);
            var hasEffectiveIdleLimit =
                (seedIdleMode == 1 && seedIdleLimit > 0) ||
                (seedIdleMode == 0 && sessionConfig.IdleSeedingLimitEnabled && sessionConfig.IdleSeedingLimit > 0);

            // With no effective seed constraints configured, honor the cleanup policy
            // immediately instead of reporting the torrent as non-removable forever.
            if (!hasEffectiveRatioLimit && !hasEffectiveIdleLimit)
            {
                return true;
            }

            // seedRatioMode: 0 = global, 1 = per-torrent, 2 = unlimited
            if (seedRatioMode == 1 && isStopped && ratio >= seedRatioLimit)
            {
                // Per-torrent ratio limit
                return true;
            }

            if (seedRatioMode == 0 && isStopped && sessionConfig.SeedRatioLimited && ratio >= sessionConfig.SeedRatioLimit)
            {
                // Use global ratio limit
                return true;
            }

            // seedIdleMode: 0 = global, 1 = per-torrent, 2 = unlimited
            // Transmission uses idle limit as a seeding time limit when set per-torrent
            if (seedIdleMode == 1 && (isStopped || isSeeding) && secondsSeeding > seedIdleLimit * 60)
            {
                // Per-torrent idle/seed time limit (in minutes)
                return true;
            }

            if (seedIdleMode == 0 && isStopped && sessionConfig.IdleSeedingLimitEnabled)
            {
                // The global idle limit is a real idle limit, if configured then 'Stopped' is enough
                return true;
            }

            return false;
        }

        private static List<string> ExtractLabels(JsonElement torrent)
        {
            var labels = new List<string>();
            if (!torrent.TryGetProperty("labels", out var labelsProp) || labelsProp.ValueKind != JsonValueKind.Array)
            {
                return labels;
            }

            foreach (var label in labelsProp.EnumerateArray())
            {
                if (label.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = label.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    labels.Add(value.Trim());
                }
            }

            return labels;
        }

        private List<string> CollectLabels(DownloadClientConfiguration client)
        {
            var labels = new List<string>();

            if (client.Settings != null && client.Settings.TryGetValue("category", out var categoryObj))
            {
                var category = categoryObj?.ToString();
                if (!string.IsNullOrWhiteSpace(category))
                {
                    labels.Add(category);
                }
            }

            if (client.Settings != null && client.Settings.TryGetValue("tags", out var tagsObj))
            {
                var tags = tagsObj?.ToString();
                if (!string.IsNullOrWhiteSpace(tags))
                {
                    labels.AddRange(tags
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => t.Trim())
                        .Where(t => !string.IsNullOrEmpty(t)));
                }
            }

            return labels;
        }

        private object[] ParseTransmissionIds(string id)
        {
            if (int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericId))
            {
                return new object[] { numericId };
            }

            return new object[] { id };
        }

        /// <summary>
        /// JsonSerializerOptions that use UnsafeRelaxedJsonEscaping so that characters like
        /// &amp;, +, and = inside magnet-link query strings are NOT escaped to \u00XX sequences.
        /// Transmission's built-in JSON parser does not always decode unicode escape sequences
        /// correctly, which causes tracker URLs in magnet links (&amp;tr=...) to be silently lost.
        /// </summary>
        private static readonly JsonSerializerOptions s_rpcJsonOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private async Task<JsonElement> InvokeRpcAsync(DownloadClientConfiguration client, object payload, CancellationToken ct)
        {
            var httpClient = _httpClientFactory.CreateClient(ClientType);
            var baseUrl = BuildBaseUrl(client);
            var serializedPayload = JsonSerializer.Serialize(payload, s_rpcJsonOptions);
            string? sessionId = null;

            _logger.LogDebug("Transmission RPC request to {Url}: {Payload}", LogRedaction.SanitizeUrl(baseUrl), LogRedaction.SanitizeText(serializedPayload, 500));

            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl)
                {
                    Content = new StringContent(serializedPayload, Encoding.UTF8, "application/json")
                };

                if (!string.IsNullOrEmpty(sessionId))
                {
                    request.Headers.Add("X-Transmission-Session-Id", sessionId);
                    _logger.LogDebug("Using X-Transmission-Session-Id: {SessionId}", LogRedaction.SanitizeText(sessionId));
                }

                var authHeader = BuildAuthHeader(client);
                if (authHeader != null)
                {
                    request.Headers.Authorization = authHeader;
                }

                var response = await httpClient.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);

                if (response.StatusCode == HttpStatusCode.Conflict && attempt == 0 && response.Headers.TryGetValues("X-Transmission-Session-Id", out var values))
                {
                    sessionId = values.FirstOrDefault();
                    _logger.LogDebug("Received 409 Conflict, retrying with session ID: {SessionId}", LogRedaction.SanitizeText(sessionId));
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var sensitiveValues = LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { client.Password ?? string.Empty });
                    var redacted = LogRedaction.RedactText(body, sensitiveValues);
                    _logger.LogWarning("Transmission returned {StatusCode}: {Body}", response.StatusCode, redacted);
                    throw new HttpRequestException($"Transmission returned {response.StatusCode}: {redacted}", null, response.StatusCode);
                }

                _logger.LogDebug("Transmission RPC response ({StatusCode}): {Body}", response.StatusCode, body);

                if (string.IsNullOrWhiteSpace(body))
                {
                    _logger.LogWarning("Transmission returned empty response body");
                    using var emptyDoc = JsonDocument.Parse("{}");
                    return emptyDoc.RootElement.Clone();
                }

                // Validate the response is actually JSON before parsing. A non-Transmission service
                // (or the web UI on the wrong port) may return HTML which would fail JSON parsing
                // with an unhelpful error message.
                var trimmedBody = body.TrimStart();
                if (trimmedBody.Length > 0 && trimmedBody[0] != '{' && trimmedBody[0] != '[')
                {
                    var preview = trimmedBody.Length > 100 ? trimmedBody[..100] + "..." : trimmedBody;
                    _logger.LogWarning("Transmission RPC returned non-JSON response: {Preview}", LogRedaction.SanitizeText(preview));
                    throw new HttpRequestException("Transmission RPC endpoint returned a non-JSON response. Verify the host and port point to the Transmission RPC endpoint (default port 9091).");
                }

                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.Clone();
            }

            throw new InvalidOperationException("Transmission did not supply a session identifier after retrying.");
        }

        private static string BuildBaseUrl(DownloadClientConfiguration client)
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
            return DownloadClientUriBuilder.BuildUri(client, rpcPath).ToString();
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

        private static string NormalizeMagnetUriForTransmission(string magnetUri)
        {
            var queryStart = magnetUri.IndexOf('?');
            if (queryStart < 0 || queryStart >= magnetUri.Length - 1)
            {
                return magnetUri;
            }

            var segments = magnetUri[(queryStart + 1)..].Split('&');
            var changed = false;

            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                if (string.IsNullOrEmpty(segment))
                {
                    continue;
                }

                var equalsIndex = segment.IndexOf('=');
                if (equalsIndex <= 0 || equalsIndex >= segment.Length - 1)
                {
                    continue;
                }

                var value = segment[(equalsIndex + 1)..];
                if (!value.Contains('%'))
                {
                    continue;
                }

                var decodedValue = Uri.UnescapeDataString(value);
                if (decodedValue.Contains('&') || decodedValue.Contains('#'))
                {
                    continue;
                }

                if (!string.Equals(decodedValue, value, StringComparison.Ordinal))
                {
                    segments[i] = $"{segment[..(equalsIndex + 1)]}{decodedValue}";
                    changed = true;
                }
            }

            if (!changed)
            {
                return magnetUri;
            }

            return $"{magnetUri[..(queryStart + 1)]}{string.Join("&", segments)}";
        }

        private static AuthenticationHeaderValue? BuildAuthHeader(DownloadClientConfiguration client)
        {
            if (string.IsNullOrWhiteSpace(client.Username))
            {
                return null;
            }

            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{client.Username}:{client.Password}"));
            return new AuthenticationHeaderValue("Basic", credentials);
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

        private static string? ExtractTorrentIdentifier(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // Try snake_case (JSON-RPC 2.0 / Transmission 4.1+) first, fall back to camelCase
            if ((element.TryGetProperty("hash_string", out var hashProp) || element.TryGetProperty("hashString", out hashProp)))
            {
                var hash = hashProp.GetString();
                if (!string.IsNullOrWhiteSpace(hash))
                {
                    return hash;
                }
            }

            if (element.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
            {
                return idProp.GetInt32().ToString(CultureInfo.InvariantCulture);
            }

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
                                var txSeedLimitReached = TransmissionHasReachedSeedLimit(
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

        /// <summary>
        /// Determines whether a Transmission torrent has reached its seed limit.
        /// Mirrors Sonarr's HasReachedSeedLimit logic for Transmission.
        /// </summary>
        private static bool TransmissionHasReachedSeedLimit(
            bool isStopped,
            bool isSeeding,
            double ratio,
            int seedRatioMode,
            double seedRatioLimit,
            int seedIdleMode,
            int seedIdleLimit,
            long secondsSeeding,
            bool sessionSeedRatioLimited,
            double sessionSeedRatioLimit,
            bool sessionIdleSeedingLimitEnabled,
            int sessionIdleSeedingLimit)
        {
            var hasEffectiveRatioLimit =
                (seedRatioMode == 1 && seedRatioLimit > 0) ||
                (seedRatioMode == 0 && sessionSeedRatioLimited && sessionSeedRatioLimit > 0);
            var hasEffectiveIdleLimit =
                (seedIdleMode == 1 && seedIdleLimit > 0) ||
                (seedIdleMode == 0 && sessionIdleSeedingLimitEnabled && sessionIdleSeedingLimit > 0);

            // If Transmission has no seed ratio or idle seeding limits configured,
            // the user's remove policy should not defer forever. Treat the item as removable.
            if (!hasEffectiveRatioLimit && !hasEffectiveIdleLimit)
            {
                return true;
            }

            // seedRatioMode: 0 = global, 1 = per-torrent, 2 = unlimited
            if (seedRatioMode == 1 && isStopped && ratio >= seedRatioLimit)
                return true;

            bool globalRatioExceeded = seedRatioMode == 0 && isStopped && sessionSeedRatioLimited && ratio >= sessionSeedRatioLimit;
            if (globalRatioExceeded)
                return true;

            // seedIdleMode: 0 = global, 1 = per-torrent, 2 = unlimited
            bool perTorrentIdleExceeded = seedIdleMode == 1 && (isStopped || isSeeding) && secondsSeeding > seedIdleLimit * 60;
            if (perTorrentIdleExceeded)
                return true;

            if (seedIdleMode == 0 && isStopped && sessionIdleSeedingLimitEnabled)
                return true;

            return false;
        }
    }
}

