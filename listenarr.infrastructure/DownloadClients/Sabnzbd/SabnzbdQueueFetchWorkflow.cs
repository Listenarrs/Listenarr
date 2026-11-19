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

        public async Task<List<QueueItem>> GetQueueAsync(
            DownloadClientConfiguration client,
            IReadOnlyCollection<string>? monitoredIds = null,
            CancellationToken ct = default)
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
                    if (isMonitorPoll)
                    {
                        throw new DownloadClientAdapterPollingException(message);
                    }
                    return items;
                }

                var requestUrl = requestBuilder.BuildUrl(requestContext, new Dictionary<string, string>
                {
                    ["mode"] = "queue",
                    ["output"] = "json"
                });
                logger.LogDebug("SABnzbd queue request (redacted): {Url}", LogRedaction.RedactText(requestUrl, requestBuilder.BuildSensitiveValues(requestContext)));

                var http = httpFactory.CreateClient(clientType);
                var response = await http.GetAsync(requestUrl, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var message = $"SABnzbd queue request failed with status {response.StatusCode} for client {LogRedaction.SanitizeText(client.Name ?? client.Id)}.";
                    logger.LogWarning("SABnzbd queue request failed with status {Status}", response.StatusCode);
                    if (isMonitorPoll)
                    {
                        throw new DownloadClientAdapterPollingException(message);
                    }
                    return items;
                }

                var jsonContent = await response.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(jsonContent))
                {
                    var message = $"SABnzbd returned empty queue response for client {LogRedaction.SanitizeText(client.Name ?? client.Id)}.";
                    logger.LogWarning("SABnzbd returned empty response for client {ClientName}", LogRedaction.SanitizeText(client.Name));
                    if (isMonitorPoll)
                    {
                        throw new DownloadClientAdapterPollingException(message);
                    }
                    return items;
                }

                var doc = JsonDocument.Parse(jsonContent);
                if (!doc.RootElement.TryGetProperty("queue", out var queue) ||
                    !queue.TryGetProperty("slots", out var slots) ||
                    slots.ValueKind != JsonValueKind.Array)
                {
                    var message = $"SABnzbd returned an invalid queue response for client {LogRedaction.SanitizeText(client.Name ?? client.Id)}.";
                    logger.LogWarning("SABnzbd returned an invalid queue response for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
                    if (isMonitorPoll)
                    {
                        throw new DownloadClientAdapterPollingException(message);
                    }
                    return items;
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
                            items.Add(queueItem);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
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
                var historyFailureIsFatal = isMonitorPoll && missingTrackedIds.Count > 0;
                await AddHistoryItemsAsync(client, requestContext, configuredCategory, items, http, historyLimit, historyFailureIsFatal, monitoredIdSet, ct);
            }
            catch (DownloadClientAdapterPollingException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Error getting SABnzbd queue");
                if (isMonitorPoll)
                {
                    throw new DownloadClientAdapterPollingException("Error polling SABnzbd queue.", ex);
                }
            }

            return items;
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
                var historyResp = await http.GetAsync(historyUrl, ct);
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

                var histDoc = JsonDocument.Parse(historyText);
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
                        var historyItem = SabnzbdResponseMapper.MapHistorySlotToQueueItem(client, slot, configuredCategory ?? string.Empty, existingNzoIds, monitoredIdSet);
                        if (historyItem != null)
                        {
                            items.Add(historyItem);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
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
