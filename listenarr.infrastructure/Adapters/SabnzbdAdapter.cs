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
using Listenarr.Application.Downloads;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Security;
using Listenarr.Domain.Common;
using Listenarr.Domain.Models;
using Listenarr.Domain.Models.Exceptions;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Adapters
{
    public class SabnzbdAdapter : IDownloadClientAdapter
    {
        public string ClientId => "sabnzbd";
        public string ClientType => "sabnzbd";
        public DownloadProtocol Protocol => DownloadProtocol.Usenet;

        private readonly IHttpClientFactory _httpFactory;
        private readonly INzbUrlResolver _nzbUrlResolver;
        private readonly ILogger<SabnzbdAdapter> _logger;
        private readonly IAppMetricsService _appMetricsService;

        public SabnzbdAdapter(
            IHttpClientFactory httpFactory,
            INzbUrlResolver nzbUrlResolver,
            ILogger<SabnzbdAdapter> logger,
            IAppMetricsService appMetricsService)
        {
            _httpFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
            _nzbUrlResolver = nzbUrlResolver ?? throw new ArgumentNullException(nameof(nzbUrlResolver));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _appMetricsService = appMetricsService;
        }

        public async Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            try
            {
                if (client == null) throw new ArgumentNullException(nameof(client));

                var baseUrl = DownloadClientUriBuilder.BuildUri(client, "/api").ToString();
                var apiKey = "";
                if (client.Settings != null && client.Settings.TryGetValue("apiKey", out var apiKeyObj))
                    apiKey = apiKeyObj?.ToString() ?? "";

                if (string.IsNullOrEmpty(apiKey))
                    return (false, "SABnzbd API key not configured in client settings");

                var url = $"{baseUrl}?mode=version&output=json&apikey={Uri.EscapeDataString(apiKey)}";
                var http = _httpFactory.CreateClient(ClientType);
                var resp = await http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    // Map common statuses to simple, actionable messages
                    if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                    {
                        return (false, "SABnzbd: API key invalid or unauthorized");
                    }

                    if (resp.StatusCode == HttpStatusCode.NotFound)
                    {
                        return (false, "SABnzbd: host or endpoint not found (check host/port)");
                    }

                    return (false, $"SABnzbd: returned {resp.StatusCode}");
                }

                return (true, "SABnzbd: connected");
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogDebug(httpEx, "SABnzbd TestConnection network error");
                return (false, $"SABnzbd: network error ({httpEx.StatusCode?.ToString() ?? "unavailable"})");
            }
            catch (TaskCanceledException tce)
            {
                _logger.LogDebug(tce, "SABnzbd TestConnection timed out");
                return (false, "SABnzbd: connection timed out");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "SABnzbd TestConnection failed");
                return (false, "SABnzbd: connection failed");
            }
        }

        public async Task<string?> AddAsync(DownloadClientConfiguration client, SearchResult result, CancellationToken ct = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (result == null) throw new ArgumentNullException(nameof(result));

            try
            {
                var baseUrl = DownloadClientUriBuilder.BuildUri(client, "/api").ToString();

                // Get API key
                var apiKey = "";
                if (client.Settings != null && client.Settings.TryGetValue("apiKey", out var apiKeyObj))
                {
                    apiKey = apiKeyObj?.ToString() ?? "";
                }

                if (string.IsNullOrEmpty(apiKey))
                    throw new Exception("SABnzbd API key not configured");

                var (nzbUrl, indexerApiKey) = await _nzbUrlResolver.ResolveAsync(result, ct);
                if (string.IsNullOrEmpty(nzbUrl))
                    throw new Exception("No NZB URL found in search result");

                _logger.LogInformation("Sending NZB to SABnzbd: {Title} from {Source}", LogRedaction.SanitizeText(result.Title), LogRedaction.SanitizeText(result.Source));

                var sensitiveValues = LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { apiKey }).ToList();
                if (!string.IsNullOrEmpty(indexerApiKey)) sensitiveValues.Add(indexerApiKey);

                var queryParams = new Dictionary<string, string>
                {
                    { "mode", "addurl" },
                    { "name", nzbUrl },
                    { "apikey", apiKey },
                    { "output", "json" },
                    { "nzbname", result.Title }
                };

                if (client.Settings != null && client.Settings.TryGetValue("recentPriority", out var priorityObj))
                {
                    var priority = priorityObj?.ToString();
                    if (!string.IsNullOrEmpty(priority) && priority != "default")
                    {
                        queryParams["priority"] = priority switch
                        {
                            "force" => "2",
                            "high" => "1",
                            "normal" => "0",
                            "low" => "-1",
                            _ => "0"
                        };
                    }
                }

                var category = "audiobooks";
                if (client.Settings != null && client.Settings.TryGetValue("category", out var categoryObj))
                {
                    var configuredCategory = categoryObj?.ToString();
                    if (!string.IsNullOrEmpty(configuredCategory))
                        category = configuredCategory;
                }
                queryParams["cat"] = category;

                var queryString = string.Join("&", queryParams.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));
                var requestUrl = $"{baseUrl}?{queryString}";

                _logger.LogDebug("SABnzbd request URL: {Url}", LogRedaction.RedactText(requestUrl, sensitiveValues));

                var http = _httpFactory.CreateClient(ClientType);
                var response = await http.GetAsync(requestUrl, ct);
                var responseContent = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    var redacted = LogRedaction.RedactText(responseContent, sensitiveValues);
                    _logger.LogError("SABnzbd returned error status {Status}: {Content}", response.StatusCode, redacted);
                    throw new Exception($"SABnzbd returned status {response.StatusCode}: {redacted}");
                }

                if (string.IsNullOrWhiteSpace(responseContent))
                {
                    _logger.LogWarning("SABnzbd returned empty response body when adding NZB: {Url}", LogRedaction.RedactText(requestUrl, sensitiveValues));
                    return null;
                }

                var jsonDoc = JsonDocument.Parse(responseContent);
                var root = jsonDoc.RootElement;

                if (root.TryGetProperty("error", out var errorElement))
                {
                    var errorMsg = errorElement.GetString();
                    if (!string.IsNullOrEmpty(errorMsg))
                        throw new Exception($"SABnzbd error: {errorMsg}");
                }

                string downloadId = "";
                if (root.TryGetProperty("nzo_ids", out var nzoIds) && nzoIds.ValueKind == JsonValueKind.Array)
                {
                    var firstId = nzoIds.EnumerateArray().FirstOrDefault();
                    downloadId = firstId.GetString() ?? Guid.NewGuid().ToString();
                }
                else
                {
                    downloadId = Guid.NewGuid().ToString();
                }

                _logger.LogInformation("Successfully added NZB to SABnzbd with ID: {DownloadId}", LogRedaction.SanitizeText(downloadId));
                return downloadId;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to send NZB to SABnzbd");
                throw;
            }
        }

        public async Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            try
            {
                var baseUrl = DownloadClientUriBuilder.BuildUri(client, "/api").ToString();

                var apiKey = "";
                if (client.Settings != null && client.Settings.TryGetValue("apiKey", out var apiKeyObj))
                {
                    apiKey = apiKeyObj?.ToString() ?? "";
                }

                if (string.IsNullOrEmpty(apiKey))
                {
                    _logger.LogWarning("SABnzbd API key not configured for {ClientName}", client.Name);
                    return false;
                }

                var http = _httpFactory.CreateClient(ClientType);
                bool removedFromQueue = false;
                bool removedFromHistory = false;

                // Try to remove from queue first (for active downloads)
                var queueRemoveUrl = $"{baseUrl}?mode=queue&name=delete&value={Uri.EscapeDataString(id)}&apikey={Uri.EscapeDataString(apiKey)}&output=json";
                if (deleteFiles)
                    queueRemoveUrl += "&del_files=1";

                try
                {
                    var queueResponse = await http.GetAsync(queueRemoveUrl, ct);
                    if (queueResponse.IsSuccessStatusCode)
                    {
                        var queueContent = await queueResponse.Content.ReadAsStringAsync(ct);
                        var queueDoc = JsonDocument.Parse(queueContent);
                        if (queueDoc.RootElement.TryGetProperty("status", out var queueStatus))
                        {
                            removedFromQueue = queueStatus.GetBoolean();
                        }
                    }
                }
                catch (Exception queueEx) when (queueEx is not OperationCanceledException && queueEx is not OutOfMemoryException && queueEx is not StackOverflowException)
                {
                    _logger.LogDebug(queueEx, "Could not remove {DownloadId} from SABnzbd queue (may not be in queue)", id);
                }

                // Try to remove from history (for completed downloads)
                var historyRemoveUrl = $"{baseUrl}?mode=history&name=delete&value={Uri.EscapeDataString(id)}&apikey={Uri.EscapeDataString(apiKey)}&output=json";
                if (deleteFiles)
                    historyRemoveUrl += "&del_files=1";

                try
                {
                    var historyResponse = await http.GetAsync(historyRemoveUrl, ct);
                    if (historyResponse.IsSuccessStatusCode)
                    {
                        var historyContent = await historyResponse.Content.ReadAsStringAsync(ct);
                        var historyDoc = JsonDocument.Parse(historyContent);
                        if (historyDoc.RootElement.TryGetProperty("status", out var historyStatus))
                        {
                            removedFromHistory = historyStatus.GetBoolean();
                        }
                    }
                }
                catch (Exception historyEx) when (historyEx is not OperationCanceledException && historyEx is not OutOfMemoryException && historyEx is not StackOverflowException)
                {
                    _logger.LogDebug(historyEx, "Could not remove {DownloadId} from SABnzbd history (may not be in history)", id);
                }

                var success = removedFromQueue || removedFromHistory;
                if (success)
                {
                    _logger.LogInformation("Removed {DownloadId} from SABnzbd (queue: {Queue}, history: {History}, deleteFiles: {DeleteFiles})",
                        LogRedaction.SanitizeText(id), removedFromQueue, removedFromHistory, deleteFiles);
                }
                else
                {
                    _logger.LogWarning("Failed to remove {DownloadId} from SABnzbd (not found in queue or history)", LogRedaction.SanitizeText(id));
                }

                return success;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error removing from SABnzbd: {DownloadId}", LogRedaction.SanitizeText(id));
                return false;
            }
        }

        public async Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var items = new List<QueueItem>();
            if (client == null) return items;

            var configuredCategory = DownloadClientCategoryFilter.GetConfiguredCategory(client);

            try
            {
                var baseUrl = DownloadClientUriBuilder.BuildUri(client, "/api").ToString();
                var apiKey = "";
                if (client.Settings != null && client.Settings.TryGetValue("apiKey", out var apiKeyObj))
                {
                    apiKey = apiKeyObj?.ToString() ?? "";
                }

                if (string.IsNullOrEmpty(apiKey))
                {
                    _logger.LogWarning("SABnzbd API key not configured for {ClientName}", client.Name);
                    return items;
                }

                var requestUrl = $"{baseUrl}?mode=queue&output=json&apikey={Uri.EscapeDataString(apiKey)}";
                _logger.LogDebug("SABnzbd queue request (redacted): {Url}", LogRedaction.RedactText(requestUrl, LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { apiKey })));

                var http = _httpFactory.CreateClient(ClientType);
                var response = await http.GetAsync(requestUrl, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("SABnzbd queue request failed with status {Status}", response.StatusCode);
                    return items;
                }

                var jsonContent = await response.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(jsonContent))
                {
                    _logger.LogWarning("SABnzbd returned empty response for client {ClientName}", LogRedaction.SanitizeText(client.Name));
                    return items;
                }

                var doc = JsonDocument.Parse(jsonContent);
                if (!doc.RootElement.TryGetProperty("queue", out var queue)) return items;
                if (!queue.TryGetProperty("slots", out var slots) || slots.ValueKind != JsonValueKind.Array) return items;

                foreach (var slot in slots.EnumerateArray())
                {
                    try
                    {
                        var nzoId = slot.TryGetProperty("nzo_id", out var id) ? id.GetString() ?? "" : "";
                        var filename = slot.TryGetProperty("filename", out var fn) ? fn.GetString() ?? "Unknown" : "Unknown";
                        var status = slot.TryGetProperty("status", out var st) ? st.GetString() ?? "Unknown" : "Unknown";

                        double ParseNumericValue(JsonElement element)
                        {
                            if (element.ValueKind == JsonValueKind.Number)
                                return element.GetDouble();
                            if (element.ValueKind == JsonValueKind.String)
                            {
                                var str = element.GetString() ?? "0";
                                if (double.TryParse(str, out var value))
                                    return value;
                            }
                            return 0;
                        }

                        var sizeMB = slot.TryGetProperty("mb", out var mb) ? ParseNumericValue(mb) : 0;
                        var mbLeft = slot.TryGetProperty("mbleft", out var left) ? ParseNumericValue(left) : 0;
                        var downloadedMB = sizeMB - mbLeft;
                        var percentage = slot.TryGetProperty("percentage", out var pct) ? ParseNumericValue(pct) : 0;

                        var timeLeft = slot.TryGetProperty("timeleft", out var time) ? time.GetString() ?? "0:00:00" : "0:00:00";
                        var category = slot.TryGetProperty("cat", out var cat) ? cat.GetString() ?? "" : "";

                        if (!DownloadClientCategoryFilter.Matches(configuredCategory, category))
                        {
                            continue;
                        }

                        int etaSeconds = 0;
                        if (!string.IsNullOrEmpty(timeLeft) && timeLeft != "0:00:00")
                        {
                            etaSeconds = ParseSABnzbdTimeLeft(timeLeft);
                        }

                        var sizeBytes = (long)(sizeMB * 1024 * 1024);
                        var downloadedBytes = (long)(downloadedMB * 1024 * 1024);

                        var speed = 0.0;
                        if (queue.TryGetProperty("speed", out var speedProp))
                        {
                            var speedStr = speedProp.GetString() ?? "0";
                            speed = ParseSABnzbdSpeed(speedStr);
                        }

                        var mappedStatus = status.ToLower() switch
                        {
                            "downloading" => "downloading",
                            "queued" => "queued",
                            "paused" => "paused",
                            "checking" => "downloading",
                            "extracting" => "downloading",
                            "moving" => "downloading",
                            "completed" => "completed",
                            "failed" => "failed",
                            _ => "queued"
                        };

                        var remotePath = client.DownloadPath ?? "";
                        var localPath = remotePath;

                        // For SABnzbd, construct ContentPath from download path + filename
                        var contentPath = !string.IsNullOrEmpty(remotePath) && !string.IsNullOrEmpty(filename)
                            ? CombineWithOptionalBase(remotePath, filename)
                            : remotePath;
                        var localContentPath = contentPath;

                        items.Add(new QueueItem
                        {
                            Id = nzoId,
                            Title = filename,
                            Quality = category,
                            Status = mappedStatus,
                            Progress = percentage,
                            Size = sizeBytes,
                            Downloaded = downloadedBytes,
                            DownloadSpeed = speed,
                            Eta = etaSeconds > 0 ? etaSeconds : null,
                            DownloadClient = client.Name,
                            DownloadClientId = client.Id,
                            DownloadClientType = "sabnzbd",
                            AddedAt = DateTime.UtcNow,
                            CanPause = mappedStatus == "downloading" || mappedStatus == "queued",
                            CanRemove = true,
                            RemotePath = remotePath,
                            LocalPath = localPath,
                            ContentPath = localContentPath
                        });
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogError(ex, "Error parsing SABnzbd queue item");
                    }
                }

                _logger.LogInformation("Retrieved {Count} items from SABnzbd active queue", items.Count);

                // Also fetch completed items from SABnzbd history — SABnzbd moves finished
                // downloads out of the queue into history, so without this the
                // CompletedDownloadHandlingService can never find them for import/removal.
                var existingNzoIds = new HashSet<string>(items.Select(i => i.Id), StringComparer.OrdinalIgnoreCase);
                try
                {
                    var historyUrl = $"{baseUrl}?mode=history&output=json&limit=30&apikey={Uri.EscapeDataString(apiKey)}";
                    var historyResp = await http.GetAsync(historyUrl, ct);
                    if (historyResp.IsSuccessStatusCode)
                    {
                        var historyText = await historyResp.Content.ReadAsStringAsync(ct);
                        if (!string.IsNullOrWhiteSpace(historyText))
                        {
                            var histDoc = JsonDocument.Parse(historyText);
                            if (histDoc.RootElement.TryGetProperty("history", out var history) &&
                                history.TryGetProperty("slots", out var histSlots) &&
                                histSlots.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var slot in histSlots.EnumerateArray())
                                {
                                    try
                                    {
                                        var nzoId = slot.TryGetProperty("nzo_id", out var hid) ? hid.GetString() ?? "" : "";
                                        if (string.IsNullOrEmpty(nzoId) || existingNzoIds.Contains(nzoId))
                                            continue;

                                        var histStatus = slot.TryGetProperty("status", out var hst) ? hst.GetString() ?? "" : "";
                                        var histName = slot.TryGetProperty("name", out var hn) ? hn.GetString() ?? "Unknown" : "Unknown";
                                        var histCategory = slot.TryGetProperty("category", out var hcat) ? hcat.GetString() ?? "" : "";
                                        var histBytes = slot.TryGetProperty("bytes", out var hb) && hb.TryGetInt64(out var hbl) ? hbl : 0L;
                                        var storagePath = slot.TryGetProperty("storage", out var sp) ? sp.GetString() ?? "" : "";

                                        if (!DownloadClientCategoryFilter.Matches(configuredCategory, histCategory))
                                            continue;

                                        var mappedStatus = histStatus.ToLower() switch
                                        {
                                            "completed" => "completed",
                                            "failed" => "failed",
                                            _ => "completed"
                                        };

                                        var remotePath = !string.IsNullOrEmpty(storagePath) ? storagePath : (client.DownloadPath ?? "");
                                        var localPath = remotePath;

                                        // Parse completed timestamp
                                        DateTime? completedAt = null;
                                        if (slot.TryGetProperty("completed", out var compEpoch) && compEpoch.TryGetInt64(out var epoch))
                                        {
                                            completedAt = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
                                        }

                                        items.Add(new QueueItem
                                        {
                                            Id = nzoId,
                                            Title = histName,
                                            Quality = histCategory,
                                            Status = mappedStatus,
                                            Progress = mappedStatus == "completed" ? 100 : 0,
                                            Size = histBytes,
                                            Downloaded = histBytes,
                                            DownloadSpeed = 0,
                                            Eta = null,
                                            DownloadClient = client.Name,
                                            DownloadClientId = client.Id,
                                            DownloadClientType = "sabnzbd",
                                            AddedAt = completedAt ?? DateTime.UtcNow,
                                            CompletionTime = completedAt,
                                            CanPause = false,
                                            CanRemove = true,
                                            RemotePath = remotePath,
                                            LocalPath = localPath,
                                            ContentPath = localPath
                                        });
                                    }
                                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                    {
                                        _logger.LogDebug(ex, "Error parsing SABnzbd history item");
                                    }
                                }

                                _logger.LogInformation("Retrieved {Count} total items from SABnzbd (queue + history)", items.Count);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogDebug(ex, "Failed to fetch SABnzbd history for queue enrichment (non-fatal)");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error getting SABnzbd queue");
            }

            return items;
        }

        public async Task<List<(string Id, string Name)>> GetRecentHistoryAsync(DownloadClientConfiguration client, int limit = 100, CancellationToken ct = default)
        {
            var result = new List<(string Id, string Name)>();
            if (client == null) return result;

            try
            {
                var baseUrl = DownloadClientUriBuilder.BuildUri(client, "/api").ToString();
                var apiKey = "";
                if (client.Settings != null && client.Settings.TryGetValue("apiKey", out var apiKeyObj))
                {
                    apiKey = apiKeyObj?.ToString() ?? "";
                }
                if (string.IsNullOrEmpty(apiKey)) return result;

                var historyUrl = $"{baseUrl}?mode=history&output=json&limit={limit}&apikey={Uri.EscapeDataString(apiKey)}";
                var http = _httpFactory.CreateClient(ClientType);
                var historyResp = await http.GetAsync(historyUrl, ct);
                if (!historyResp.IsSuccessStatusCode) return result;

                var historyText = await historyResp.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(historyText)) return result;

                var doc = JsonDocument.Parse(historyText);
                if (doc.RootElement.TryGetProperty("history", out var history) && history.TryGetProperty("slots", out var slots) && slots.ValueKind == JsonValueKind.Array)
                {
                    foreach (var slot in slots.EnumerateArray())
                    {
                        var nzoId = slot.TryGetProperty("nzo_id", out var nzo) ? nzo.GetString() ?? string.Empty : string.Empty;
                        var name = slot.TryGetProperty("name", out var nm) ? nm.GetString() ?? string.Empty : string.Empty;
                        result.Add((nzoId, name));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to fetch SABnzbd history (non-fatal)");
            }

            return result;
        }

        /// <summary>
        /// Get all downloads as standardized DownloadClientItem objects
        /// </summary>
        public async Task<List<DownloadClientItem>> GetItemsAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var items = new List<DownloadClientItem>();
            if (client == null) return items;

            var configuredCategory = DownloadClientCategoryFilter.GetConfiguredCategory(client);

            try
            {
                var baseUrl = DownloadClientUriBuilder.BuildUri(client, "/api").ToString();
                var apiKey = "";
                if (client.Settings != null && client.Settings.TryGetValue("apiKey", out var apiKeyObj))
                {
                    apiKey = apiKeyObj?.ToString() ?? "";
                }
                if (string.IsNullOrEmpty(apiKey))
                {
                    _logger.LogWarning("SABnzbd API key not configured for client {ClientName}", LogRedaction.SanitizeText(client.Name));
                    return items;
                }

                var requestUrl = $"{baseUrl}?mode=queue&output=json&apikey={Uri.EscapeDataString(apiKey)}";
                var http = _httpFactory.CreateClient(ClientType);
                var response = await http.GetAsync(requestUrl, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("SABnzbd queue request failed with status {Status}", response.StatusCode);
                    return items;
                }

                var jsonContent = await response.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(jsonContent))
                {
                    _logger.LogWarning("SABnzbd returned empty response for client {ClientName}", LogRedaction.SanitizeText(client.Name));
                    return items;
                }

                var doc = JsonDocument.Parse(jsonContent);
                if (!doc.RootElement.TryGetProperty("queue", out var queue)) return items;
                if (!queue.TryGetProperty("slots", out var slots) || slots.ValueKind != JsonValueKind.Array) return items;

                var queueSpeed = 0.0;
                if (queue.TryGetProperty("speed", out var speedProp))
                {
                    var speedStr = speedProp.GetString() ?? "0";
                    queueSpeed = ParseSABnzbdSpeed(speedStr);
                }

                foreach (var slot in slots.EnumerateArray())
                {
                    try
                    {
                        var nzoId = slot.TryGetProperty("nzo_id", out var id) ? id.GetString() ?? "" : "";
                        var filename = slot.TryGetProperty("filename", out var fn) ? fn.GetString() ?? "Unknown" : "Unknown";
                        var status = slot.TryGetProperty("status", out var st) ? st.GetString() ?? "Unknown" : "Unknown";

                        double ParseNumericValue(JsonElement element)
                        {
                            if (element.ValueKind == JsonValueKind.Number)
                                return element.GetDouble();
                            if (element.ValueKind == JsonValueKind.String)
                            {
                                var str = element.GetString() ?? "0";
                                if (double.TryParse(str, out var value))
                                    return value;
                            }
                            return 0;
                        }

                        var sizeMB = slot.TryGetProperty("mb", out var mb) ? ParseNumericValue(mb) : 0;
                        var mbLeft = slot.TryGetProperty("mbleft", out var left) ? ParseNumericValue(left) : 0;
                        var percentage = slot.TryGetProperty("percentage", out var pct) ? ParseNumericValue(pct) : 0;

                        var timeLeft = slot.TryGetProperty("timeleft", out var time) ? time.GetString() ?? "0:00:00" : "0:00:00";
                        var category = slot.TryGetProperty("cat", out var cat) ? cat.GetString() ?? "" : "";

                        if (!DownloadClientCategoryFilter.Matches(configuredCategory, category))
                        {
                            continue;
                        }

                        int etaSeconds = 0;
                        if (!string.IsNullOrEmpty(timeLeft) && timeLeft != "0:00:00")
                        {
                            etaSeconds = ParseSABnzbdTimeLeft(timeLeft);
                        }

                        var sizeBytes = (long)(sizeMB * 1024 * 1024);
                        var remainingBytes = (long)(mbLeft * 1024 * 1024);

                        // Map SABnzbd status to DownloadItemStatus
                        var mappedStatus = status.ToLower() switch
                        {
                            "downloading" => DownloadItemStatus.Downloading,
                            "queued" => DownloadItemStatus.Queued,
                            "paused" => DownloadItemStatus.Paused,
                            "checking" => DownloadItemStatus.Downloading,
                            "extracting" => DownloadItemStatus.Downloading,
                            "moving" => DownloadItemStatus.Downloading,
                            "completed" => DownloadItemStatus.Completed,
                            "failed" => DownloadItemStatus.Failed,
                            _ => DownloadItemStatus.Queued
                        };

                        var remotePath = client.DownloadPath ?? "";
                        var contentPath = !string.IsNullOrEmpty(remotePath) && !string.IsNullOrEmpty(filename)
                            ? CombineWithOptionalBase(remotePath, filename)
                            : remotePath;
                        var localContentPath = contentPath;

                        TimeSpan? remainingTime = etaSeconds > 0 ? TimeSpan.FromSeconds(etaSeconds) : null;

                        items.Add(new DownloadClientItem
                        {
                            DownloadId = nzoId.ToUpperInvariant(), // SABnzbd uses nzo_id as unique identifier
                            Title = filename,
                            Category = category,
                            Status = mappedStatus,
                            TotalSize = sizeBytes,
                            RemainingSize = remainingBytes,
                            RemainingTime = remainingTime,
                            OutputPath = localContentPath,
                            Message = status,
                            Progress = percentage,
                            DownloadSpeed = queueSpeed, // SABnzbd provides global speed
                            CanBeRemoved = true,
                            CanMoveFiles = mappedStatus == DownloadItemStatus.Completed,
                            DownloadClientInfo = DownloadClientItemClientInfo.FromClient(
                                clientId: client.Id,
                                clientName: client.Name,
                                clientType: "sabnzbd",
                                protocol: DownloadProtocol.Usenet,
                                removeCompletedDownloads: client.Settings?.TryGetValue("removeCompletedDownloads", out var removeVal) is true &&
                                                         (removeVal is bool boolVal && boolVal),
                                hasPostImportCategory: !string.IsNullOrEmpty(client.Settings?.GetValueOrDefault("postImportCategory")?.ToString())
                            )
                        });
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogError(ex, "Error parsing SABnzbd queue item");
                    }
                }

                _logger.LogInformation("Retrieved {Count} items from SABnzbd queue", items.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error getting SABnzbd items");
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

            try
            {
                // Query SABnzbd history for the download
                var baseUrl = DownloadClientUriBuilder.BuildUri(client, "/api").ToString();
                var apiKey = "";
                if (client.Settings != null && client.Settings.TryGetValue("apiKey", out var apiKeyObj))
                {
                    apiKey = apiKeyObj?.ToString() ?? "";
                }

                if (string.IsNullOrEmpty(apiKey))
                {
                    _logger.LogWarning("SABnzbd API key not configured for client {ClientId}", client.Id);
                    return result;
                }

                // Query history with nzo_id filter
                var historyUrl = $"{baseUrl}?mode=history&output=json&apikey={Uri.EscapeDataString(apiKey)}";
                var http = _httpFactory.CreateClient(ClientType);
                var historyResp = await http.GetAsync(historyUrl, ct);

                if (!historyResp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to query SABnzbd history for download {NzoId}", item.DownloadId);
                    return result;
                }

                var historyText = await historyResp.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(historyText))
                {
                    return result;
                }

                var doc = JsonDocument.Parse(historyText);
                if (!doc.RootElement.TryGetProperty("history", out var history) ||
                    !history.TryGetProperty("slots", out var slots) ||
                    slots.ValueKind != JsonValueKind.Array)
                {
                    _logger.LogWarning("Invalid SABnzbd history response format");
                    return result;
                }

                // Find matching history entry (case-insensitive comparison)
                foreach (var slot in slots.EnumerateArray())
                {
                    var nzoId = slot.TryGetProperty("nzo_id", out var nzo) ? nzo.GetString() ?? string.Empty : string.Empty;
                    if (!string.Equals(nzoId, item.DownloadId, StringComparison.OrdinalIgnoreCase)) continue;

                    // Extract storage path
                    var storage = slot.TryGetProperty("storage", out var storageProp) ? storageProp.GetString() : null;
                    if (string.IsNullOrEmpty(storage))
                    {
                        _logger.LogWarning("No storage path found for SABnzbd download {NzoId}", item.DownloadId);
                        return result;
                    }

                    // Apply path mapping
                    var localContentPath = storage;
                    result.OutputPath = localContentPath;

                    _logger.LogDebug(
                        "Resolved SABnzbd content path for {NzoId}: {ContentPath}",
                        item.DownloadId,
                        localContentPath);

                    return result;
                }

                _logger.LogWarning("Download {NzoId} not found in SABnzbd history", item.DownloadId);
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Error resolving import item for SABnzbd download {NzoId}", item.DownloadId);
                return result;
            }
        }

        private int ParseSABnzbdTimeLeft(string timeLeft)
        {
            try
            {
                var totalSeconds = 0;

                if (timeLeft.Contains("day"))
                {
                    var parts = timeLeft.Split(new[] { " day ", " days " }, StringSplitOptions.None);
                    if (parts.Length == 2 && int.TryParse(parts[0], out var days))
                    {
                        totalSeconds += days * 86400;
                        timeLeft = parts[1];
                    }
                }

                var timeParts = timeLeft.Split(':');
                if (timeParts.Length == 3)
                {
                    if (int.TryParse(timeParts[0], out var hours))
                        totalSeconds += hours * 3600;
                    if (int.TryParse(timeParts[1], out var minutes))
                        totalSeconds += minutes * 60;
                    if (int.TryParse(timeParts[2], out var seconds))
                        totalSeconds += seconds;
                }

                return totalSeconds;
            }
            catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException)
            {
                return 0;
            }
        }

        private double ParseSABnzbdSpeed(string speedStr)
        {
            try
            {
                var parts = speedStr.Trim().Split(' ');
                if (parts.Length != 2)
                    return 0;

                if (!double.TryParse(parts[0], out var value))
                    return 0;

                var unit = parts[1].ToUpper();
                return unit switch
                {
                    "B" => value,
                    "K" => value * 1024,
                    "M" => value * 1024 * 1024,
                    "G" => value * 1024 * 1024 * 1024,
                    _ => 0
                };
            }
            catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException)
            {
                return 0;
            }
        }

        /// <summary>
        /// Resolves the actual import item for a completed download.
        /// Queries SABnzbd history for storage path.
        /// Matches Sabnzbd.GetImportItem pattern.
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

            // If ContentPath is already set and exists, use it
            if (!string.IsNullOrEmpty(result.ContentPath))
            {
                var localPath = result.ContentPath;
                if (!string.IsNullOrEmpty(localPath) && (File.Exists(localPath) || Directory.Exists(localPath)))
                {
                    result.ContentPath = localPath;
                    return result;
                }
            }

            try
            {
                // Query SABnzbd history for the download
                var baseUrl = DownloadClientUriBuilder.BuildUri(client, "/api").ToString();
                var apiKey = "";
                if (client.Settings != null && client.Settings.TryGetValue("apiKey", out var apiKeyObj))
                {
                    apiKey = apiKeyObj?.ToString() ?? "";
                }

                if (string.IsNullOrEmpty(apiKey))
                {
                    _logger.LogWarning("SABnzbd API key not configured for client {ClientId}", client.Id);
                    return result;
                }

                // Query history with nzo_id filter
                var historyUrl = $"{baseUrl}?mode=history&output=json&apikey={Uri.EscapeDataString(apiKey)}";
                var http = _httpFactory.CreateClient(ClientType);
                var historyResp = await http.GetAsync(historyUrl, ct);

                if (!historyResp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to query SABnzbd history for download {NzoId}", queueItem.Id);
                    return result;
                }

                var historyText = await historyResp.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(historyText))
                {
                    return result;
                }

                var doc = JsonDocument.Parse(historyText);
                if (!doc.RootElement.TryGetProperty("history", out var history) ||
                    !history.TryGetProperty("slots", out var slots) ||
                    slots.ValueKind != JsonValueKind.Array)
                {
                    _logger.LogWarning("Invalid SABnzbd history response format");
                    return result;
                }

                // Find matching history entry
                foreach (var slot in slots.EnumerateArray())
                {
                    var nzoId = slot.TryGetProperty("nzo_id", out var nzo) ? nzo.GetString() ?? string.Empty : string.Empty;
                    if (nzoId != queueItem.Id) continue;

                    // Extract storage path
                    var storage = slot.TryGetProperty("storage", out var storageProp) ? storageProp.GetString() : null;
                    if (string.IsNullOrEmpty(storage))
                    {
                        _logger.LogWarning("No storage path found for SABnzbd download {NzoId}", queueItem.Id);
                        return result;
                    }

                    result.ContentPath = storage;
                    _logger.LogDebug($"Resolved SABnzbd content path for {queueItem.Id}: {result.ContentPath}");

                    return result;
                }

                _logger.LogWarning("Download {NzoId} not found in SABnzbd history", queueItem.Id);
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Error resolving import item for SABnzbd download {NzoId}", queueItem.Id);
                return result;
            }
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

        public async Task<List<Download>> FetchDownloadsAsync(
            DownloadClientConfiguration client,
            List<Download> downloads,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug("Polling SABnzbd client {ClientName}", client.Name);
            try
            {
                var baseUrl = DownloadClientUriBuilder.BuildUri(client, "/api").ToString();

                using var http = _httpFactory.CreateClient(ClientType);

                // Get API key from settings
                var apiKey = "";
                if (client.Settings != null && client.Settings.TryGetValue("apiKey", out var apiKeyObj))
                {
                    apiKey = apiKeyObj?.ToString() ?? "";
                }

                if (string.IsNullOrEmpty(apiKey))
                {
                    throw new DownloadClientAdapterPollingException($"SABnzbd API key not configured for client {client.Id}");
                }

                // Poll SABnzbd queue for active downloads progress updates
                var queueUrl = $"{baseUrl}?mode=queue&output=json&apikey={Uri.EscapeDataString(apiKey)}";
                // Redacted queue URL for safe diagnostics
                _logger.LogDebug("SABnzbd poll queue URL (redacted): {Url}", LogRedaction.RedactText(queueUrl, LogRedaction.GetSensitiveValuesFromEnvironment().Concat([apiKey])));
                using var queueResponse = await http.GetAsync(queueUrl, cancellationToken);

                if (queueResponse.IsSuccessStatusCode)
                {
                    var queueJson = await queueResponse.Content.ReadAsStringAsync(cancellationToken);
                    var queueDoc = JsonDocument.Parse(queueJson);

                    if (queueDoc.RootElement.TryGetProperty("queue", out var queue) &&
                        queue.TryGetProperty("slots", out var queueSlots) &&
                        queueSlots.ValueKind == JsonValueKind.Array)
                    {
                        foreach (Download download in downloads)
                        {
                            var clientDownloadId = download.GetClientDownloadItemId();

                            foreach (var slot in queueSlots.EnumerateArray())
                            {
                                try
                                {
                                    var nzoId = slot.TryGetProperty("nzo_id", out var nzoIdProp) ? nzoIdProp.GetString() ?? "" : "";
                                    if (!string.IsNullOrEmpty(clientDownloadId) && !string.Equals(nzoId, clientDownloadId, StringComparison.OrdinalIgnoreCase))
                                    {
                                        continue;
                                    }

                                    var filename = slot.TryGetProperty("filename", out var filenameProp) ? filenameProp.GetString() ?? "" : "";
                                    if (!TitleUtils.AreTitlesSimilar(download.Title, filename))
                                    {
                                        continue;
                                    }

                                    // SABnzbd sometimes returns numeric values as numbers or strings.
                                    // Be defensive and accept either JSON number or JSON string.
                                    double GetDoubleValue(System.Text.Json.JsonElement el)
                                    {
                                        try
                                        {
                                            if (el.ValueKind == System.Text.Json.JsonValueKind.Number)
                                                return el.GetDouble();

                                            if (el.ValueKind == System.Text.Json.JsonValueKind.String)
                                            {
                                                var s = el.GetString();
                                                if (double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v))
                                                    return v;
                                            }
                                        }
                                        catch (Exception caughtEx_10) when (caughtEx_10 is not OperationCanceledException && caughtEx_10 is not OutOfMemoryException && caughtEx_10 is not StackOverflowException)
                                        {
                                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                        }

                                        return 0.0;
                                    }

                                    var percentage = slot.TryGetProperty("percentage", out var percentageProp) ? GetDoubleValue(percentageProp) : 0.0;
                                    var mbleft = slot.TryGetProperty("mbleft", out var mbleftProp) ? GetDoubleValue(mbleftProp) : 0.0;
                                    var status = slot.TryGetProperty("status", out var statusProp) ? statusProp.GetString() ?? "" : "";

                                    // Calculate progress and update
                                    // percentage is provided by SABnzbd as a percent (e.g. 50.0). Our UpdateDownloadProgressAsync
                                    // expects a percentage in the 0..100 range. Use the percentage directly.
                                    var progressPercent = percentage; // 0..100

                                    // Convert sizes from MB -> bytes
                                    var amountLeft = (long)(mbleft * 1024 * 1024);

                                    // Update progress using percent and amountLeft (UpdateDownloadProgressAsync uses percent->downloaded size calculation when TotalSize is set)
                                    AdapterUtils.MapDownloadProgress(download, progressPercent, amountLeft, status);
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                {
                                    _logger.LogWarning(ex, "Error updating SABnzbd queue progress for slot");
                                }
                            }
                        }
                    }
                }

                // Get completed downloads (history) - limit to recent items
                var historyUrl = $"{baseUrl}?mode=history&limit=100&output=json&apikey={Uri.EscapeDataString(apiKey)}";
                // Redacted history URL for safe diagnostics
                _logger.LogDebug("SABnzbd history URL (redacted): {Url}", LogRedaction.RedactText(historyUrl, LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { apiKey })));
                using var historyResponse = await http.GetAsync(historyUrl, cancellationToken);

                if (!historyResponse.IsSuccessStatusCode)
                {
                    throw new DownloadClientAdapterPollingException($"Failed to fetch SABnzbd history for {client.Id}: {historyResponse.StatusCode}");
                }

                var historyJson = await historyResponse.Content.ReadAsStringAsync(cancellationToken);
                var historyDoc = System.Text.Json.JsonDocument.Parse(historyJson);

                if (!historyDoc.RootElement.TryGetProperty("history", out var history) ||
                    !history.TryGetProperty("slots", out var slots) ||
                    slots.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    throw new DownloadClientAdapterPollingException($"No history data found for SABnzbd client {client.Id}");
                }

                // Build a lookup of completed items for faster matching
                // Include nzo_id when available so we can match downloads by ID as well
                var completedItems = new List<(string Name, string Status, string Path, DateTime CompletedTime, string NzoId)>();
                var failedItems = new List<(string Name, string Status, string Path, DateTime CompletedTime, string NzoId, string Error)>();

                foreach (var slot in slots.EnumerateArray())
                {
                    var name = slot.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                    var status = slot.TryGetProperty("status", out var statusProp) ? statusProp.GetString() ?? "" : "";
                    var path = slot.TryGetProperty("storage", out var pathProp) ? pathProp.GetString() ?? "" : "";
                    var nzoId = slot.TryGetProperty("nzo_id", out var nzoIdProp) ? nzoIdProp.GetString() ?? "" : "";

                    // Parse completion time
                    var completedTime = DateTime.MinValue;
                    if (slot.TryGetProperty("completed", out var completedProp))
                    {
                        var completedTimestamp = completedProp.GetInt64();
                        completedTime = DateTimeOffset.FromUnixTimeSeconds(completedTimestamp).DateTime;
                    }

                    if (!string.IsNullOrEmpty(name) &&
                        (status.Equals("Completed", StringComparison.OrdinalIgnoreCase) ||
                            status.Equals("Complete", StringComparison.OrdinalIgnoreCase)))
                    {
                        _logger.LogInformation("SABnzbd history slot parsed: nzo_id={NzoId}, name={Name}, status={Status}, path={Path}, completed={Completed}", nzoId, LogRedaction.SanitizeText(name), LogRedaction.SanitizeText(status), LogRedaction.SanitizeFilePath(path), completedTime);

                        completedItems.Add((name, status, path, completedTime, nzoId));
                    }
                    else if (!string.IsNullOrEmpty(name) && status.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                    {
                        var failMessage = slot.TryGetProperty("fail_message", out var failProp)
                            ? failProp.GetString() ?? string.Empty
                            : status;

                        failedItems.Add((name, status, path, completedTime, nzoId, failMessage));
                    }
                }

                _logger.LogDebug("Found {CompletedCount} completed items in SABnzbd history for client {ClientName}",
                    completedItems.Count, client.Name);

                // Check each download against completed items
                foreach (var dl in downloads)
                {
                    // Skip downloads that are already being processed, awaiting import,
                    // or fully imported to avoid duplicate finalization/notifications.
                    if (dl.Status == DownloadStatus.Moved ||
                        dl.Status == DownloadStatus.Processing ||
                        dl.Status == DownloadStatus.ImportPending)
                        continue;

                    try
                    {
                        var failedMatch = failedItems.FirstOrDefault(item =>
                            (!string.IsNullOrEmpty(item.NzoId) && !string.IsNullOrEmpty(dl.GetClientDownloadItemId()) &&
                                string.Equals(item.NzoId, dl.GetClientDownloadItemId(), StringComparison.OrdinalIgnoreCase)) ||
                            string.Equals(item.Name, dl.Title, StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrEmpty(dl.Title) && item.Name.Contains(dl.Title, StringComparison.OrdinalIgnoreCase))
                        );

                        if (!string.IsNullOrEmpty(failedMatch.Name))
                        {
                            continue;
                        }

                        // Find matching active download by NZO ID
                        var matchingItem = completedItems.FirstOrDefault(item =>
                            // Match by NZO ID (strongest) or fall back to name/title matching
                            (!string.IsNullOrEmpty(item.NzoId) && !string.IsNullOrEmpty(dl.GetClientDownloadItemId()) &&
                                string.Equals(item.NzoId, dl.GetClientDownloadItemId(), StringComparison.OrdinalIgnoreCase)) ||
                            string.Equals(item.Name, dl.Title, StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrEmpty(dl.Title) && item.Name.Contains(dl.Title, StringComparison.OrdinalIgnoreCase))
                        );

                        if (!string.IsNullOrEmpty(matchingItem.Name))
                        {
                            AdapterUtils.MapDownloadProgress(dl, 100.0, 0, "success");

                            // Record match type metrics
                            try
                            {
                                if (!string.IsNullOrEmpty(matchingItem.NzoId) && !string.IsNullOrEmpty(dl.GetClientDownloadItemId()) && string.Equals(matchingItem.NzoId, dl.GetClientDownloadItemId(), StringComparison.OrdinalIgnoreCase))
                                {
                                    _appMetricsService.Increment("sabnzbd.history.match.nzo");
                                }
                                else if (!string.IsNullOrEmpty(matchingItem.Name) && string.Equals(matchingItem.Name, dl.Title, StringComparison.OrdinalIgnoreCase))
                                {
                                    _appMetricsService.Increment("sabnzbd.history.match.title_exact");
                                }
                                else
                                {
                                    _appMetricsService.Increment("sabnzbd.history.match.title_contains");
                                }
                            }
                            catch (Exception caughtEx_11) when (caughtEx_11 is not OperationCanceledException && caughtEx_11 is not OutOfMemoryException && caughtEx_11 is not StackOverflowException)
                            {
                                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                            }
                            _logger.LogInformation("Found completed SABnzbd download: {DownloadTitle} -> {CompletedName} at {Path}",
                                dl.Title, matchingItem.Name, matchingItem.Path);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Error processing download {DownloadId} while polling SABnzbd", dl.Id);
                    }
                }

                return downloads;
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                throw new DownloadClientAdapterPollingException($"Error polling SABnzbd client {client.Id}");
            }
        }
    }
}

