/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Sabnzbd
{
    internal sealed class SabnzbdItemFetchWorkflow(
        IHttpClientFactory httpFactory,
        SabnzbdRequestBuilder requestBuilder,
        ILogger<SabnzbdAdapter> logger,
        string clientType)
    {
        public async Task<List<DownloadClientItem>> GetItemsAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var items = new List<DownloadClientItem>();
            if (client == null) return items;

            var configuredCategory = DownloadClientCategoryFilter.GetConfiguredCategory(client);

            try
            {
                var requestContext = requestBuilder.CreateContext(client);
                if (!requestContext.HasApiKey)
                {
                    logger.LogWarning("SABnzbd API key not configured for client {ClientName}", LogRedaction.SanitizeText(client.Name));
                    return items;
                }

                var requestUrl = requestBuilder.BuildUrl(requestContext, new Dictionary<string, string>
                {
                    ["mode"] = "queue",
                    ["output"] = "json"
                });
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
                        logger.LogError(ex, "Error parsing SABnzbd queue item");
                    }
                }
                logger.LogInformation("Retrieved {Count} items from SABnzbd queue", items.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Error getting SABnzbd items");
            }

            return items;
        }
    }
}
