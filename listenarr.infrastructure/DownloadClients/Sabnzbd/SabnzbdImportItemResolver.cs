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
    internal sealed class SabnzbdImportItemResolver(
        IHttpClientFactory httpFactory,
        SabnzbdRequestBuilder requestBuilder,
        ILogger logger,
        string clientType)
    {
        public async Task<DownloadClientItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            DownloadClientItem item,
            CancellationToken ct = default)
        {
            var result = item.Clone();

            if (!string.IsNullOrWhiteSpace(result.OutputPath))
            {
                var localPath = result.OutputPath;
                if (SabnzbdImportPathResolver.IsExistingLocalPath(localPath))
                {
                    result.OutputPath = localPath;
                    return result;
                }
            }

            // SABnzbd's active queue path can be absent or stale. The completed history
            // record's storage field is the authoritative import location once available.
            var storage = await ResolveHistoryStorageAsync(client, item.DownloadId, ct);
            if (!string.IsNullOrWhiteSpace(storage))
            {
                result.OutputPath = storage;
                logger.LogDebug(
                    "Resolved SABnzbd content path for {NzoId}: {ContentPath}",
                    item.DownloadId,
                    storage);
            }

            return result;
        }

        public async Task<QueueItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            QueueItem queueItem,
            CancellationToken ct = default)
        {
            var result = queueItem.Clone();

            if (!string.IsNullOrWhiteSpace(result.ContentPath))
            {
                var localPath = result.ContentPath;
                if (SabnzbdImportPathResolver.IsExistingLocalPath(localPath))
                {
                    result.ContentPath = localPath;
                    return result;
                }
            }

            // Prefer SABnzbd history storage over a guessed or missing queue ContentPath.
            // This keeps active queue telemetry separate from import path resolution.
            var storage = await ResolveHistoryStorageAsync(client, queueItem.Id, ct);
            if (!string.IsNullOrWhiteSpace(storage))
            {
                result.ContentPath = storage;
                logger.LogDebug($"Resolved SABnzbd content path for {queueItem.Id}: {result.ContentPath}");
            }

            return result;
        }

        // SABnzbd history returns 10 slots by default, so a job that finished a while ago may not be on
        // the first page. Ask for the job by id first, then fall back to a bounded paged scan.
        private const int HistoryPageSize = 100;
        private const int MaxHistoryPages = 10;

        private sealed record HistoryLookup(bool Found, string? Storage, int SlotCount);

        private async Task<string?> ResolveHistoryStorageAsync(
            DownloadClientConfiguration client,
            string nzoId,
            CancellationToken ct)
        {
            try
            {
                var requestContext = requestBuilder.CreateContext(client);
                if (!requestContext.HasApiKey)
                {
                    logger.LogWarning("SABnzbd API key not configured for client {ClientId}", client.Id);
                    return null;
                }

                HistoryLookup? byId = null;
                try
                {
                    byId = await QueryHistoryAsync(requestContext, nzoId, new Dictionary<string, string>
                    {
                        ["nzo_ids"] = nzoId
                    }, ct);
                }
                catch (JsonException)
                {
                    // A malformed targeted response must not skip the paged scan below.
                    logger.LogWarning("SABnzbd returned malformed JSON for the targeted history lookup of {NzoId}; falling back to the paged scan", nzoId);
                }

                if (byId is { Found: true })
                {
                    return byId.Storage;
                }

                for (var page = 0; page < MaxHistoryPages; page++)
                {
                    var lookup = await QueryHistoryAsync(requestContext, nzoId, new Dictionary<string, string>
                    {
                        ["start"] = (page * HistoryPageSize).ToString(CultureInfo.InvariantCulture),
                        ["limit"] = HistoryPageSize.ToString(CultureInfo.InvariantCulture)
                    }, ct);
                    if (lookup == null) return null;
                    if (lookup.Found) return lookup.Storage;
                    if (lookup.SlotCount < HistoryPageSize) break;
                }

                logger.LogWarning("Download {NzoId} not found in SABnzbd history", nzoId);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Error resolving import item for SABnzbd download {NzoId}", nzoId);
                return null;
            }
        }

        private async Task<HistoryLookup?> QueryHistoryAsync(
            SabnzbdRequestContext requestContext,
            string nzoId,
            Dictionary<string, string> filter,
            CancellationToken ct)
        {
            filter["mode"] = "history";
            filter["output"] = "json";
            var historyUrl = requestBuilder.BuildUrl(requestContext, filter);
            var http = httpFactory.CreateClient(clientType);
            var historyResp = await http.GetAsync(historyUrl, ct);

            if (!historyResp.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to query SABnzbd history for download {NzoId}", nzoId);
                return null;
            }

            var historyText = await historyResp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(historyText))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(historyText);
            if (!doc.RootElement.TryGetProperty("history", out var history) ||
                !history.TryGetProperty("slots", out var slots) ||
                slots.ValueKind != JsonValueKind.Array)
            {
                logger.LogWarning("Invalid SABnzbd history response format");
                return null;
            }

            foreach (var slot in slots.EnumerateArray())
            {
                var slotNzoId = slot.TryGetProperty("nzo_id", out var nzo) ? nzo.GetString() ?? string.Empty : string.Empty;
                if (!string.Equals(slotNzoId, nzoId, StringComparison.OrdinalIgnoreCase)) continue;

                var storage = SabnzbdImportPathResolver.GetStoragePath(slot);
                if (string.IsNullOrWhiteSpace(storage))
                {
                    logger.LogWarning("No storage path found for SABnzbd download {NzoId}", nzoId);
                    return new HistoryLookup(true, null, slots.GetArrayLength());
                }

                return new HistoryLookup(true, storage, slots.GetArrayLength());
            }

            return new HistoryLookup(false, null, slots.GetArrayLength());
        }
    }
}
