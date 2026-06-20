/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Text.Encodings.Web;
using System.Text.Json;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Transmission
{
    internal sealed class TransmissionDownloadPollingWorkflow
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger _logger;
        private readonly string _clientType;

        public TransmissionDownloadPollingWorkflow(IHttpClientFactory httpClientFactory, ILogger logger, string clientType)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _clientType = clientType;
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
                using var http = _httpClientFactory.CreateClient(_clientType);

                bool txRemoveCompletedDownloads = !string.IsNullOrEmpty(client.RemoveCompletedDownloads) &&
                    client.RemoveCompletedDownloads != "none";

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

                var serializedPayload = JsonSerializer.Serialize(rpc, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                string? sessionId = null;

                _logger.LogDebug("PollTransmission RPC request to {BaseUrl}", baseUrl);

                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl)
                    {
                        Content = new StringContent(serializedPayload, System.Text.Encoding.UTF8, "application/json")
                    };

                    if (!string.IsNullOrEmpty(sessionId))
                    {
                        request.Headers.Add("X-Transmission-Session-Id", sessionId);
                        _logger.LogDebug("PollTransmission using X-Transmission-Session-Id: {SessionId}", sessionId);
                    }

                    if (!string.IsNullOrWhiteSpace(client.Username))
                    {
                        var credentials = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{client.Username}:{client.Password}"));
                        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
                    }

                    var resp = await http.SendAsync(request, cancellationToken);
                    var respText = await resp.Content.ReadAsStringAsync(cancellationToken);

                    if (resp.StatusCode == System.Net.HttpStatusCode.Conflict && attempt == 0)
                    {
                        if (resp.Headers.TryGetValues("X-Transmission-Session-Id", out var values))
                        {
                            sessionId = values.FirstOrDefault();
                            _logger.LogDebug("PollTransmission received 409 Conflict, retrying with session-id: {SessionId}", sessionId);
                            continue;
                        }
                    }

                    _logger.LogInformation("PollTransmission HTTP response: {StatusCode}", resp.StatusCode);
                    if (!resp.IsSuccessStatusCode)
                    {
                        throw new DownloadClientAdapterPollingException($"PollTransmission early-return: non-success HTTP status {resp.StatusCode} from {baseUrl} for client {client.Id}");
                    }

                    _logger.LogDebug("PollTransmission response text length: {Length}", respText?.Length ?? 0);
                    if (string.IsNullOrWhiteSpace(respText))
                    {
                        throw new DownloadClientAdapterPollingException($"PollTransmission early-return: empty response content for client {client.Id}");
                    }

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

                    bool txSessionSeedRatioLimited = false;
                    double txSessionSeedRatioLimit = 0;
                    bool txSessionIdleSeedingLimitEnabled = false;
                    int txSessionIdleSeedingLimit = 0;
                    try
                    {
                        var sessionPayload = JsonSerializer.Serialize(new { method = "session-get", arguments = new { }, tag = 99 }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
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
                            var sessionDoc = JsonSerializer.Deserialize<JsonElement>(sessionText);
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

                    foreach (var dl in downloads)
                    {
                        try
                        {
                            var matching = torrents.EnumerateArray().FirstOrDefault(t =>
                            {
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

                                var name = t.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                                if (string.Equals(name, dl.Title, StringComparison.OrdinalIgnoreCase))
                                    return true;
                                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(dl.Title) &&
                                    string.Equals(TitleUtils.NormalizeTitle(name), TitleUtils.NormalizeTitle(dl.Title), StringComparison.OrdinalIgnoreCase))
                                    return true;
                                return false;
                            });

                            if (matching.ValueKind == JsonValueKind.Undefined)
                            {
                                _logger.LogDebug("Could not find matching torrent for download {DownloadId} ({Title}) in Transmission", dl.Id, dl.Title);
                                continue;
                            }

                            _logger.LogDebug("Matched download {DownloadId} to Transmission torrent", dl.Id);

                            var percent = matching.TryGetProperty("percentDone", out var p) ? p.GetDouble() : 0.0;
                            var left = matching.TryGetProperty("leftUntilDone", out var l) ? l.GetInt64() : 0L;
                            var statusCode = matching.TryGetProperty("status", out var statusProp) ? statusProp.GetInt32() : 0;

                            var status = statusCode switch
                            {
                                0 => "paused",
                                1 => "queued",
                                2 => "downloading",
                                3 => "queued",
                                4 => "downloading",
                                5 => "queued",
                                6 => "seeding",
                                7 => "failed",
                                _ => "unknown"
                            };

                            AdapterUtils.MapDownloadProgress(dl, percent * 100, left, status);

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

                            if (dl.Status == DownloadStatus.Moved ||
                                dl.Status == DownloadStatus.Processing ||
                                dl.Status == DownloadStatus.ImportPending)
                            {
                                _logger.LogDebug("Skipping finalization for {Status} download {DownloadId}", dl.Status, dl.Id);
                                continue;
                            }

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
