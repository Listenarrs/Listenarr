/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Nzbget
{
    internal sealed class NzbgetItemFetchWorkflow(
        NzbgetXmlRpcClient xmlRpcClient,
        NzbgetHistoryEnrichmentWorkflow historyEnrichmentWorkflow,
        ILogger<NzbgetAdapter> logger)
    {
        public async Task<List<DownloadClientItem>> GetItemsAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var items = new List<DownloadClientItem>();
            if (client == null) return items;

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
                            activeIdentities.Add(
                                historyEnrichmentWorkflow.ParseActiveIdentity(
                                    structElement,
                                    downloadClientItem.DownloadId,
                                    downloadClientItem.Title));
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        logger.LogDebug(ex, "Failed to map NZBGet queue item (non-fatal)");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Failed to retrieve NZBGet items for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
                return items;
            }

            await ApplyGlobalDownloadRateFallbackAsync(client, items, ct);
            await historyEnrichmentWorkflow.EnrichItemsAsync(
                client,
                configuredCategory,
                activeIdentities,
                items,
                ct);
            return items;
        }

        private async Task ApplyGlobalDownloadRateFallbackAsync(
            DownloadClientConfiguration client,
            List<DownloadClientItem> items,
            CancellationToken ct)
        {
            var candidates = items
                .Where(item => item.RemainingTime == null &&
                    item.DownloadSpeed <= 0 &&
                    item.Status == DownloadItemStatus.Downloading &&
                    item.RemainingSize > 0)
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
            item.DownloadSpeed = downloadRate.Value;
            var etaSeconds = CalculateEtaSeconds(item.RemainingSize, downloadRate.Value);
            item.RemainingTime = etaSeconds.HasValue
                ? TimeSpan.FromSeconds(etaSeconds.Value)
                : null;
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
