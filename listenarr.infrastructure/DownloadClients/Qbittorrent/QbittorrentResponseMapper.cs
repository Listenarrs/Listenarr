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
                AddedAt = UnixSecondsToDateTimeOrEpoch(addedOn),
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
            var ratioLimitValue = ReadDouble(torrent, "ratio_limit");
            var seedingTimeLimitValue = ReadInt64(torrent, "seeding_time_limit", truncateFraction: false);
            var ratioLimit = (float)(ratioLimitValue ?? -2);
            var seedingTimeLimit = seedingTimeLimitValue ?? -2;
            var seedingTime = GetNullableInt64(torrent, "seeding_time");
            var savePath = GetString(torrent, "save_path");
            var category = GetString(torrent, "category");

            var status = MapDownloadItemStatus(state, progress);
            TimeSpan? remainingTime = eta.HasValue && eta.Value < 8640000 ? TimeSpan.FromSeconds(eta.Value) : null;
            var isStopped = state is "pausedUP" or "stoppedUP";
            var seedLimitReached = QbittorrentSeedLimitEvaluator.HasReachedSeedLimit(
                ratio ?? 0,
                ratioLimit,
                seedingTime,
                seedingTimeLimit,
                globalMaxRatioEnabled,
                globalMaxRatio,
                globalMaxSeedingTimeEnabled,
                globalMaxSeedingTime);
            // A per-torrent limit that is present but unreadable must not fall back to "inherit the
            // global limit", which could authorize removal the real limit would forbid. A seeding
            // time limit is a whole number of minutes, so a fractional one is unreadable too.
            var seedLimitReadable = IsReadableOrAbsent(ratioLimitValue, torrent, "ratio_limit") &&
                IsReadableOrAbsent(seedingTimeLimitValue, torrent, "seeding_time_limit");
            var canBeRemoved = removeCompletedDownloads && seedLimitReached && seedLimitReadable;

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
                // qBittorrent < 5.0 (and compatible APIs like rdt-client) report pausedDL/pausedUP
                // where 5.x reports stoppedDL/stoppedUP (#1047).
                "stoppedDL" or "pausedDL" => DownloadItemStatus.Paused,
                "stoppedUP" or "pausedUP" => DownloadItemStatus.Paused,
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

            if (progress >= 100.0 && (status == DownloadItemStatus.Downloading || state is "uploading" or "stalledUP" or "checkingUP" or "forcedUP" or "stoppedUP" or "pausedUP"))
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
                "stoppedDL" or "pausedDL" => "paused",
                "stoppedUP" or "pausedUP" => "paused",
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

            return progress >= 100.0 && (status == "seeding" || state is "uploading" or "stalledUP" or "checkingUP" or "forcedUP" or "stoppedUP" or "pausedUP")
                ? "completed"
                : status;
        }

        private static string GetString(Dictionary<string, JsonElement> values, string key, string defaultValue = "")
        {
            return values.TryGetValue(key, out var element) ? element.GetString() ?? defaultValue : defaultValue;
        }

        // #1008: qBittorrent's API does not reliably hand back the shape these fields are
        // documented as. A field nominally typed as an integer can arrive as a non-integral
        // number (600.5), exponent notation (6e2, which GetInt64/GetInt32 cannot parse even
        // though the value is whole), or a quoted string or object. Calling GetInt64/GetInt32/
        // GetDouble directly threw FormatException or InvalidOperationException on any of these,
        // which made the caller drop that torrent from the poll result.
        //
        // Numeric strings are deliberately NOT parsed here; a quoted number is treated as
        // unreadable. qBittorrent's WebUI API documents every numeric field read here as an integer
        // or a float, so no documented response needs it.
        //
        // truncateFraction: false is for values that only make sense as whole numbers, where 60.0
        // and 6e1 still read as 60 but 60.5 is unreadable instead of becoming 60.
        private static long? ReadInt64(Dictionary<string, JsonElement> values, string key, bool truncateFraction = true)
        {
            if (!values.TryGetValue(key, out var element) || element.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            if (element.TryGetInt64(out var value))
            {
                return value;
            }

            // (double)long.MaxValue rounds up to 2^63, which no long can hold, so the bound is exclusive
            return element.TryGetDouble(out var doubleValue) &&
                double.IsFinite(doubleValue) &&
                (truncateFraction || double.IsInteger(doubleValue)) &&
                doubleValue >= long.MinValue &&
                doubleValue < (double)long.MaxValue
                ? (long)doubleValue
                : null;
        }

        private static int? ReadInt32(Dictionary<string, JsonElement> values, string key)
        {
            if (!values.TryGetValue(key, out var element) || element.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            if (element.TryGetInt32(out var value))
            {
                return value;
            }

            return element.TryGetDouble(out var doubleValue) &&
                double.IsFinite(doubleValue) &&
                doubleValue >= int.MinValue &&
                doubleValue <= int.MaxValue
                ? (int)doubleValue
                : null;
        }

        private static double? ReadDouble(Dictionary<string, JsonElement> values, string key)
        {
            if (!values.TryGetValue(key, out var element) || element.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            return element.TryGetDouble(out var value) && double.IsFinite(value) ? value : null;
        }

        // added_on only orders and labels the queue view. FromUnixTimeSeconds throws for a value
        // outside the DateTime range (below -62135596800 or above 253402300799), which dropped the
        // whole torrent. A negative value inside that range does not throw, but it is not a real
        // add time either. Both fall back to the epoch, the same value a missing added_on produces.
        private static DateTime UnixSecondsToDateTimeOrEpoch(long unixSeconds)
        {
            return unixSeconds >= 0 && unixSeconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
                ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds).DateTime
                : DateTimeOffset.UnixEpoch.DateTime;
        }

        private static bool IsReadableOrAbsent<T>(T? value, Dictionary<string, JsonElement> values, string key)
            where T : struct
        {
            return value.HasValue || !values.ContainsKey(key);
        }

        private static double GetDouble(Dictionary<string, JsonElement> values, string key, double defaultValue = 0)
        {
            return ReadDouble(values, key) ?? defaultValue;
        }

        private static long GetInt64(Dictionary<string, JsonElement> values, string key, long defaultValue = 0)
        {
            return ReadInt64(values, key) ?? defaultValue;
        }

        private static int? GetNullableInt32(Dictionary<string, JsonElement> values, string key)
        {
            return ReadInt32(values, key);
        }

        private static long? GetNullableInt64(Dictionary<string, JsonElement> values, string key)
        {
            return ReadInt64(values, key);
        }

        private static double? GetNullableDouble(Dictionary<string, JsonElement> values, string key)
        {
            return ReadDouble(values, key);
        }
    }
}
