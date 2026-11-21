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
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Sabnzbd
{
    internal sealed class SabnzbdQueueFetchWorkflow(
        IHttpClientFactory httpFactory,
        SabnzbdRequestBuilder requestBuilder,
        ILogger logger,
        string clientType)
    {
        private const int DisplayHistoryLimit = 30;
        private const int MonitorHistoryLimit = 100;
        private const int MaxEncodedHistoryIdsLength = 2048;
        private const int EncodedCommaLength = 3;
        private const string IncompleteSnapshotMessage = "Incomplete SABnzbd queue snapshot.";

        public async Task<List<QueueItem>> GetQueueAsync(
            DownloadClientConfiguration client,
            IReadOnlyCollection<string>? monitoredIds = null,
            CancellationToken ct = default,
            bool trackedPresence = false)
        {
            var items = new List<QueueItem>();
            if (client == null) return items;

            var monitoredIdSet = BuildMonitoredIdSet(monitoredIds);
            var isMonitorPoll = monitoredIdSet.Count > 0;
            var configuredCategory = DownloadClientCategoryFilter.GetConfiguredCategory(client);

            try
            {
                var requestContext = requestBuilder.CreateContext(client);
                if (!requestContext.HasApiKey)
                {
                    var message = $"SABnzbd API key not configured for client {LogRedaction.SanitizeText(client.Name ?? client.Id)}.";
                    logger.LogWarning("SABnzbd API key not configured for {ClientName}", client.Name);
                    throw new DownloadClientAdapterPollingException(message);
                }

                var requestUrl = requestBuilder.BuildUrl(requestContext, new Dictionary<string, string>
                {
                    ["mode"] = "queue",
                    ["output"] = "json"
                });
                logger.LogDebug("SABnzbd queue request (redacted): {Url}", LogRedaction.RedactText(requestUrl, requestBuilder.BuildSensitiveValues(requestContext)));

                using var http = httpFactory.CreateClient(clientType);
                using var response = await http.GetAsync(requestUrl, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var message = $"SABnzbd queue request failed with status {response.StatusCode} for client {LogRedaction.SanitizeText(client.Name ?? client.Id)}.";
                    logger.LogWarning("SABnzbd queue request failed with status {Status}", response.StatusCode);
                    throw new DownloadClientAdapterPollingException(message);
                }

                var jsonContent = await response.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(jsonContent))
                {
                    var message = $"SABnzbd returned empty queue response for client {LogRedaction.SanitizeText(client.Name ?? client.Id)}.";
                    logger.LogWarning("SABnzbd returned empty response for client {ClientName}", LogRedaction.SanitizeText(client.Name));
                    throw new DownloadClientAdapterPollingException(message);
                }

                using var doc = JsonDocument.Parse(jsonContent, new JsonDocumentOptions { AllowDuplicateProperties = isMonitorPoll && !trackedPresence });
                if (!doc.RootElement.TryGetProperty("queue", out var queue) ||
                    !queue.TryGetProperty("slots", out var slots) ||
                    slots.ValueKind != JsonValueKind.Array)
                {
                    var message = $"SABnzbd returned an invalid queue response for client {LogRedaction.SanitizeText(client.Name ?? client.Id)}.";
                    logger.LogWarning("SABnzbd returned an invalid queue response for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
                    throw new DownloadClientAdapterPollingException(message);
                }

                if (trackedPresence)
                {
                    return await ReadTrackedPresenceAsync(requestContext, http, queue, slots, monitoredIdSet, ct);
                }

                var speed = 0.0;
                if (queue.TryGetProperty("speed", out var speedProp))
                {
                    speed = SabnzbdResponseMapper.ParseSpeed(speedProp.GetString() ?? "0");
                }

                foreach (var slot in slots.EnumerateArray())
                {
                    try
                    {
                        var queueItem = SabnzbdResponseMapper.MapQueueSlotToQueueItem(client, slot, configuredCategory ?? string.Empty, speed, monitoredIdSet);
                        if (queueItem != null)
                        {
                            if (!isMonitorPoll && string.IsNullOrWhiteSpace(queueItem.Id))
                            {
                                throw new DownloadClientAdapterPollingException(IncompleteSnapshotMessage);
                            }

                            items.Add(queueItem);
                        }
                    }
                    catch (Exception ex) when (ex is not DownloadClientAdapterPollingException && ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        // A full poll drives orphan cleanup, so a slot we cannot read
                        // must fail the snapshot instead of silently dropping out of it.
                        if (!isMonitorPoll)
                        {
                            throw new DownloadClientAdapterPollingException(IncompleteSnapshotMessage, ex);
                        }

                        logger.LogError(ex, "Error parsing SABnzbd queue item");
                    }
                }
                logger.LogInformation("Retrieved {Count} items from SABnzbd active queue", items.Count);

                var missingTrackedIds = GetMissingTrackedIds(monitoredIdSet, items);

                // History is required for display enrichment and for monitored items that
                // have already left the active queue. If every monitored ID is still active,
                // a flaky history endpoint must not block progress updates.
                var historyRequired = !isMonitorPoll || missingTrackedIds.Count > 0;
                if (!historyRequired)
                {
                    logger.LogDebug(
                        "Skipping SABnzbd history enrichment for active monitored items on client {ClientName}",
                        LogRedaction.SanitizeText(client.Name ?? client.Id));
                    return items;
                }

                var historyLimit = isMonitorPoll ? MonitorHistoryLimit : DisplayHistoryLimit;
                var historyFailureIsFatal = !isMonitorPoll || missingTrackedIds.Count > 0;
                await AddHistoryItemsAsync(client, requestContext, configuredCategory, items, http, historyLimit, historyFailureIsFatal, monitoredIdSet, ct);
            }
            catch (DownloadClientAdapterPollingException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Error getting SABnzbd queue");
                throw new DownloadClientAdapterPollingException("Error polling SABnzbd queue.", ex);
            }

            return items;
        }

        private async Task<List<QueueItem>> ReadTrackedPresenceAsync(
            SabnzbdRequestContext requestContext,
            HttpClient http,
            JsonElement queue,
            JsonElement slots,
            HashSet<string> missingIds,
            CancellationToken ct)
        {
            var found = new List<QueueItem>();
            ReadPresenceIds(slots, missingIds, found);
            if (missingIds.Count == 0)
            {
                return found;
            }

            if (queue.TryGetProperty("noofslots", out var queueCount) && !MatchesSlotCount(queueCount, slots))
            {
                throw new DownloadClientAdapterPollingException("Incomplete SABnzbd active queue during tracked lookup.");
            }

            foreach (var batch in BatchHistoryIds(missingIds))
            {
                await ReadTrackedHistoryBatchAsync(requestContext, http, batch, found, ct);
            }

            return found;
        }

        private async Task ReadTrackedHistoryBatchAsync(
            SabnzbdRequestContext requestContext,
            HttpClient http,
            HashSet<string> missingIds,
            List<QueueItem> found,
            CancellationToken ct)
        {
            var parameters = new Dictionary<string, string>
            {
                ["mode"] = "history",
                ["output"] = "json",
                ["limit"] = MonitorHistoryLimit.ToString(CultureInfo.InvariantCulture),
                ["nzo_ids"] = string.Join(',', missingIds)
            };

            using var response = await http.GetAsync(requestBuilder.BuildUrl(requestContext, parameters), ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new DownloadClientAdapterPollingException($"SABnzbd tracked history request failed with status {response.StatusCode}.");
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct), new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (!document.RootElement.TryGetProperty("history", out var history) ||
                !history.TryGetProperty("slots", out var historySlots) || historySlots.ValueKind != JsonValueKind.Array)
            {
                throw new DownloadClientAdapterPollingException("Invalid SABnzbd tracked history response.");
            }

            ReadPresenceIds(historySlots, missingIds, found);
            // Older servers can ignore nzo_ids. A short result alone does not
            // prove absence. Require the returned slots to cover the reported total.
            if (missingIds.Count > 0 &&
                (!history.TryGetProperty("noofslots", out var historyCount) || !MatchesSlotCount(historyCount, historySlots)))
            {
                throw new DownloadClientAdapterPollingException("Incomplete SABnzbd tracked history response.");
            }
        }

        private static IEnumerable<HashSet<string>> BatchHistoryIds(IEnumerable<string> ids)
        {
            var batch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var encodedLength = 0;
            foreach (var id in ids)
            {
                var idLength = Uri.EscapeDataString(id).Length;
                if (idLength > MaxEncodedHistoryIdsLength || id.Contains(','))
                {
                    throw new DownloadClientAdapterPollingException("SABnzbd tracked ID cannot be queried exactly.");
                }

                if (batch.Count > 0 && (batch.Count == MonitorHistoryLimit ||
                    encodedLength + EncodedCommaLength + idLength > MaxEncodedHistoryIdsLength))
                {
                    yield return batch;
                    batch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    encodedLength = 0;
                }

                encodedLength += batch.Count == 0 ? idLength : EncodedCommaLength + idLength;
                batch.Add(id);
            }

            if (batch.Count > 0) yield return batch;
        }

        private static bool MatchesSlotCount(JsonElement count, JsonElement slots) =>
            count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var total) && total == slots.GetArrayLength();

        private static void ReadPresenceIds(JsonElement slots, HashSet<string> missingIds, List<QueueItem> found)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var slot in slots.EnumerateArray())
            {
                if (!HasUsableNzoId(slot) || !seen.Add(slot.GetProperty("nzo_id").GetString()!))
                {
                    throw new DownloadClientAdapterPollingException(IncompleteSnapshotMessage);
                }

                var id = slot.GetProperty("nzo_id").GetString()!;
                if (missingIds.Remove(id))
                {
                    found.Add(new QueueItem { Id = id });
                }
            }
        }

        private async Task AddHistoryItemsAsync(
            DownloadClientConfiguration client,
            SabnzbdRequestContext requestContext,
            string? configuredCategory,
            List<QueueItem> items,
            HttpClient http,
            int historyLimit,
            bool historyFailureIsFatal,
            ISet<string> monitoredIdSet,
            CancellationToken ct)
        {
            var existingNzoIds = new HashSet<string>(items.Select(i => i.Id), StringComparer.OrdinalIgnoreCase);
            try
            {
                var historyUrl = requestBuilder.BuildUrl(requestContext, new Dictionary<string, string>
                {
                    ["mode"] = "history",
                    ["output"] = "json",
                    ["limit"] = historyLimit.ToString(CultureInfo.InvariantCulture)
                });
                using var historyResp = await http.GetAsync(historyUrl, ct);
                if (!historyResp.IsSuccessStatusCode)
                {
                    var message = $"SABnzbd history request failed with status {historyResp.StatusCode} for client {LogRedaction.SanitizeText(client.Name ?? client.Id)}.";
                    logger.LogDebug("SABnzbd history request failed with status {Status}", historyResp.StatusCode);
                    if (historyFailureIsFatal)
                    {
                        throw new DownloadClientAdapterPollingException(message);
                    }
                    return;
                }

                var historyText = await historyResp.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(historyText))
                {
                    var message = $"SABnzbd returned empty history response for client {LogRedaction.SanitizeText(client.Name ?? client.Id)}.";
                    logger.LogDebug("SABnzbd returned empty history response for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
                    if (historyFailureIsFatal)
                    {
                        throw new DownloadClientAdapterPollingException(message);
                    }
                    return;
                }

                var isFullPoll = monitoredIdSet.Count == 0;
                using var histDoc = JsonDocument.Parse(historyText, new JsonDocumentOptions { AllowDuplicateProperties = !isFullPoll });
                if (!histDoc.RootElement.TryGetProperty("history", out var history) ||
                    !history.TryGetProperty("slots", out var histSlots) ||
                    histSlots.ValueKind != JsonValueKind.Array)
                {
                    var message = $"SABnzbd returned an invalid history response for client {LogRedaction.SanitizeText(client.Name ?? client.Id)}.";
                    logger.LogDebug("SABnzbd returned an invalid history response for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
                    if (historyFailureIsFatal)
                    {
                        throw new DownloadClientAdapterPollingException(message);
                    }
                    return;
                }

                foreach (var slot in histSlots.EnumerateArray())
                {
                    try
                    {
                        if (isFullPoll && !HasUsableNzoId(slot))
                        {
                            throw new DownloadClientAdapterPollingException(IncompleteSnapshotMessage);
                        }

                        var historyItem = SabnzbdResponseMapper.MapHistorySlotToQueueItem(client, slot, configuredCategory ?? string.Empty, existingNzoIds, monitoredIdSet);
                        if (historyItem != null)
                        {
                            items.Add(historyItem);
                        }
                    }
                    catch (Exception ex) when (ex is not DownloadClientAdapterPollingException && ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        if (isFullPoll)
                        {
                            throw new DownloadClientAdapterPollingException(IncompleteSnapshotMessage, ex);
                        }

                        logger.LogDebug(ex, "Error parsing SABnzbd history item");
                    }
                }
                logger.LogInformation("Retrieved {Count} total items from SABnzbd (queue + history)", items.Count);
            }
            catch (DownloadClientAdapterPollingException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogDebug(ex, "Failed to fetch SABnzbd history for queue enrichment (non-fatal)");
                if (historyFailureIsFatal)
                {
                    throw new DownloadClientAdapterPollingException("Error polling SABnzbd history.", ex);
                }
            }
        }

        private static bool HasUsableNzoId(JsonElement slot)
        {
            return slot.ValueKind == JsonValueKind.Object && slot.TryGetProperty("nzo_id", out var nzoId) &&
                nzoId.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(nzoId.GetString());
        }

        private static HashSet<string> BuildMonitoredIdSet(IReadOnlyCollection<string>? monitoredIds)
        {
            return monitoredIds?
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        }

        private static HashSet<string> GetMissingTrackedIds(ISet<string> monitoredIds, IReadOnlyCollection<QueueItem> items)
        {
            if (monitoredIds.Count == 0)
            {
                return [];
            }

            var activeIds = items
                .Select(item => item.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return monitoredIds
                .Where(id => !activeIds.Contains(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }
}
