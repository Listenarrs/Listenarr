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

namespace Listenarr.Infrastructure.DownloadClients.Qbittorrent
{
    internal static class QbittorrentResponseMapper
    {
        public static QueueItem MapQueueItem(
            Dictionary<string, JsonElement> torrent,
            DownloadClientConfiguration client,
            List<Dictionary<string, JsonElement>> files)
        {
            var name = GetString(torrent, "name");
            var progress = GetDouble(torrent, "progress") * 100;
            var size = GetInt64(torrent, "size");
            var downloaded = GetInt64(torrent, "downloaded");
            var dlspeed = GetDouble(torrent, "dlspeed");
            var eta = GetNullableInt32(torrent, "eta");
            var state = GetString(torrent, "state", "unknown");
            var hash = GetString(torrent, "hash");
            var addedOn = GetInt64(torrent, "added_on");
            var numSeeds = GetNullableInt32(torrent, "num_seeds");
            var numLeechs = GetNullableInt32(torrent, "num_leechs");
            var ratio = GetNullableDouble(torrent, "ratio");
            var savePath = GetString(torrent, "save_path");
            var status = MapQueueStatus(state, progress);

            return new QueueItem
            {
                Id = hash,
                Title = name,
                Quality = "Unknown",
                Status = status,
                Progress = progress,
                Size = size,
                Downloaded = downloaded,
                DownloadSpeed = dlspeed,
                Eta = eta >= 8640000 ? null : eta,
                DownloadClient = client.Name,
                DownloadClientId = client.Id,
                DownloadClientType = "qbittorrent",
                AddedAt = DateTimeOffset.FromUnixTimeSeconds(addedOn).DateTime,
                Seeders = numSeeds,
                Leechers = numLeechs,
                Ratio = ratio,
                CanPause = status == "downloading" || status == "queued",
                CanRemove = true,
                RemotePath = savePath,
                LocalPath = savePath,
                SourceFiles = TorrentClientPathMapper.BuildQbittorrentSourceFiles(savePath, files),
                ContentPath = TorrentClientPathMapper.ResolveQbittorrentContentPath(savePath, files)
            };
        }

        public static DownloadClientItem MapDownloadClientItem(
            Dictionary<string, JsonElement> torrent,
            DownloadClientConfiguration client,
            bool removeCompletedDownloads,
            bool globalMaxRatioEnabled,
            float globalMaxRatio,
            bool globalMaxSeedingTimeEnabled,
            long globalMaxSeedingTime)
        {
            var name = GetString(torrent, "name");
            var progress = GetDouble(torrent, "progress") * 100;
            var size = GetInt64(torrent, "size");
            var downloaded = GetInt64(torrent, "downloaded");
            var dlspeed = GetDouble(torrent, "dlspeed");
            var eta = GetNullableInt32(torrent, "eta");
            var state = GetString(torrent, "state", "unknown");
            var hash = GetString(torrent, "hash");
            var numSeeds = GetNullableInt32(torrent, "num_seeds");
            var numLeechs = GetNullableInt32(torrent, "num_leechs");
            var ratio = GetNullableDouble(torrent, "ratio");
            var ratioLimit = (float)GetDouble(torrent, "ratio_limit", -2);
            var seedingTimeLimit = GetInt64(torrent, "seeding_time_limit", -2);
            var seedingTime = GetNullableInt64(torrent, "seeding_time");
            var savePath = GetString(torrent, "save_path");
            var category = GetString(torrent, "category");

            var status = MapDownloadItemStatus(state, progress);
            TimeSpan? remainingTime = eta.HasValue && eta.Value < 8640000 ? TimeSpan.FromSeconds(eta.Value) : null;
            var isStopped = IsFinishedStoppedState(state);
            var seedLimitReached = QbittorrentSeedLimitEvaluator.HasReachedSeedLimit(
                ratio ?? 0,
                ratioLimit,
                seedingTime,
                seedingTimeLimit,
                globalMaxRatioEnabled,
                globalMaxRatio,
                globalMaxSeedingTimeEnabled,
                globalMaxSeedingTime);
            var canBeRemoved = removeCompletedDownloads && seedLimitReached;

            return new DownloadClientItem
            {
                DownloadId = hash,
                Title = name,
                Category = category,
                Status = status,
                TotalSize = size,
                RemainingSize = size - downloaded,
                RemainingTime = remainingTime,
                SeedRatio = ratio,
                OutputPath = savePath,
                Message = state,
                Progress = progress,
                DownloadSpeed = dlspeed,
                Seeders = numSeeds ?? 0,
                Leechers = numLeechs ?? 0,
                CanBeRemoved = canBeRemoved,
                CanMoveFiles = canBeRemoved && isStopped,
                DownloadClientInfo = DownloadClientItemClientInfo.FromClient(
                    clientId: client.Id,
                    clientName: client.Name,
                    clientType: "qbittorrent",
                    protocol: DownloadProtocol.Torrent,
                    removeCompletedDownloads: removeCompletedDownloads,
                    hasPostImportCategory: !string.IsNullOrEmpty(client.Settings?.GetValueOrDefault("postImportCategory")?.ToString()))
            };
        }

        public static DownloadItemStatus MapDownloadItemStatus(string state, double progress)
        {
            var status = state switch
            {
                "downloading" => DownloadItemStatus.Downloading,
                "metaDL" => DownloadItemStatus.Downloading,
                "forcedDL" => DownloadItemStatus.Downloading,
                "forcedMetaDL" => DownloadItemStatus.Downloading,
                "stalledDL" => DownloadItemStatus.Downloading,
                "checkingDL" => DownloadItemStatus.Downloading,
                "stoppedDL" => DownloadItemStatus.Paused,
                "stoppedUP" => DownloadItemStatus.Paused,
                "pausedDL" => DownloadItemStatus.Paused,
                "pausedUP" => DownloadItemStatus.Paused,
                "queuedDL" => DownloadItemStatus.Queued,
                "queuedUP" => DownloadItemStatus.Queued,
                "uploading" => DownloadItemStatus.Downloading,
                "stalledUP" => DownloadItemStatus.Downloading,
                "checkingUP" => DownloadItemStatus.Downloading,
                "forcedUP" => DownloadItemStatus.Downloading,
                "checkingResumeData" => DownloadItemStatus.Downloading,
                "moving" => DownloadItemStatus.Downloading,
                "error" => DownloadItemStatus.Failed,
                "missingFiles" => DownloadItemStatus.Failed,
                _ => DownloadItemStatus.Warning
            };

            if (progress >= 100.0 && (status == DownloadItemStatus.Downloading || IsSeedingOrFinishedState(state)))
            {
                return DownloadItemStatus.Completed;
            }

            return status;
        }

        public static string MapQueueStatus(string state, double progress)
        {
            var status = state switch
            {
                "downloading" => "downloading",
                "metaDL" => "downloading",
                "forcedDL" => "downloading",
                "forcedMetaDL" => "downloading",
                "stalledDL" => "downloading",
                "checkingDL" => "downloading",
                "stoppedDL" => "paused",
                "stoppedUP" => "paused",
                "pausedDL" => "paused",
                "pausedUP" => "paused",
                "queuedDL" => "queued",
                "queuedUP" => "queued",
                "uploading" => "seeding",
                "stalledUP" => "seeding",
                "checkingUP" => "seeding",
                "forcedUP" => "seeding",
                "checkingResumeData" => "downloading",
                "moving" => "downloading",
                "error" => "failed",
                "missingFiles" => "failed",
                _ => "unknown"
            };

            return progress >= 100.0 && (status == "seeding" || IsSeedingOrFinishedState(state))
                ? "completed"
                : status;
        }

        /// <summary>
        /// A torrent that has finished downloading and is no longer transferring.
        /// qBittorrent 5.0 (Web API 2.11.0) renamed the paused states to stopped states,
        /// so 4.x servers report "pausedUP" where 5.x servers report "stoppedUP".
        /// Both spellings stay live for as long as 4.x installs do.
        /// </summary>
        public static bool IsFinishedStoppedState(string state)
        {
            return state is "stoppedUP" or "pausedUP";
        }

        /// <summary>
        /// States a torrent occupies once its content is fully downloaded, whether it is
        /// still seeding, being rechecked, or stopped. At 100% progress these all mean the
        /// payload is on disk and ready to import.
        /// </summary>
        private static bool IsSeedingOrFinishedState(string state)
        {
            return state is "uploading" or "stalledUP" or "checkingUP" or "forcedUP"
                || IsFinishedStoppedState(state);
        }

        private static string GetString(Dictionary<string, JsonElement> values, string key, string defaultValue = "")
        {
            return values.TryGetValue(key, out var element) ? element.GetString() ?? defaultValue : defaultValue;
        }

        private static double GetDouble(Dictionary<string, JsonElement> values, string key, double defaultValue = 0)
        {
            return values.TryGetValue(key, out var element) ? element.GetDouble() : defaultValue;
        }

        private static long GetInt64(Dictionary<string, JsonElement> values, string key, long defaultValue = 0)
        {
            return values.TryGetValue(key, out var element) ? element.GetInt64() : defaultValue;
        }

        private static int? GetNullableInt32(Dictionary<string, JsonElement> values, string key)
        {
            return values.TryGetValue(key, out var element) ? element.GetInt32() : null;
        }

        private static long? GetNullableInt64(Dictionary<string, JsonElement> values, string key)
        {
            return values.TryGetValue(key, out var element) ? element.GetInt64() : null;
        }

        private static double? GetNullableDouble(Dictionary<string, JsonElement> values, string key)
        {
            return values.TryGetValue(key, out var element) ? element.GetDouble() : null;
        }
    }
}
