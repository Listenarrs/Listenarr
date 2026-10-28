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

namespace Listenarr.Infrastructure.DownloadClients.Transmission
{
    internal sealed class TransmissionQueueFetchWorkflow(
        TransmissionRpcClient rpcClient,
        ILogger<TransmissionAdapter> logger)
    {
        public async Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, List<string> ids, CancellationToken ct = default)
        {
            var items = new List<QueueItem>();
            if (client == null) return items;

            var isMonitorPoll = ids.Count > 0;
            var configuredCategory = DownloadClientCategoryFilter.GetConfiguredCategory(client);

            // Keep local filtering for monitor calls. Listenarr stores hash-shaped IDs,
            // while Transmission RPC ID targeting varies across server versions.
            var payload = new
            {
                method = "torrent-get",
                arguments = new
                {
                    fields = new[]
                    {
                        "id", "hashString", "name", "percentDone", "status", "totalSize", "rateDownload", "rateUpload",
                        "leftUntilDone", "eta", "downloadDir", "addedDate", "uploadedEver", "uploadRatio", "labels"
                    }
                },
                tag = 3
            };

            try
            {
                var response = await rpcClient.InvokeAsync(client, payload, ct);
                if (!response.TryGetProperty("arguments", out var args) || !args.TryGetProperty("torrents", out var torrents) || torrents.ValueKind != JsonValueKind.Array)
                {
                    var message = $"Transmission returned an invalid queue response for client {LogRedaction.SanitizeText(client.Name ?? client.Id)}.";
                    logger.LogWarning("Transmission returned an invalid queue response for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
                    if (isMonitorPoll)
                    {
                        throw new DownloadClientAdapterPollingException(message);
                    }
                    return items;
                }

                foreach (var torrent in torrents.EnumerateArray())
                {
                    try
                    {
                        var labels = TransmissionResponseMapper.ExtractLabels(torrent);
                        if (!DownloadClientCategoryFilter.MatchesAny(configuredCategory, labels))
                        {
                            continue;
                        }

                        items.Add(TransmissionResponseMapper.MapQueueItem(client, torrent));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        logger.LogDebug(ex, "Failed to map Transmission torrent entry (non-fatal)");
                    }
                }
            }
            catch (DownloadClientAdapterPollingException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Failed to retrieve Transmission queue for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
                if (isMonitorPoll)
                {
                    throw new DownloadClientAdapterPollingException("Error polling Transmission queue.", ex);
                }
            }

            return FilterByIds(items, ids);
        }

        private static List<QueueItem> FilterByIds(List<QueueItem> items, List<string> ids)
        {
            if (ids.Count == 0)
            {
                return items;
            }

            var idSet = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return [.. items.Where(item => !string.IsNullOrWhiteSpace(item.Id) && idSet.Contains(item.Id))];
        }
    }
}
