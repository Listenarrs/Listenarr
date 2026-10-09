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
    internal sealed class TransmissionItemFetchWorkflow(
        TransmissionRpcClient rpcClient,
        ILogger<TransmissionAdapter> logger)
    {
        public async Task<List<DownloadClientItem>> GetItemsAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var items = new List<DownloadClientItem>();
            if (client == null) return items;

            var configuredCategory = DownloadClientCategoryFilter.GetConfiguredCategory(client);
            var sessionConfig = await ReadSessionConfigAsync(client, ct);

            var payload = new
            {
                method = "torrent-get",
                arguments = new
                {
                    fields = new[]
                    {
                        "id", "hashString", "name", "percentDone", "status", "totalSize", "rateDownload", "rateUpload",
                        "leftUntilDone", "eta", "downloadDir", "addedDate", "uploadedEver", "uploadRatio", "labels",
                        "seedRatioMode", "seedRatioLimit", "seedIdleMode", "seedIdleLimit", "secondsSeeding"
                    }
                },
                tag = 3
            };

            try
            {
                var response = await rpcClient.InvokeAsync(client, payload, ct);
                if (!response.TryGetProperty("arguments", out var args) || !args.TryGetProperty("torrents", out var torrents) || torrents.ValueKind != JsonValueKind.Array)
                {
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

                        var item = TransmissionResponseMapper.MapDownloadClientItem(
                            client, torrent, sessionConfig ?? default);
                        if (!sessionConfig.HasValue)
                        {
                            // An unavailable inherited seed policy is not proof that
                            // the torrent has no limits and can be moved or removed.
                            item.CanMoveFiles = false;
                            item.CanBeRemoved = false;
                        }
                        items.Add(item);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        logger.LogDebug(ex, "Failed to map Transmission torrent entry (non-fatal)");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Failed to retrieve Transmission items for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
            }

            return items;
        }

        private async Task<(bool SeedRatioLimited, double SeedRatioLimit, bool IdleSeedingLimitEnabled, int IdleSeedingLimit)?> ReadSessionConfigAsync(
            DownloadClientConfiguration client,
            CancellationToken ct)
        {
            var sessionSeedRatioLimited = false;
            var sessionSeedRatioLimit = 0.0;
            var sessionIdleSeedingLimitEnabled = false;
            var sessionIdleSeedingLimit = 0;
            try
            {
                var sessionPayload = new { method = "session-get", arguments = new { }, tag = 99 };
                var sessionResp = await rpcClient.InvokeAsync(client, sessionPayload, ct);
                if (!sessionResp.TryGetProperty("arguments", out var sessionArgs)
                    || !(sessionArgs.TryGetProperty("seedRatioLimited", out var srl) || sessionArgs.TryGetProperty("seed_ratio_limited", out srl))
                    || !(sessionArgs.TryGetProperty("seedRatioLimit", out var srlv) || sessionArgs.TryGetProperty("seed_ratio_limit", out srlv))
                    || !(sessionArgs.TryGetProperty("idle-seeding-limit-enabled", out var isle) || sessionArgs.TryGetProperty("idle_seeding_limit_enabled", out isle))
                    || !(sessionArgs.TryGetProperty("idle-seeding-limit", out var isl) || sessionArgs.TryGetProperty("idle_seeding_limit", out isl)))
                {
                    return null;
                }
                sessionSeedRatioLimited = srl.GetBoolean();
                sessionSeedRatioLimit = srlv.GetDouble();
                sessionIdleSeedingLimitEnabled = isle.GetBoolean();
                sessionIdleSeedingLimit = isl.GetInt32();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogDebug(ex, "Failed to fetch Transmission session config for seed limit evaluation, will use conservative defaults");
                return null;
            }

            return (sessionSeedRatioLimited, sessionSeedRatioLimit, sessionIdleSeedingLimitEnabled, sessionIdleSeedingLimit);
        }
    }
}
