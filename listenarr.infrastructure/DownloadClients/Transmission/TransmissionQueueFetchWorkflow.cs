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
        private const string IncompleteSnapshotMessage = "Incomplete Transmission queue snapshot.";

        public async Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, List<string> ids, CancellationToken ct = default, bool ignoreCategory = false)
        {
            var items = new List<QueueItem>();
            if (client == null) return items;

            // Tracked lookups for orphan cleanup ignore labels, so a tracked torrent
            // stays visible after its label changes. Display and discovery keep them.
            var configuredCategory = ignoreCategory ? null : DownloadClientCategoryFilter.GetConfiguredCategory(client);

            // Keep local filtering for monitor calls. Listenarr stores hash-shaped IDs,
            // while Transmission RPC ID targeting varies across server versions.
            var payload = new
            {
                method = "torrent-get",
                arguments = new
                {
                    fields = ignoreCategory ? ["id", "hashString"] : new[]
                    {
                        "id", "hashString", "name", "percentDone", "status", "totalSize", "rateDownload", "rateUpload",
                        "leftUntilDone", "eta", "downloadDir", "addedDate", "uploadedEver", "uploadRatio", "labels"
                    }
                },
                tag = 3
            };

            try
            {
                var response = await rpcClient.InvokeAsync(client, payload, ct, allowDuplicateProperties: ids.Count > 0 && !ignoreCategory);

                // HTTP 200 only means the RPC call was answered. An RPC error can still
                // carry an empty torrents array, which must not read as an empty queue.
                if (!response.TryGetProperty("result", out var resultProp) ||
                    resultProp.ValueKind != JsonValueKind.String ||
                    !string.Equals(resultProp.GetString(), "success", StringComparison.OrdinalIgnoreCase))
                {
                    var message = $"Transmission RPC did not report success for client {LogRedaction.SanitizeText(client.Name ?? client.Id)}.";
                    logger.LogWarning("Transmission RPC did not report success for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
                    throw new DownloadClientAdapterPollingException(message);
                }

                if (!response.TryGetProperty("arguments", out var args) || !args.TryGetProperty("torrents", out var torrents) || torrents.ValueKind != JsonValueKind.Array)
                {
                    var message = $"Transmission returned an invalid queue response for client {LogRedaction.SanitizeText(client.Name ?? client.Id)}.";
                    logger.LogWarning("Transmission returned an invalid queue response for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
                    throw new DownloadClientAdapterPollingException(message);
                }

                var needsNumericId = ignoreCategory && ids.Any(id => int.TryParse(id, out _));
                var needsHash = ignoreCategory && ids.Any(id => !int.TryParse(id, out _));
                foreach (var torrent in torrents.EnumerateArray())
                {
                    if (ignoreCategory)
                    {
                        AddTrackedIdentities(torrent, needsHash, needsNumericId, items);
                        continue;
                    }

                    try
                    {
                        var labels = TransmissionResponseMapper.ExtractLabels(torrent);
                        if (!DownloadClientCategoryFilter.MatchesAny(configuredCategory, labels))
                        {
                            continue;
                        }

                        var item = TransmissionResponseMapper.MapQueueItem(client, torrent);
                        if (ids.Count == 0 && string.IsNullOrWhiteSpace(item.Id))
                        {
                            throw new DownloadClientAdapterPollingException(IncompleteSnapshotMessage);
                        }

                        items.Add(item);
                    }
                    catch (Exception ex) when (ex is not DownloadClientAdapterPollingException && ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        // A full poll drives orphan cleanup, so a torrent we cannot read
                        // must fail the snapshot instead of silently dropping out of it.
                        if (ids.Count == 0)
                        {
                            throw new DownloadClientAdapterPollingException(IncompleteSnapshotMessage, ex);
                        }

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
                throw new DownloadClientAdapterPollingException("Error polling Transmission queue.", ex);
            }

            return FilterByIds(items, ids);
        }

        private static void AddTrackedIdentities(JsonElement torrent, bool needsHash, bool needsNumericId, List<QueueItem> items)
        {
            var hash = needsHash ? TransmissionResponseMapper.GetTorrentHash(torrent) : string.Empty;
            if (needsHash && torrent.TryGetProperty("hash_string", out _) && torrent.TryGetProperty("hashString", out var alternateHash) &&
                (alternateHash.ValueKind != JsonValueKind.String || !string.Equals(hash, alternateHash.GetString(), StringComparison.OrdinalIgnoreCase)))
            {
                throw new DownloadClientAdapterPollingException("Transmission returned conflicting hashes for a torrent.");
            }

            var numericId = torrent.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var value) && value > 0
                ? value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null;

            // Both ID formats have been used by the adapter. If a stored format
            // cannot be checked against this entry, absence is ambiguous.
            if ((needsHash && string.IsNullOrWhiteSpace(hash)) || (needsNumericId && numericId == null))
            {
                throw new DownloadClientAdapterPollingException(IncompleteSnapshotMessage);
            }

            if (needsHash) items.Add(new QueueItem { Id = hash });
            if (needsNumericId) items.Add(new QueueItem { Id = numericId! });
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
