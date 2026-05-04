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
using System.Text.Json;
using System.Xml.Linq;
using Listenarr.Application.Downloads;
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

        private static readonly HashSet<char> InvalidFileNameChars = new(Path.GetInvalidFileNameChars());

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly INzbUrlResolver _nzbUrlResolver;
        private readonly ILogger<NzbgetAdapter> _logger;

        public NzbgetAdapter(
            IHttpClientFactory httpClientFactory,
            INzbUrlResolver nzbUrlResolver,
            ILogger<NzbgetAdapter> logger)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _nzbUrlResolver = nzbUrlResolver ?? throw new ArgumentNullException(nameof(nzbUrlResolver));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
                var versionResult = await CallXmlRpcAsync(client, "version");
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

        private static bool IsVersion25OrNewer(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return false;

            // Version format: "25.4" or "25.4-testing"
            var parts = version.Split(new[] { '.', '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && int.TryParse(parts[0], out var major))
            {
                return major >= 25;
            }

            return false;
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
            var category = ResolveCategory(client);
            var priority = ResolvePriority(client);
            var droneId = Guid.NewGuid().ToString().Replace("-", string.Empty);

            // Download NZB content
            var nzbBytes = await DownloadNzbAsync(nzbUrl, indexerApiKey, ct);
            var nzbFileName = BuildNzbFileName(result);

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
            var authHeader = BuildAuthHeader(client);
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
            var category = ResolveCategory(client);
            var priority = ResolvePriority(client);
            var droneId = Guid.NewGuid().ToString().Replace("-", string.Empty);

            // Download and base64-encode the NZB content
            var nzbBytes = await DownloadNzbAsync(nzbUrl, indexerApiKey, ct);
            var nzbContentBase64 = Convert.ToBase64String(nzbBytes);
            var nzbFileName = BuildNzbFileName(result);

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
                var appendResult = await CallXmlRpcAsync(client, "append",
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
            var numericId = TryParseId(id);

            // If it's not a numeric ID, it might be a droneId (GUID from Listenarr)
            // Try to find it in history first
            if (!numericId.HasValue)
            {
                _logger.LogInformation("ID {Id} is not numeric, searching NZBGet history for matching download", LogRedaction.SanitizeText(id));

                try
                {
                    // Get history to find the NZBID by matching droneId
                    var historyResult = await CallXmlRpcAsync(client, "history", false);
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
                var historyDeleteResult = await CallXmlRpcAsync(client, "editqueue", "HistoryDelete", 0, string.Empty, new[] { numericId.Value });
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
                var editResult = await CallXmlRpcAsync(client, "editqueue", command, 0, string.Empty, new[] { numericId.Value });
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
                var listResult = await CallXmlRpcAsync(client, "listgroups");
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

                            var queueItem = MapGroup(client, structElement);
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
                var historyResult = await CallXmlRpcAsync(client, "history", false);
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
                var listResult = await CallXmlRpcAsync(client, "listgroups");
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

                            var downloadClientItem = await MapGroupToDownloadClientItemAsync(client, structElement);
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
                var historyResult = await CallXmlRpcAsync(client, "history", false);
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

        private async Task<DownloadClientItem> MapGroupToDownloadClientItemAsync(DownloadClientConfiguration client, XElement structElement)
        {
            var members = (IReadOnlyDictionary<string, string?>)structElement.Elements("member").ToDictionary(
                m => m.Element("name")?.Value ?? string.Empty,
                m => m.Element("value")?.Elements().FirstOrDefault()?.Value ?? string.Empty
            );

            var id = members.GetValueOrDefault("GroupID", null)
                ?? members.GetValueOrDefault("LastID", null)
                ?? Guid.NewGuid().ToString("N");

            var title = members.GetValueOrDefault("NZBName", string.Empty);
            var statusRaw = members.GetValueOrDefault("Status", string.Empty);
            var category = members.GetValueOrDefault("Category", string.Empty);
            var sizeMb = double.TryParse(members.GetValueOrDefault("FileSizeMB", "0"), NumberStyles.Any, CultureInfo.InvariantCulture, out var sm) ? sm : 0d;
            var remainingMb = double.TryParse(members.GetValueOrDefault("RemainingSizeMB", "0"), NumberStyles.Any, CultureInfo.InvariantCulture, out var rm) ? rm : 0d;
            var downloadRate = double.TryParse(members.GetValueOrDefault("DownloadRate", "0"), NumberStyles.Any, CultureInfo.InvariantCulture, out var dr) ? dr : 0d;
            var destDir = members.GetValueOrDefault("DestDir", string.Empty);

            var sizeBytes = Convert.ToInt64(Math.Max(0, sizeMb) * 1024 * 1024);
            var remainingBytes = Convert.ToInt64(Math.Max(0, remainingMb) * 1024 * 1024);

            TimeSpan? remainingTime = null;
            if (downloadRate > 0 && remainingMb > 0)
            {
                var remainingBytesExact = remainingMb * 1024 * 1024;
                var etaSeconds = (int)Math.Max(0, remainingBytesExact / downloadRate);
                remainingTime = TimeSpan.FromSeconds(etaSeconds);
            }

            // Map NZBGet status to DownloadItemStatus.
            // NZBGet can emit suffixed states (e.g. SUCCESS/HEALTH, FAILURE/HEALTH).
            var normalizedStatus = (statusRaw ?? "QUEUED").ToUpperInvariant();
            var status = normalizedStatus switch
            {
                "QUEUED" => DownloadItemStatus.Queued,
                "DOWNLOADING" => DownloadItemStatus.Downloading,
                "PAUSED" => DownloadItemStatus.Paused,
                "FETCHING" => DownloadItemStatus.Downloading,
                "SCANNING" => DownloadItemStatus.Downloading,
                "PP_QUEUED" => DownloadItemStatus.Downloading,
                "PP_PROCESSING" => DownloadItemStatus.Downloading,
                _ when normalizedStatus.StartsWith("SUCCESS", StringComparison.Ordinal) => DownloadItemStatus.Completed,
                _ when normalizedStatus.StartsWith("FAILURE", StringComparison.Ordinal) || normalizedStatus.StartsWith("FAILED", StringComparison.Ordinal) => DownloadItemStatus.Failed,
                _ => DownloadItemStatus.Queued
            };

            // For NZBGet, construct OutputPath from destDir + title
            var contentPath = !string.IsNullOrEmpty(destDir) && !string.IsNullOrEmpty(title)
                ? CombineWithOptionalBase(destDir, title)
                : (destDir ?? string.Empty);
            var localContentPath = contentPath ?? string.Empty;

            var progress = sizeMb > 0 ? Math.Clamp((sizeMb - remainingMb) / sizeMb * 100, 0, 100) : 0;

            return new DownloadClientItem
            {
                DownloadId = id.ToUpperInvariant(),
                Title = title ?? string.Empty,
                Category = category ?? string.Empty,
                Status = status,
                TotalSize = sizeBytes,
                RemainingSize = remainingBytes,
                RemainingTime = remainingTime,
                OutputPath = localContentPath ?? string.Empty,
                Message = statusRaw ?? "QUEUED",
                Progress = progress,
                DownloadSpeed = downloadRate,
                CanBeRemoved = true,
                CanMoveFiles = status == DownloadItemStatus.Completed,
                DownloadClientInfo = DownloadClientItemClientInfo.FromClient(
                    clientId: client.Id,
                    clientName: client.Name,
                    clientType: "nzbget",
                    protocol: DownloadProtocol.Usenet,
                    removeCompletedDownloads: client.Settings?.TryGetValue("removeCompletedDownloads", out var removeVal) is true &&
                                             (removeVal is bool boolVal && boolVal),
                    hasPostImportCategory: !string.IsNullOrEmpty(client.Settings?.GetValueOrDefault("postImportCategory")?.ToString())
                )
            };
        }

        private QueueItem MapGroup(DownloadClientConfiguration client, XElement structElement)
        {
            var members = (IReadOnlyDictionary<string, string?>)structElement.Elements("member").ToDictionary(
                m => m.Element("name")?.Value ?? string.Empty,
                m => m.Element("value")?.Elements().FirstOrDefault()?.Value ?? string.Empty
            );

            var id = members.GetValueOrDefault("GroupID", null)
                ?? members.GetValueOrDefault("LastID", null)
                ?? Guid.NewGuid().ToString("N");

            var title = members.GetValueOrDefault("NZBName", string.Empty);
            var statusRaw = members.GetValueOrDefault("Status", string.Empty);
            var category = members.GetValueOrDefault("Category", string.Empty);
            var sizeMb = double.TryParse(members.GetValueOrDefault("FileSizeMB", "0"), NumberStyles.Any, CultureInfo.InvariantCulture, out var sm) ? sm : 0d;
            var remainingMb = double.TryParse(members.GetValueOrDefault("RemainingSizeMB", "0"), NumberStyles.Any, CultureInfo.InvariantCulture, out var rm) ? rm : 0d;
            var downloadedMb = sizeMb - remainingMb;
            var downloadRate = double.TryParse(members.GetValueOrDefault("DownloadRate", "0"), NumberStyles.Any, CultureInfo.InvariantCulture, out var dr) ? dr : 0d;
            var destDir = members.GetValueOrDefault("DestDir", string.Empty);

            var sizeBytes = Convert.ToInt64(Math.Max(0, sizeMb) * 1024 * 1024);
            var downloadedBytes = Convert.ToInt64(Math.Max(0, downloadedMb) * 1024 * 1024);

            int? etaSeconds = null;
            if (downloadRate > 0 && remainingMb > 0)
            {
                var remainingBytes = remainingMb * 1024 * 1024;
                etaSeconds = (int)Math.Max(0, remainingBytes / downloadRate);
            }

            var normalizedStatus = (statusRaw ?? "QUEUED").ToUpperInvariant();
            string status = normalizedStatus switch
            {
                "QUEUED" => "queued",
                "DOWNLOADING" => "downloading",
                "PAUSED" => "paused",
                "FETCHING" => "downloading",
                "SCANNING" => "downloading",
                "PP_QUEUED" => "downloading",
                "PP_PROCESSING" => "downloading",
                _ when normalizedStatus.StartsWith("SUCCESS", StringComparison.Ordinal) => "completed",
                _ when normalizedStatus.StartsWith("FAILURE", StringComparison.Ordinal) || normalizedStatus.StartsWith("FAILED", StringComparison.Ordinal) => "failed",
                _ => "queued"
            };

            string? localPath = destDir;

            // For NZBGet, construct ContentPath from destDir + title
            var contentPath = !string.IsNullOrEmpty(destDir) && !string.IsNullOrEmpty(title)
                ? CombineWithOptionalBase(destDir, title)
                : destDir;
            var localContentPath = contentPath;

            var addedAt = DateTime.UtcNow;

            return new QueueItem
            {
                Id = id,
                Title = title ?? string.Empty,
                Quality = category ?? string.Empty,
                Status = status,
                Progress = sizeMb > 0 ? Math.Clamp(downloadedMb / sizeMb * 100, 0, 100) : 0,
                Size = sizeBytes,
                Downloaded = downloadedBytes,
                DownloadSpeed = downloadRate,
                Eta = etaSeconds > 0 ? etaSeconds : null,
                DownloadClient = client.Name ?? client.Id ?? "NZBGet",
                DownloadClientId = client.Id ?? string.Empty,
                DownloadClientType = ClientType,
                AddedAt = addedAt,
                CanPause = status is "downloading" or "queued",
                CanRemove = true,
                RemotePath = destDir,
                LocalPath = localPath,
                ContentPath = localContentPath
            };
        }

        private string ResolveCategory(DownloadClientConfiguration client)
        {
            if (client.Settings != null && client.Settings.TryGetValue("category", out var categoryObj))
            {
                var category = categoryObj?.ToString();
                if (!string.IsNullOrWhiteSpace(category))
                {
                    return category;
                }
            }

            return string.Empty;
        }

        private int ResolvePriority(DownloadClientConfiguration client)
        {
            if (client.Settings != null && client.Settings.TryGetValue("recentPriority", out var priorityObj))
            {
                var priority = priorityObj?.ToString();
                if (!string.IsNullOrWhiteSpace(priority) && !string.Equals(priority, "default", StringComparison.OrdinalIgnoreCase))
                {
                    return priority.ToLowerInvariant() switch
                    {
                        "force" => 100,
                        "high" => 50,
                        "normal" => 0,
                        "low" => -50,
                        _ => 0
                    };
                }
            }

            return 0;
        }

        private static string BuildNzbFileName(SearchResult result)
        {
            if (result == null)
            {
                return "listenarr-download.nzb";
            }

            var rawName = result.Title;
            if (string.IsNullOrWhiteSpace(rawName))
            {
                if (!string.IsNullOrWhiteSpace(result.NzbUrl) && Uri.TryCreate(result.NzbUrl, UriKind.Absolute, out var nzbUri))
                {
                    rawName = Path.GetFileName(nzbUri.AbsolutePath);
                }

                if (string.IsNullOrWhiteSpace(rawName))
                {
                    rawName = result.Id;
                }
            }

            if (string.IsNullOrWhiteSpace(rawName))
            {
                rawName = "listenarr-download";
            }

            // NZBGet v25.4 is very strict about filenames - remove ALL special characters except basic ones
            var sanitizedChars = rawName.Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '.').ToArray();
            var sanitized = new string(sanitizedChars).Trim();
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = "listenarr-download";
            }

            if (!sanitized.EndsWith(".nzb", StringComparison.OrdinalIgnoreCase))
            {
                sanitized = sanitized + ".nzb";
            }

            return sanitized;
        }

        private async Task<XElement> CallXmlRpcAsync(DownloadClientConfiguration client, string methodName, params object[] parameters)
        {
            var baseUrl = DownloadClientUriBuilder.BuildUri(client, "/xmlrpc").ToString();
            var httpClient = _httpClientFactory.CreateClient(ClientType);

            // Build XML-RPC request
            var methodCall = new XElement("methodCall",
                new XElement("methodName", methodName),
                new XElement("params",
                    parameters.Select(p => new XElement("param", new XElement("value", SerializeValue(p))))
                )
            );

            var xmlContent = $"<?xml version=\"1.0\"?>\n{methodCall}";
            var content = new StringContent(xmlContent, Encoding.UTF8, "text/xml");

            using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl) { Content = content };
            var authHeader = BuildAuthHeader(client);
            if (authHeader != null)
                request.Headers.Authorization = authHeader;

            using var response = await httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"NZBGet XML-RPC error: {response.StatusCode} - {responseBody}", null, response.StatusCode);
            }

            var doc = XDocument.Parse(responseBody);
            var fault = doc.Root?.Element("fault");
            if (fault != null)
            {
                var faultStruct = fault.Descendants("member").ToDictionary(
                    m => m.Element("name")?.Value ?? string.Empty,
                    m => m.Element("value")?.Value ?? string.Empty
                );
                var faultString = faultStruct.GetValueOrDefault("faultString", "Unknown error");
                throw new Exception($"NZBGet XML-RPC fault: {faultString}");
            }

            return doc.Root?.Element("params")?.Element("param")?.Element("value")
                ?? throw new Exception("Invalid XML-RPC response");
        }

        private XElement SerializeValue(object value)
        {
            return value switch
            {
                string s => new XElement("string", s),
                int i => new XElement("i4", i),
                bool b => new XElement("boolean", b ? "1" : "0"),
                double d => new XElement("double", d.ToString(CultureInfo.InvariantCulture)),
                int[] arr => new XElement("array",
                    new XElement("data",
                        arr.Select(item => new XElement("value", new XElement("i4", item)))
                    )
                ),
                object[] arr => new XElement("array",
                    new XElement("data",
                        arr.Select(item => new XElement("value", SerializeValue(item)))
                    )
                ),
                Dictionary<string, object> dict => new XElement("struct",
                    dict.Select(kvp => new XElement("member",
                        new XElement("name", kvp.Key),
                        new XElement("value", SerializeValue(kvp.Value))
                    ))
                ),
                _ => new XElement("string", value.ToString() ?? string.Empty)
            };
        }


        private async Task<byte[]> DownloadNzbAsync(string nzbUrl, string? indexerApiKey, CancellationToken ct)
        {
            // SSRF guard: reject non-HTTP(S) schemes and embedded credentials; allow private/LAN hosts
            // because indexers are commonly self-hosted (Prowlarr, Jackett, etc.) on local networks.
            if (!OutboundRequestSecurity.TryValidateExternalHttpUrl(nzbUrl, out var ssrfReason, allowPrivateTargets: true))
            {
                _logger.LogWarning("Blocked SSRF attempt in NZB download: {Reason}", ssrfReason);
                throw new InvalidOperationException($"NZB URL blocked: {ssrfReason}");
            }

            try
            {
                _logger.LogDebug("Downloading NZB from {Url}", LogRedaction.SanitizeUrl(nzbUrl));

                var httpClient = _httpClientFactory.CreateClient(ClientType);
                using var request = new HttpRequestMessage(HttpMethod.Get, nzbUrl);

                // Note: Newznab/Torznab APIs include the API key in the URL query string (e.g., &apikey=xxx)
                // We should NOT add an X-Api-Key header as it may conflict with URL-based authentication
                // and cause the API to return error responses instead of the actual NZB file

                // Set User-Agent header - many indexers require this and will reject requests without it
                request.Headers.Add("User-Agent", "Listenarr/1.0.0.0");

                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                _logger.LogDebug("NZB download response: StatusCode={StatusCode}, ContentType={ContentType}, ContentLength={ContentLength}",
                    response.StatusCode,
                    response.Content.Headers.ContentType?.ToString() ?? "null",
                    response.Content.Headers.ContentLength?.ToString() ?? "unknown");

                response.EnsureSuccessStatusCode();

                var contentBytes = await response.Content.ReadAsByteArrayAsync(ct);

                _logger.LogInformation("Downloaded NZB content: {Size} bytes", contentBytes.Length);

                // If the content is suspiciously small, log it to see if it's an error message
                if (contentBytes.Length > 0 && contentBytes.Length < 500)
                {
                    var contentText = Encoding.UTF8.GetString(contentBytes);
                    _logger.LogWarning("NZB content is suspiciously small ({Size} bytes). Content: {Content}",
                        contentBytes.Length, contentText);
                }

                if (contentBytes.Length == 0)
                {
                    _logger.LogError("Downloaded NZB file is empty (0 bytes) from {Url}", LogRedaction.SanitizeUrl(nzbUrl));
                    throw new InvalidOperationException($"Downloaded NZB file is empty from {nzbUrl}");
                }

                return contentBytes;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to download NZB content from {Url}", LogRedaction.SanitizeUrl(nzbUrl));
                throw new InvalidOperationException($"Unable to retrieve NZB content from {nzbUrl}");
            }
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

        private static int? TryParseId(string id)
        {
            if (int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericId))
            {
                return numericId;
            }

            return null;
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
                var historyResult = await CallXmlRpcAsync(client, "history", false);
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
            _logger.LogDebug("Polling NZBGet client {ClientName}", client.Name);
            try
            {
                var baseUrl = DownloadClientUriBuilder.BuildUri(client, "/jsonrpc");

                using var http = _httpClientFactory.CreateClient(ClientType);

                // Add basic auth if credentials provided
                if (!string.IsNullOrEmpty(client.Username))
                {
                    var authBytes = Encoding.UTF8.GetBytes($"{client.Username}:{client.Password}");
                    var authHeader = Convert.ToBase64String(authBytes);
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authHeader);
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
                                            var clientItemId = dl.GetClientDownloadItemId();
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

