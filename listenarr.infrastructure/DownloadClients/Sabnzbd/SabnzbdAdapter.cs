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
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Sabnzbd
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
        private readonly SabnzbdRequestBuilder _requestBuilder;
        private readonly SabnzbdDownloadPollingWorkflow _downloadPollingWorkflow;
        private readonly SabnzbdRemovalWorkflow _removalWorkflow;
        private readonly SabnzbdQueueFetchWorkflow _queueFetchWorkflow;
        private readonly SabnzbdImportItemResolver _importItemResolver;

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
            _requestBuilder = new SabnzbdRequestBuilder();
            _downloadPollingWorkflow = new SabnzbdDownloadPollingWorkflow(_httpFactory, _requestBuilder, _appMetricsService, _logger, ClientType);
            _removalWorkflow = new SabnzbdRemovalWorkflow(_httpFactory, _requestBuilder, _logger, ClientType);
            _queueFetchWorkflow = new SabnzbdQueueFetchWorkflow(_httpFactory, _requestBuilder, _logger, ClientType);
            _importItemResolver = new SabnzbdImportItemResolver(_httpFactory, _requestBuilder, _logger, ClientType);
        }

        public async Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            try
            {
                if (client == null) throw new ArgumentNullException(nameof(client));

                var requestContext = _requestBuilder.CreateContext(client);
                if (!requestContext.HasApiKey)
                    return (false, "SABnzbd API key not configured in client settings");

                var url = _requestBuilder.BuildUrl(requestContext, new Dictionary<string, string>
                {
                    ["mode"] = "version",
                    ["output"] = "json"
                });
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

        public async Task<DownloadClientSubmissionResult> AddAsync(
            DownloadClientConfiguration client,
            PreparedDownloadSubmission submission,
            CancellationToken ct = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (submission is not PreparedUsenetSubmission usenet)
                throw new DownloadClientSubmissionException("SABnzbd requires a prepared Usenet submission.");

            try
            {
                var requestContext = _requestBuilder.CreateContext(client);
                if (!requestContext.HasApiKey)
                    throw new Exception("SABnzbd API key not configured");

                _logger.LogInformation("Sending prepared NZB to SABnzbd: {Title} from {Source}", LogRedaction.SanitizeText(usenet.Title), LogRedaction.SanitizeText(usenet.Source));

                var sensitiveValues = _requestBuilder.BuildSensitiveValues(requestContext);
                var queryParams = SabnzbdAddRequestPlanner.BuildFileQueryParams(client, usenet.Title);
                var requestUrl = _requestBuilder.BuildUrl(requestContext, queryParams);

                _logger.LogDebug("SABnzbd request URL: {Url}", LogRedaction.RedactText(requestUrl, sensitiveValues));

                var http = _httpFactory.CreateClient(ClientType);
                using var multipart = new MultipartFormDataContent();
                var fileContent = new ByteArrayContent(usenet.NzbBytes);
                fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-nzb");
                multipart.Add(fileContent, "name", usenet.FileName);
                var response = await http.PostAsync(requestUrl, multipart, ct);
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
                    throw new DownloadClientSubmissionException("SABnzbd returned an empty submission response.");
                }

                var jsonDoc = JsonDocument.Parse(responseContent);
                var root = jsonDoc.RootElement;

                if (root.TryGetProperty("error", out var errorElement))
                {
                    var errorMsg = errorElement.GetString();
                    if (!string.IsNullOrEmpty(errorMsg))
                        throw new Exception($"SABnzbd error: {errorMsg}");
                }

                string downloadId;
                if (root.TryGetProperty("nzo_ids", out var nzoIds) && nzoIds.ValueKind == JsonValueKind.Array)
                {
                    var firstId = nzoIds.EnumerateArray().FirstOrDefault();
                    downloadId = firstId.GetString() ?? Guid.NewGuid().ToString();
                }
                else
                {
                    throw new DownloadClientSubmissionException("SABnzbd did not return a verified queue identifier.");
                }

                _logger.LogInformation("Successfully added NZB to SABnzbd with ID: {DownloadId}", LogRedaction.SanitizeText(downloadId));
                return new DownloadClientSubmissionResult(downloadId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to send NZB to SABnzbd");
                throw;
            }
        }

        public async Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default)
        {
            return await _removalWorkflow.RemoveAsync(client, id, deleteFiles, ct);
        }

        public async Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            return await _queueFetchWorkflow.GetQueueAsync(client, ct);
        }

        public async Task<List<(string Id, string Name)>> GetRecentHistoryAsync(DownloadClientConfiguration client, int limit = 100, CancellationToken ct = default)
        {
            var result = new List<(string Id, string Name)>();
            if (client == null) return result;

            try
            {
                var requestContext = _requestBuilder.CreateContext(client);
                if (!requestContext.HasApiKey) return result;

                var historyUrl = _requestBuilder.BuildUrl(requestContext, new Dictionary<string, string>
                {
                    ["mode"] = "history",
                    ["output"] = "json",
                    ["limit"] = limit.ToString(CultureInfo.InvariantCulture)
                });
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
                var requestContext = _requestBuilder.CreateContext(client);
                if (!requestContext.HasApiKey)
                {
                    _logger.LogWarning("SABnzbd API key not configured for client {ClientName}", LogRedaction.SanitizeText(client.Name));
                    return items;
                }

                var requestUrl = _requestBuilder.BuildUrl(requestContext, new Dictionary<string, string>
                {
                    ["mode"] = "queue",
                    ["output"] = "json"
                });
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
                    queueSpeed = SabnzbdResponseMapper.ParseSpeed(speedStr);
                }

                foreach (var slot in slots.EnumerateArray())
                {
                    try
                    {
                        var downloadClientItem = SabnzbdResponseMapper.MapQueueSlotToDownloadClientItem(client, slot, configuredCategory ?? string.Empty, queueSpeed);
                        if (downloadClientItem != null)
                        {
                            items.Add(downloadClientItem);
                        }
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
            return await _importItemResolver.GetImportItemAsync(client, item, ct);
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
            return await _importItemResolver.GetImportItemAsync(client, queueItem, ct);
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
