/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using System.Net;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Nzbget
{
    internal sealed class NzbgetQueueFetchWorkflow(
        NzbgetXmlRpcClient xmlRpcClient,
        NzbgetHistoryEnrichmentWorkflow historyEnrichmentWorkflow,
        ILogger<NzbgetAdapter> logger)
    {
        public async Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, List<string> ids, CancellationToken ct = default)
        {
            var items = new List<QueueItem>();
            if (client == null) return items;

            var isMonitorPoll = ids.Count > 0;
            var configuredCategory = DownloadClientCategoryFilter.GetConfiguredCategory(client);
            var activeIdentities = new List<NzbgetHistoryEnrichmentWorkflow.ActiveHistoryIdentity>();

            try
            {
                var listResult = await xmlRpcClient.CallAsync(
                    new NzbgetXmlRpcRequest
                    {
                        Client = client,
                        MethodName = "listgroups",
                        Parameters = [0]
                    },
                    ct);
                var arrayData = listResult.Element("array")?.Element("data");

                if (arrayData == null)
                {
                    var message = $"NZBGet returned an invalid queue response for client {LogRedaction.SanitizeText(client.Name ?? client.Id)}.";
                    logger.LogWarning("NZBGet returned an invalid queue response for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
                    if (isMonitorPoll)
                    {
                        throw new DownloadClientAdapterPollingException(message);
                    }
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
                            activeIdentities.Add(
                                historyEnrichmentWorkflow.ParseActiveIdentity(
                                    structElement,
                                    queueItem.Id,
                                    queueItem.Title));
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        logger.LogDebug(ex, "Failed to map NZBGet queue item (non-fatal)");
                    }
                }
            }
            catch (DownloadClientAdapterPollingException)
            {
                throw;
            }
            catch (HttpRequestException httpEx) when (httpEx.StatusCode == HttpStatusCode.Unauthorized || httpEx.StatusCode == HttpStatusCode.Forbidden)
            {
                logger.LogWarning("NZBGet authentication failed for client {ClientName} — check username/password", LogRedaction.SanitizeText(client.Name ?? client.Id));
                if (isMonitorPoll)
                {
                    throw new DownloadClientAdapterPollingException("NZBGet authentication failed.", httpEx);
                }
                return items;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Failed to retrieve NZBGet queue for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
                if (isMonitorPoll)
                {
                    throw new DownloadClientAdapterPollingException("Error polling NZBGet queue.", ex);
                }
                return items;
            }

            await ApplyGlobalDownloadRateFallbackAsync(client, items, ct);
            await historyEnrichmentWorkflow.EnrichQueueAsync(
                client,
                configuredCategory,
                activeIdentities,
                items,
                ct,
                monitoredIds: ids);
            return NzbgetQueueFilter.FilterByIds(items, ids, activeIdentities);
        }

        private async Task ApplyGlobalDownloadRateFallbackAsync(
            DownloadClientConfiguration client,
            List<QueueItem> items,
            CancellationToken ct)
        {
            var candidates = items
                .Where(item => item.Eta == null &&
                    item.DownloadSpeed <= 0 &&
                    string.Equals(item.Status, "downloading", StringComparison.OrdinalIgnoreCase) &&
                    item.Size > item.Downloaded)
                .ToList();
            if (candidates.Count != 1)
            {
                return;
            }

            var downloadRate = await TryGetGlobalDownloadRateAsync(client, ct);
            if (!downloadRate.HasValue || downloadRate.Value <= 0)
            {
                return;
            }

            var item = candidates[0];
            var remainingBytes = Math.Max(0, item.Size - item.Downloaded);
            item.DownloadSpeed = downloadRate.Value;
            item.Eta = CalculateEtaSeconds(remainingBytes, downloadRate.Value);
        }

        private async Task<long?> TryGetGlobalDownloadRateAsync(
            DownloadClientConfiguration client,
            CancellationToken ct)
        {
            try
            {
                var statusResult = await xmlRpcClient.CallAsync(
                    new NzbgetXmlRpcRequest
                    {
                        Client = client,
                        MethodName = "status"
                    },
                    ct);
                return NzbgetResponseMapper.MapStatusDownloadRate(statusResult);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogDebug(
                    ex,
                    "Unable to retrieve NZBGet global download rate for ETA fallback for client {ClientName}",
                    LogRedaction.SanitizeText(client.Name ?? client.Id));
                return null;
            }
        }

        private static int? CalculateEtaSeconds(long remainingBytes, long downloadRate)
        {
            if (remainingBytes <= 0 || downloadRate <= 0)
            {
                return null;
            }

            var etaSeconds = (long)Math.Ceiling(remainingBytes / (double)downloadRate);
            return etaSeconds > int.MaxValue ? int.MaxValue : (int)etaSeconds;
        }
    }
}
