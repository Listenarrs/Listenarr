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

namespace Listenarr.Infrastructure.DownloadClients.Qbittorrent
{
    internal static class QbittorrentResponseMapper
    {
        // Valid argument range for DateTimeOffset.FromUnixTimeSeconds; a garbage timestamp outside
        // it would otherwise throw and drop the whole torrent from the snapshot (#1008 review).
        private const long MinUnixSeconds = -62135596800;
        private const long MaxUnixSeconds = 253402300799;

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
                AddedAt = DateTimeOffset.FromUnixTimeSeconds(
                    Math.Clamp(addedOn, MinUnixSeconds, MaxUnixSeconds)).DateTime,
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
            var ratioLimitReadable = TryGetSeedLimitDouble(torrent, "ratio_limit", out var ratioLimitValue);
            var ratioLimit = (float)ratioLimitValue;
            var seedingTimeLimitReadable = TryGetSeedLimitInt64(torrent, "seeding_time_limit", out var seedingTimeLimit);
            var seedPolicyReadable = ratioLimitReadable && seedingTimeLimitReadable;
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
            // When a seed-limit field is present but unreadable, the policy is unknown; do not let
            // the tolerant -2 ("inherit global") fallback silently authorize movement/removal that
            // the configured limits would block (#1008 review).
            var canBeRemoved = removeCompletedDownloads && seedLimitReached && seedPolicyReadable;

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

        private static double GetDouble(Dictionary<string, JsonElement> values, string key, double defaultValue = 0)
        {
            return values.TryGetValue(key, out var element) && TryReadDouble(element, out var result) ? result : defaultValue;
        }

        private static long GetInt64(Dictionary<string, JsonElement> values, string key, long defaultValue = 0)
        {
            return values.TryGetValue(key, out var element) && TryReadInt64(element, out var result) ? result : defaultValue;
        }

        private static int? GetNullableInt32(Dictionary<string, JsonElement> values, string key)
        {
            return values.TryGetValue(key, out var element) && TryReadInt64(element, out var result)
                ? (int)Math.Clamp(result, int.MinValue, int.MaxValue)
                : null;
        }

        private static long? GetNullableInt64(Dictionary<string, JsonElement> values, string key)
        {
            return values.TryGetValue(key, out var element) && TryReadInt64(element, out var result) ? result : null;
        }

        private static double? GetNullableDouble(Dictionary<string, JsonElement> values, string key)
        {
            return values.TryGetValue(key, out var element) && TryReadDouble(element, out var result) ? result : null;
        }

        // Seed-limit fields default to -2 ("inherit global") only when absent. A present but
        // unreadable value returns false so the caller can refuse movement/removal instead of
        // defaulting to the permissive inherit-global behavior.
        private static bool TryGetSeedLimitDouble(Dictionary<string, JsonElement> values, string key, out double value)
        {
            value = -2;
            if (!values.TryGetValue(key, out var element))
            {
                return true;
            }

            if (TryReadDouble(element, out var parsed))
            {
                value = parsed;
                return true;
            }

            return false;
        }

        private static bool TryGetSeedLimitInt64(Dictionary<string, JsonElement> values, string key, out long value)
        {
            value = -2;
            if (!values.TryGetValue(key, out var element))
            {
                return true;
            }

            if (TryReadInt64(element, out var parsed))
            {
                value = parsed;
                return true;
            }

            return false;
        }

        // qBittorrent and the various libtorrent builds behind it are not consistent about numeric
        // JSON: a field can arrive as an integer, as a decimal or exponent, or as a quoted string.
        // Reading it with JsonElement.GetInt64()/GetInt32() throws FormatException on anything but a
        // plain integer, and a single such value used to fail the entire queue fetch (#1008). Read
        // numbers tolerantly and fall back to the default rather than throwing.
        private static bool TryReadInt64(JsonElement element, out long value)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Number:
                    if (element.TryGetInt64(out value))
                    {
                        return true;
                    }

                    if (element.TryGetDouble(out var numeric) && TryConvertFiniteDoubleToInt64(numeric, out value))
                    {
                        return true;
                    }

                    break;
                case JsonValueKind.String:
                    var text = element.GetString();
                    if (long.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
                    {
                        return true;
                    }

                    if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                        && TryConvertFiniteDoubleToInt64(parsed, out value))
                    {
                        return true;
                    }

                    break;
            }

            value = 0;
            return false;
        }

        // A double that is NaN, infinite, or outside the Int64 range cannot be cast to long
        // safely (the cast is undefined/overflows), so treat it as unreadable.
        private static bool TryConvertFiniteDoubleToInt64(double candidate, out long value)
        {
            // 9.2e18 is safely inside Int64's range (long.MaxValue is ~9.223e18), so the cast
            // cannot overflow. long.MaxValue itself rounds up when converted to double, which is
            // why an explicit sub-range bound is used instead of comparing against long.MaxValue.
            if (double.IsFinite(candidate) && candidate > -9.2e18 && candidate < 9.2e18)
            {
                value = (long)candidate;
                return true;
            }

            value = 0;
            return false;
        }

        private static bool TryReadDouble(JsonElement element, out double value)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Number:
                    // Reject NaN/Infinity: they map through to fields like Progress and then throw
                    // when the queue is serialized, outside the per-torrent guard (#1008 review).
                    if (element.TryGetDouble(out value) && double.IsFinite(value))
                    {
                        return true;
                    }

                    break;
                case JsonValueKind.String:
                    if (double.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value)
                        && double.IsFinite(value))
                    {
                        return true;
                    }

                    break;
            }

            value = 0;
            return false;
        }
    }
}
