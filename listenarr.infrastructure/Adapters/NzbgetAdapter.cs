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
using System.Text;
using System.Text.Json;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Security;
using Listenarr.Domain.Common;
using Listenarr.Domain.Models;
using Listenarr.Domain.Models.Exceptions;
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
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            // First try to parse as numeric NZBID (for queue removal)
            var numericId = NzbgetRequestPlanner.TryParseId(id);

            // If it's not a numeric ID, it might be a droneId (GUID from Listenarr)
            // Try to find it in history first
            if (!numericId.HasValue)
            {
                _logger.LogInformation("ID {Id} is not numeric, searching NZBGet history for matching download", LogRedaction.SanitizeText(id));

                try
                {
                    // Get history to find the NZBID by matching droneId
                    var historyResult = await _xmlRpcClient.CallAsync(client, "history", false);
                    var arrayData = historyResult.Element("array")?.Element("data");

                    var historyCount = arrayData?.Elements("value").Count() ?? 0;
                    _logger.LogInformation("NZBGet history contains {Count} entries", historyCount);

                    if (arrayData != null)
                    {
                        foreach (var members in arrayData.Elements("value")
                            .Select(valueElement => valueElement.Element("struct"))
                            .Where(s => s != null)
                            .Select(s => s!.Elements("member").ToDictionary(
                                m => m.Element("name")?.Value ?? string.Empty,
                                m => m.Element("value")?.Elements().FirstOrDefault()
                            )))
                        {

                            // Log what fields this history entry has
                            _logger.LogInformation("History entry has fields: {Fields}", string.Join(", ", members.Keys));

                            // Check if this history entry has matching droneId in parameters
                            if (members.TryGetValue("Parameters", out var paramsElement))
                            {
                                var paramsArray = paramsElement?.Element("array")?.Element("data");
                                var paramCount = paramsArray?.Elements("value").Count() ?? 0;
                                _logger.LogInformation("History entry has {Count} parameters", paramCount);

                                if (paramsArray != null)
                                {
                                    foreach (var paramMembers in paramsArray.Elements("value")
                                        .Select(paramValueElement => paramValueElement.Element("struct"))
                                        .Where(ps => ps != null)
                                        .Select(ps => ps!.Elements("member").ToDictionary(
                                            m => m.Element("name")?.Value ?? string.Empty,
                                            m => m.Element("value")?.Elements().FirstOrDefault()?.Value ?? string.Empty
                                        )))
                                    {

                                        // Log all parameters for debugging
                                        foreach (var pm in paramMembers)
                                        {
                                            _logger.LogDebug("NZBGet History Parameter: Name={Name}, Value={Value}", pm.Key, LogRedaction.SanitizeText(pm.Value));
                                        }

                                        if (paramMembers.TryGetValue("Name", out var paramName) &&
                                            paramMembers.TryGetValue("Value", out var paramValue) &&
                                            paramName == "*drone" && paramValue == id &&
                                            members.TryGetValue("ID", out var idElement) &&
                                            int.TryParse(idElement?.Value, out var foundNumericId))
                                        {
                                            // Found matching droneId, get the NZBID
                                            _logger.LogDebug("Found NZBID {NzbId} for droneId {DroneId} in history", foundNumericId, LogRedaction.SanitizeText(id));
                                            numericId = foundNumericId;
                                            break;
                                        }
                                    }
                                }
                            }

                            if (numericId.HasValue) break;
                        }
                    }
                }
                catch (Exception histEx) when (histEx is not OperationCanceledException && histEx is not OutOfMemoryException && histEx is not StackOverflowException)
                {
                    _logger.LogDebug(histEx, "Failed to search NZBGet history for download {Id}", LogRedaction.SanitizeText(id));
                }
            }

            if (!numericId.HasValue)
            {
                _logger.LogWarning("Cannot remove NZB {Id} - not found in queue or history", LogRedaction.SanitizeText(id));
                return false;
            }

            // Try to remove from history first (for completed downloads)
            try
            {
                var historyDeleteResult = await _xmlRpcClient.CallAsync(client, "editqueue", "HistoryDelete", 0, string.Empty, new[] { numericId.Value });
                var historySuccess = historyDeleteResult.Element("boolean")?.Value == "1";

                if (historySuccess)
                {
                    _logger.LogInformation("Removed NZB {Id} from NZBGet history (deleteFiles={DeleteFiles})", LogRedaction.SanitizeText(id), deleteFiles);
                    return true;
                }
            }
            catch (Exception histEx) when (histEx is not OperationCanceledException && histEx is not OutOfMemoryException && histEx is not StackOverflowException)
            {
                _logger.LogDebug(histEx, "Could not remove {Id} from NZBGet history (may not be in history)", LogRedaction.SanitizeText(id));
            }

            // Fall back to queue removal (for active downloads)
            try
            {
                var command = deleteFiles ? "GroupDeleteFinal" : "GroupDelete";
                var editResult = await _xmlRpcClient.CallAsync(client, "editqueue", command, 0, string.Empty, new[] { numericId.Value });
                var success = editResult.Element("boolean")?.Value == "1";

                if (success)
                {
                    _logger.LogInformation("Removed NZB {Id} from NZBGet queue (deleteFiles={DeleteFiles})", LogRedaction.SanitizeText(id), deleteFiles);
                    return true;
                }

                _logger.LogWarning("NZBGet reported failure when removing {Id} from both history and queue", LogRedaction.SanitizeText(id));
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error removing NZB {Id} from NZBGet", LogRedaction.SanitizeText(id));
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
            _logger.LogDebug("Polling NZBGet client {ClientName}", client.Name);
            try
            {
                var baseUrl = DownloadClientUriBuilder.BuildUri(client, "/jsonrpc");

                using var http = _httpClientFactory.CreateClient(ClientType);

                // Add basic auth if credentials provided
                var authHeader = NzbgetAuthentication.BuildAuthHeader(client);
                if (authHeader != null)
                {
                    http.DefaultRequestHeaders.Authorization = authHeader;
                }

                // Get active downloads from status for progress updates
                var statusRequest = new
                {
                    method = "status",
                    id = 2
                };

                var statusJsonContent = JsonSerializer.Serialize(statusRequest);
                using var statusHttpContent = new StringContent(statusJsonContent, Encoding.UTF8, "application/json");

                using var statusResponse = await http.PostAsync(baseUrl, statusHttpContent, cancellationToken);

                if (statusResponse.IsSuccessStatusCode)
                {
                    var statusJson = await statusResponse.Content.ReadAsStringAsync(cancellationToken);
                    var statusDoc = JsonDocument.Parse(statusJson);

                    if (statusDoc.RootElement.TryGetProperty("result", out var statusResult))
                    {
                        // Get queue for active downloads
                        var queueRequest = new
                        {
                            method = "listgroups",
                            id = 3
                        };

                        var queueJsonContent = JsonSerializer.Serialize(queueRequest);
                        using var queueHttpContent = new StringContent(queueJsonContent, Encoding.UTF8, "application/json");

                        using var queueResponse = await http.PostAsync(baseUrl, queueHttpContent, cancellationToken);

                        if (queueResponse.IsSuccessStatusCode)
                        {
                            var queueJson = await queueResponse.Content.ReadAsStringAsync(cancellationToken);
                            var queueDoc = JsonDocument.Parse(queueJson);

                            if (queueDoc.RootElement.TryGetProperty("result", out var queueResult) && queueResult.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var group in queueResult.EnumerateArray())
                                {
                                    try
                                    {
                                        var nzbId = group.TryGetProperty("NZBID", out var nzbIdProp) ? nzbIdProp.GetInt32() : 0;
                                        var nzbName = group.TryGetProperty("NZBName", out var nameProp) ? nameProp.GetString() ?? "" : "";
                                        var status = group.TryGetProperty("Status", out var statusProp) ? statusProp.GetString() ?? "" : "";
                                        var fileSizeMB = group.TryGetProperty("FileSizeMB", out var sizeProp) ? sizeProp.GetString() ?? "" : "";
                                        var remainingSizeMB = group.TryGetProperty("RemainingSizeMB", out var remainingSizeProp) ? remainingSizeProp.GetString() ?? "" : "";
                                        // Find matching download by NZB ID
                                        var matchingDownload = downloads.FirstOrDefault(dl =>
                                        {
                                            var clientItemId = dl.GetExternalId();
                                            return !string.IsNullOrEmpty(clientItemId) &&
                                                    clientItemId.Equals(nzbId.ToString(), StringComparison.OrdinalIgnoreCase);
                                        });

                                        if (matchingDownload == null && !string.IsNullOrEmpty(nzbName))
                                        {
                                            matchingDownload = downloads.FirstOrDefault(dl => TitleUtils.AreTitlesSimilar(dl.Title, nzbName));
                                        }

                                        if (matchingDownload != null &&
                                            double.TryParse(fileSizeMB, out var totalMB) &&
                                            double.TryParse(remainingSizeMB, out var remainingMB))
                                        {
                                            var progress = totalMB > 0 ? (totalMB - remainingMB) / totalMB : 0.0;
                                            var amountLeft = (long)(remainingMB * 1024 * 1024); // Convert MB to bytes

                                            AdapterUtils.MapDownloadProgress(matchingDownload, progress, amountLeft, status);
                                        }
                                    }
                                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                    {
                                        _logger.LogWarning(ex, "Error updating NZBGet queue progress for group");
                                    }
                                }
                            }
                        }
                    }
                }

                return downloads;
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                throw new DownloadClientAdapterPollingException($"Error polling NZBGet client {client.Id}", exception);
            }
        }
    }
}
