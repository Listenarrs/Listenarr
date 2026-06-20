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
        public async Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var items = new List<QueueItem>();
            if (client == null) return items;

            var configuredCategory = DownloadClientCategoryFilter.GetConfiguredCategory(client);

            try
            {
                var requestContext = requestBuilder.CreateContext(client);
                if (!requestContext.HasApiKey)
                {
                    logger.LogWarning("SABnzbd API key not configured for {ClientName}", client.Name);
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
                    logger.LogWarning("SABnzbd queue request failed with status {Status}", response.StatusCode);
                    return items;
                }

                var jsonContent = await response.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(jsonContent))
                {
                    logger.LogWarning("SABnzbd returned empty response for client {ClientName}", LogRedaction.SanitizeText(client.Name));
                    return items;
                }

                var doc = JsonDocument.Parse(jsonContent);
                if (!doc.RootElement.TryGetProperty("queue", out var queue)) return items;
                if (!queue.TryGetProperty("slots", out var slots) || slots.ValueKind != JsonValueKind.Array) return items;

                var speed = 0.0;
                if (queue.TryGetProperty("speed", out var speedProp))
                {
                    speed = SabnzbdResponseMapper.ParseSpeed(speedProp.GetString() ?? "0");
                }

                foreach (var slot in slots.EnumerateArray())
                {
                    try
                    {
                        var queueItem = SabnzbdResponseMapper.MapQueueSlotToQueueItem(client, slot, configuredCategory ?? string.Empty, speed);
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

                await AddHistoryItemsAsync(client, requestContext, configuredCategory, items, http, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Error getting SABnzbd queue");
            }

            return items;
        }

        private async Task AddHistoryItemsAsync(
            DownloadClientConfiguration client,
            SabnzbdRequestContext requestContext,
            string? configuredCategory,
            List<QueueItem> items,
            HttpClient http,
            CancellationToken ct)
        {
            var existingNzoIds = new HashSet<string>(items.Select(i => i.Id), StringComparer.OrdinalIgnoreCase);
            try
            {
                var historyUrl = requestBuilder.BuildUrl(requestContext, new Dictionary<string, string>
                {
                    ["mode"] = "history",
                    ["output"] = "json",
                    ["limit"] = "30"
                });
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
                                    var historyItem = SabnzbdResponseMapper.MapHistorySlotToQueueItem(client, slot, configuredCategory ?? string.Empty, existingNzoIds);
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
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogDebug(ex, "Failed to fetch SABnzbd history for queue enrichment (non-fatal)");
            }
        }
    }
}
