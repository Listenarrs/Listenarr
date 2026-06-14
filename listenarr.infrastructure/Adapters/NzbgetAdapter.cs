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
using Listenarr.Application.Interfaces;
using Listenarr.Application.Security;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Adapters
{
    public class NzbgetAdapter : IDownloadClientAdapter
    {
        public string ClientId => "nzbget";
        public string ClientType => "nzbget";
        public DownloadProtocol Protocol => DownloadProtocol.Usenet;

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly INzbUrlResolver _nzbUrlResolver;
        private readonly ILogger<NzbgetAdapter> _logger;
        private readonly NzbgetXmlRpcClient _xmlRpcClient;
        private readonly NzbgetNzbDownloader _nzbDownloader;
        private readonly NzbgetDownloadPollingWorkflow _downloadPollingWorkflow;
        private readonly NzbgetRemovalWorkflow _removalWorkflow;

        public NzbgetAdapter(
            IHttpClientFactory httpClientFactory,
            INzbUrlResolver nzbUrlResolver,
            ILogger<NzbgetAdapter> logger)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _nzbUrlResolver = nzbUrlResolver ?? throw new ArgumentNullException(nameof(nzbUrlResolver));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _xmlRpcClient = new NzbgetXmlRpcClient(_httpClientFactory, ClientType);
            _nzbDownloader = new NzbgetNzbDownloader(_httpClientFactory, ClientType, _logger);
            _downloadPollingWorkflow = new NzbgetDownloadPollingWorkflow(_httpClientFactory, _logger, ClientType);
            _removalWorkflow = new NzbgetRemovalWorkflow(_xmlRpcClient, _logger);
        }

        public async Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            if (client == null)
            {
                return (false, "NZBGet: Configuration not provided");
            }

            if (!string.IsNullOrWhiteSpace(client.Username) && string.IsNullOrWhiteSpace(client.Password))
            {
                return (false, "NZBGet: Password is required when a username is specified");
            }

            try
            {
                // Test connection via XML-RPC
                var versionResult = await _xmlRpcClient.CallAsync(client, "version");
                var version = versionResult.Element("string")?.Value ?? "unknown";

                if (string.IsNullOrWhiteSpace(version))
                {
                    return (false, "NZBGet: Unable to retrieve version");
                }

                return (true, "NZBGet: connected");
            }
            catch (HttpRequestException httpEx) when (httpEx.StatusCode == HttpStatusCode.Unauthorized || httpEx.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.LogDebug(httpEx, "NZBGet authentication failed for client {ClientId}", LogRedaction.SanitizeText(client.Id ?? client.Name ?? client.Type));
                return (false, "NZBGet: Authentication failed (check username/password)");
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogDebug(httpEx, "NZBGet network error for client {ClientId}", LogRedaction.SanitizeText(client.Id ?? client.Name ?? client.Type));
                return (false, $"NZBGet: network error ({httpEx.StatusCode?.ToString() ?? "unavailable"})");
            }
            catch (TaskCanceledException tce)
            {
                _logger.LogDebug(tce, "NZBGet test timed out for client {ClientId}", LogRedaction.SanitizeText(client.Id ?? client.Name ?? client.Type));
                return (false, "NZBGet: connection timed out");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "NZBGet test failed for client {ClientId}", LogRedaction.SanitizeText(client.Id ?? client.Name ?? client.Type));
                return (false, "NZBGet: connection failed");
            }
        }

        public async Task<string?> AddAsync(DownloadClientConfiguration client, SearchResult result, CancellationToken ct = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (result == null) throw new ArgumentNullException(nameof(result));

            var (nzbUrl, indexerApiKey) = await _nzbUrlResolver.ResolveAsync(result, ct);
            if (string.IsNullOrWhiteSpace(nzbUrl))
            {
                throw new ArgumentException("No NZB URL available for NZBGet", nameof(result));
            }

            // Use JSON-RPC for all versions (v25+ REST API has authentication issues)
            _logger.LogInformation("Using NZBGet JSON-RPC append method");
            return await AddViaJsonRpcAsync(client, result, nzbUrl, indexerApiKey, ct);
        }

        private async Task<string?> AddViaRestApiAsync(
            DownloadClientConfiguration client,
            SearchResult result,
            string nzbUrl,
            string? indexerApiKey,
            CancellationToken ct)
        {
            var category = NzbgetRequestPlanner.ResolveCategory(client);
            var priority = NzbgetRequestPlanner.ResolvePriority(client);
            var droneId = Guid.NewGuid().ToString().Replace("-", string.Empty);

            // Download NZB content
            var nzbBytes = await _nzbDownloader.DownloadAsync(nzbUrl, indexerApiKey, ct);
            var nzbFileName = NzbgetRequestPlanner.BuildNzbFileName(result);

            var uploadUrl = DownloadClientUriBuilder.BuildUri(client, "/api/v2/nzb");

            using var httpClient = _httpClientFactory.CreateClient(ClientType);
            using var content = new MultipartFormDataContent();

            // Add NZB file
            content.Add(new ByteArrayContent(nzbBytes), "file", nzbFileName);

            // Add metadata
            if (!string.IsNullOrWhiteSpace(category))
            {
                content.Add(new StringContent(category), "Category");
            }

            if (priority != 0)
            {
                content.Add(new StringContent(priority.ToString()), "Priority");
            }

            // Add drone tracking parameter
            content.Add(new StringContent($"drone={droneId}"), "PPParameters");

            using var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl)
            {
                Content = content
            };

            // Add Basic Auth (NZBGet v25 REST API accepts Basic Auth)
            var authHeader = NzbgetAuthentication.BuildAuthHeader(client);
            if (authHeader != null)
            {
                request.Headers.Authorization = authHeader;
            }

            _logger.LogDebug("NZBGet REST API POST to {Url} with file {FileName}", LogRedaction.SanitizeUrl(uploadUrl.ToString()), LogRedaction.SanitizeText(nzbFileName));

            using var response = await httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("NZBGet REST API upload failed: {StatusCode} - {Body}", response.StatusCode, responseBody);
                throw new Exception($"NZBGet REST API upload error: {response.StatusCode} - {responseBody}");
            }

            // Parse response JSON to get queue ID
            var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseBody);
            if (jsonResponse.TryGetProperty("nzbId", out var nzbIdProp))
            {
                var queueId = nzbIdProp.GetInt32();
                _logger.LogInformation("NZBGet REST API added '{Title}' with queue ID {QueueId}", LogRedaction.SanitizeText(result.Title), queueId);
                return queueId.ToString();
            }

            _logger.LogWarning("NZBGet REST API response missing nzbId: {Body}", responseBody);
            return null;
        }

        private async Task<string?> AddViaJsonRpcAsync(
            DownloadClientConfiguration client,
            SearchResult result,
            string nzbUrl,
            string? indexerApiKey,
            CancellationToken ct)
        {
            var category = NzbgetRequestPlanner.ResolveCategory(client);
            var priority = NzbgetRequestPlanner.ResolvePriority(client);
            var droneId = Guid.NewGuid().ToString().Replace("-", string.Empty);

            // Download and base64-encode the NZB content
            var nzbBytes = await _nzbDownloader.DownloadAsync(nzbUrl, indexerApiKey, ct);
            var nzbContentBase64 = Convert.ToBase64String(nzbBytes);
            var nzbFileName = NzbgetRequestPlanner.BuildNzbFileName(result);

            // PPParameters as array of structs (key-value pairs)
            var ppParams = new[]
            {
                new Dictionary<string, object>
                {
                    { "Name", "drone" },
                    { "Value", droneId }
                }
            };

            try
            {
                // Call append via XML-RPC
                _logger.LogInformation("Calling NZBGet append via XML-RPC for '{Title}'", LogRedaction.SanitizeText(result.Title));
                var appendResult = await _xmlRpcClient.CallAsync(client, "append",
                    nzbFileName,
                    nzbContentBase64,
                    category ?? string.Empty,
                    priority,
                    false,  // addToTop
                    false,  // addPaused
                    string.Empty,  // dupeKey
                    0,      // dupeScore
                    "SCORE", // dupeMode
                    ppParams
                );

                var queueId = int.Parse(appendResult.Element("i4")?.Value ?? appendResult.Element("int")?.Value ?? "0");

                if (queueId <= 0)
                {
                    _logger.LogWarning("NZBGet rejected NZB '{Title}', returned ID: {QueueId}", LogRedaction.SanitizeText(result.Title), queueId);
                    return null;
                }

                _logger.LogInformation("NZBGet XML-RPC queued '{Title}' with ID {QueueId}, droneId: {DroneId}", LogRedaction.SanitizeText(result.Title), queueId, LogRedaction.SanitizeText(droneId));
                // Return the NZBID so it can be stored and used for removal later
                return queueId.ToString();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to add NZB via XML-RPC");
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

            try
            {
                var listResult = await _xmlRpcClient.CallAsync(client, "listgroups");
                var arrayData = listResult.Element("array")?.Element("data");

                if (arrayData == null)
                {
                    return items;
                }

                foreach (var valueElement in arrayData.Elements("value"))
                {
                    try
                    {
                        var structElement = valueElement.Element("struct");
                        if (structElement != null)
                        {
                            var groupCategory = structElement.Elements("member")
                                .FirstOrDefault(m => string.Equals(m.Element("name")?.Value, "Category", StringComparison.Ordinal))?
                                .Element("value")?.Elements().FirstOrDefault()?.Value ?? string.Empty;

                            if (!DownloadClientCategoryFilter.Matches(configuredCategory, groupCategory))
                            {
                                continue;
                            }

                            var queueItem = NzbgetResponseMapper.MapGroup(client, structElement);
                            items.Add(queueItem);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogDebug(ex, "Failed to map NZBGet queue item (non-fatal)");
                    }
                }
            }
            catch (HttpRequestException httpEx) when (httpEx.StatusCode == HttpStatusCode.Unauthorized || httpEx.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("NZBGet authentication failed for client {ClientName} — check username/password", LogRedaction.SanitizeText(client.Name ?? client.Id));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to retrieve NZBGet queue for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
            }

            return items;
        }

        public async Task<List<(string Id, string Name)>> GetRecentHistoryAsync(DownloadClientConfiguration client, int limit = 100, CancellationToken ct = default)
        {
            var history = new List<(string Id, string Name)>();
            if (client == null) return history;

            try
            {
                var historyResult = await _xmlRpcClient.CallAsync(client, "history", false);
                var arrayData = historyResult.Element("array")?.Element("data");

                if (arrayData == null)
                {
                    return history;
                }

                var count = 0;
                foreach (var valueElement in arrayData.Elements("value"))
                {
                    if (count >= limit) break;

                    var structElement = valueElement.Element("struct");
                    if (structElement != null)
                    {
                        var members = structElement.Elements("member").ToDictionary(
                            m => m.Element("name")?.Value ?? string.Empty,
                            m => m.Element("value")?.Elements().FirstOrDefault()?.Value ?? string.Empty
                        );

                        var entryId = members.GetValueOrDefault("ID", string.Empty);
                        var entryName = members.GetValueOrDefault("NZBName", string.Empty);

                        if (!string.IsNullOrEmpty(entryId) && !string.IsNullOrEmpty(entryName))
                        {
                            history.Add((entryId, entryName));
                            count++;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to fetch NZBGet history for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
            }

            return history;
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
                var listResult = await _xmlRpcClient.CallAsync(client, "listgroups");
                var arrayData = listResult.Element("array")?.Element("data");

                if (arrayData == null)
                {
                    return items;
                }

                foreach (var valueElement in arrayData.Elements("value"))
                {
                    try
                    {
                        var structElement = valueElement.Element("struct");
                        if (structElement != null)
                        {
                            var groupCategory = structElement.Elements("member")
                                .FirstOrDefault(m => string.Equals(m.Element("name")?.Value, "Category", StringComparison.Ordinal))?
                                .Element("value")?.Elements().FirstOrDefault()?.Value ?? string.Empty;

                            if (!DownloadClientCategoryFilter.Matches(configuredCategory, groupCategory))
                            {
                                continue;
                            }

                            var downloadClientItem = NzbgetResponseMapper.MapGroupToDownloadClientItem(client, structElement);
                            items.Add(downloadClientItem);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogDebug(ex, "Failed to map NZBGet queue item (non-fatal)");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to retrieve NZBGet items for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
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
                return result;
            }

            try
            {
                // Query NZBGet history for the download
                var historyResult = await _xmlRpcClient.CallAsync(client, "history", false);
                var arrayData = historyResult.Element("array")?.Element("data");

                if (arrayData == null)
                {
                    _logger.LogWarning("Invalid NZBGet history response format");
                    return result;
                }

                // Find matching history entry by ID
                foreach (var members in arrayData.Elements("value")
                    .Select(valueElement => valueElement.Element("struct"))
                    .Where(structElement => structElement != null)
                    .Select(structElement => structElement!.Elements("member").ToDictionary(
                        m => m.Element("name")?.Value ?? string.Empty,
                        m => m.Element("value")?.Elements().FirstOrDefault()?.Value ?? string.Empty)))
                {
                    var entryId = members.GetValueOrDefault("ID", string.Empty);
                    if (!string.Equals(entryId, item.DownloadId, StringComparison.OrdinalIgnoreCase)) continue;

                    // Extract destination directory
                    var destDir = members.GetValueOrDefault("DestDir", string.Empty);
                    if (string.IsNullOrEmpty(destDir))
                    {
                        _logger.LogWarning("No DestDir found for NZBGet download {Id}", item.DownloadId);
                        return result;
                    }

                    result.OutputPath = destDir;

                    _logger.LogDebug(
                        "Resolved NZBGet content path for {Id}: {ContentPath}",
                        item.DownloadId,
                        destDir);

                    return result;
                }

                _logger.LogWarning("Download {Id} not found in NZBGet history", item.DownloadId);
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Error resolving import item for NZBGet download {Id}", item.DownloadId);
                return result;
            }
        }

        /// <summary>
        /// Resolves the actual import item for a completed download.
        /// Queries NZBGet history for FinalDir or DestDir.
        /// Matches NzbGet.GetImportItem pattern.
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
                return result;
            }

            try
            {
                // Query NZBGet history for the download
                var historyResult = await _xmlRpcClient.CallAsync(client, "history", false);
                var arrayData = historyResult.Element("array")?.Element("data");

                if (arrayData == null)
                {
                    _logger.LogWarning("Failed to query NZBGet history for download {NzbId}", queueItem.Id);
                    return result;
                }

                // Find the history entry matching our download ID
                foreach (var members in arrayData.Elements("value")
                    .Select(valueElement => valueElement.Element("struct"))
                    .Where(structElement => structElement != null)
                    .Select(structElement => structElement!.Elements("member").ToDictionary(
                        m => m.Element("name")?.Value ?? string.Empty,
                        m => m.Element("value")?.Elements().FirstOrDefault()?.Value ?? string.Empty)))
                {
                    var entryId = members.GetValueOrDefault("NZBID", string.Empty);
                    if (entryId != queueItem.Id) continue;

                    // Found matching entry - extract path
                    // FinalDir is preferred (post-processing destination), fallback to DestDir
                    var finalDir = members.GetValueOrDefault("FinalDir", string.Empty);
                    var destDir = members.GetValueOrDefault("DestDir", string.Empty);
                    var contentPath = !string.IsNullOrEmpty(finalDir) ? finalDir : destDir;

                    if (string.IsNullOrEmpty(contentPath))
                    {
                        _logger.LogWarning("No FinalDir or DestDir found for NZB {NzbId}", queueItem.Id);
                        return result;
                    }

                    // Apply path mapping
                    var localContentPath = contentPath;
                    result.ContentPath = localContentPath;

                    _logger.LogDebug(
                        "Resolved NZBGet content path for {NzbId}: {ContentPath}",
                        queueItem.Id,
                        localContentPath);

                    return result;
                }

                _logger.LogWarning("Download {NzbId} not found in NZBGet history", queueItem.Id);
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Error resolving import item for NZBGet download {NzbId}", queueItem.Id);
                return result;
            }
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
