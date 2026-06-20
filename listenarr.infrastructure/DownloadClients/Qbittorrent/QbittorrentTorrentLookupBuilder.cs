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

namespace Listenarr.Infrastructure.DownloadClients.Qbittorrent
{
    internal static class QbittorrentTorrentLookupBuilder
    {
        public static List<(string Hash, string Name, string SavePath, string ContentPath, double Progress, long AmountLeft, string State, long Size, string Category, long? SeedingTime, double Ratio, float RatioLimit, long SeedingTimeLimit, bool CanMoveFiles, bool CanBeRemoved)> Build(
            IEnumerable<Dictionary<string, JsonElement>> torrents,
            bool removeCompletedDownloads,
            bool globalMaxRatioEnabled,
            float globalMaxRatio,
            bool globalMaxSeedingTimeEnabled,
            long globalMaxSeedingTime)
        {
            var torrentLookup = new List<(string Hash, string Name, string SavePath, string ContentPath, double Progress, long AmountLeft, string State, long Size, string Category, long? SeedingTime, double Ratio, float RatioLimit, long SeedingTimeLimit, bool CanMoveFiles, bool CanBeRemoved)>();
            foreach (var torrent in torrents)
            {
                var hash = torrent.TryGetValue("hash", out var hashElement) ? hashElement.GetString() ?? "" : "";
                var name = torrent.TryGetValue("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
                var savePath = torrent.TryGetValue("save_path", out var savePathElement) ? savePathElement.GetString() ?? "" : "";
                var contentPath = torrent.TryGetValue("content_path", out var contentPathElement) ? contentPathElement.GetString() ?? "" : "";
                var progress = torrent.TryGetValue("progress", out var progressElement) ? progressElement.GetDouble() : 0.0;
                var amountLeft = torrent.TryGetValue("amount_left", out var amountLeftElement) ? amountLeftElement.GetInt64() : 0L;
                var state = torrent.TryGetValue("state", out var stateElement) ? stateElement.GetString() ?? "" : "";
                var size = torrent.TryGetValue("size", out var sizeElement) ? sizeElement.GetInt64() : 0L;
                var category = torrent.TryGetValue("category", out var categoryElement) ? categoryElement.GetString() ?? "" : "";
                var seedingTime = torrent.TryGetValue("seeding_time", out var seedingTimeElement) ? seedingTimeElement.GetInt64() : (long?)null;
                var ratio = torrent.TryGetValue("ratio", out var ratioElement) ? ratioElement.GetDouble() : 0.0;
                var ratioLimit = torrent.TryGetValue("ratio_limit", out var ratioLimitElement) ? (float)ratioLimitElement.GetDouble() : -2f;
                var seedingTimeLimit = torrent.TryGetValue("seeding_time_limit", out var seedingTimeLimitElement) ? seedingTimeLimitElement.GetInt64() : -2L;

                var isStopped = state is "pausedUP" or "stoppedUP";
                var seedLimitReached = QbittorrentSeedLimitEvaluator.HasReachedSeedLimit(
                    ratio,
                    ratioLimit,
                    seedingTime,
                    seedingTimeLimit,
                    globalMaxRatioEnabled,
                    globalMaxRatio,
                    globalMaxSeedingTimeEnabled,
                    globalMaxSeedingTime);
                var canBeRemoved = removeCompletedDownloads && seedLimitReached;
                var canMoveFiles = canBeRemoved && isStopped;

                torrentLookup.Add((hash, name, savePath, contentPath, progress, amountLeft, state, size, category, seedingTime, ratio, ratioLimit, seedingTimeLimit, canMoveFiles, canBeRemoved));
            }

            return torrentLookup;
        }
    }
}
