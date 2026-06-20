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
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.DownloadClients.Transmission
{
    internal static class TransmissionResponseMapper
    {
        public static QueueItem MapQueueItem(DownloadClientConfiguration client, JsonElement torrent)
        {
            var id = GetString(torrent, "hash_string", "hashString");
            if (string.IsNullOrEmpty(id) && torrent.TryGetProperty("id", out var numericId))
            {
                id = numericId.GetInt32().ToString(CultureInfo.InvariantCulture);
            }

            var name = GetString(torrent, "name");
            var percentDone = GetDouble(torrent, "percent_done", "percentDone") * 100;
            var totalSize = GetInt64(torrent, "total_size", "totalSize");
            var leftUntilDone = GetInt64(torrent, "left_until_done", "leftUntilDone");
            var rateDownload = GetDouble(torrent, "rate_download", "rateDownload");
            var eta = torrent.TryGetProperty("eta", out var etaProp) ? etaProp.GetInt32() : -1;
            var downloadDir = GetString(torrent, "download_dir", "downloadDir");
            var statusCode = torrent.TryGetProperty("status", out var statusProp) ? statusProp.GetInt32() : 0;
            var addedDate = GetInt64(torrent, "added_date", "addedDate");
            var uploadRatio = GetDouble(torrent, "upload_ratio", "uploadRatio");
            var downloaded = Math.Max(0, totalSize - leftUntilDone);
            var status = MapQueueStatus(statusCode, percentDone);
            var addedAt = addedDate > 0 ? DateTimeOffset.FromUnixTimeSeconds(addedDate).UtcDateTime : DateTime.UtcNow;
            var contentPath = !string.IsNullOrEmpty(downloadDir) && !string.IsNullOrEmpty(name)
                ? FileUtils.CombineWithOptionalBase(downloadDir, name)
                : downloadDir;
            var primaryLabel = ExtractLabels(torrent).FirstOrDefault() ?? string.Empty;

            return new QueueItem
            {
                Id = id,
                Title = name,
                Quality = string.IsNullOrWhiteSpace(primaryLabel) ? "Unknown" : primaryLabel,
                Status = status,
                Progress = percentDone,
                Size = totalSize,
                Downloaded = downloaded,
                DownloadSpeed = rateDownload,
                Eta = eta >= 0 ? eta : null,
                DownloadClient = client.Name ?? client.Id ?? "Transmission",
                DownloadClientId = client.Id ?? string.Empty,
                DownloadClientType = "transmission",
                AddedAt = addedAt,
                Ratio = uploadRatio,
                CanPause = status is "downloading" or "queued",
                CanRemove = true,
                RemotePath = downloadDir,
                LocalPath = downloadDir,
                ContentPath = contentPath
            };
        }

        public static DownloadClientItem MapDownloadClientItem(
            DownloadClientConfiguration client,
            JsonElement torrent,
            (bool SeedRatioLimited, double SeedRatioLimit, bool IdleSeedingLimitEnabled, int IdleSeedingLimit) sessionConfig)
        {
            var hash = GetString(torrent, "hash_string", "hashString");
            var numericId = torrent.TryGetProperty("id", out var numericIdProp) ? numericIdProp.GetInt32() : 0;
            var name = GetString(torrent, "name");
            var percentDone = GetDouble(torrent, "percent_done", "percentDone") * 100;
            var totalSize = GetInt64(torrent, "total_size", "totalSize");
            var leftUntilDone = GetInt64(torrent, "left_until_done", "leftUntilDone");
            var rateDownload = GetDouble(torrent, "rate_download", "rateDownload");
            var eta = torrent.TryGetProperty("eta", out var etaProp) ? etaProp.GetInt32() : -1;
            var downloadDir = GetString(torrent, "download_dir", "downloadDir");
            var statusCode = torrent.TryGetProperty("status", out var statusProp) ? statusProp.GetInt32() : 0;
            var uploadRatio = GetDouble(torrent, "upload_ratio", "uploadRatio");
            var seedRatioMode = GetInt32(torrent, "seed_ratio_mode", "seedRatioMode");
            var seedRatioLimit = GetDouble(torrent, "seed_ratio_limit", "seedRatioLimit");
            var seedIdleMode = GetInt32(torrent, "seed_idle_mode", "seedIdleMode");
            var seedIdleLimit = GetInt32(torrent, "seed_idle_limit", "seedIdleLimit");
            var secondsSeeding = GetInt64(torrent, "seconds_seeding", "secondsSeeding");
            var contentPath = !string.IsNullOrEmpty(downloadDir) && !string.IsNullOrEmpty(name)
                ? FileUtils.CombineWithOptionalBase(downloadDir, name)
                : downloadDir;
            var primaryLabel = ExtractLabels(torrent).FirstOrDefault() ?? string.Empty;
            TimeSpan? remainingTime = eta >= 0 ? TimeSpan.FromSeconds(eta) : null;
            var downloadId = !string.IsNullOrEmpty(hash) ? hash.ToUpperInvariant() : numericId.ToString(CultureInfo.InvariantCulture);
            var removeCompletedDownloads = client.Settings?.TryGetValue("removeCompletedDownloads", out var removeVal) is true &&
                removeVal is bool boolVal && boolVal;
            var isStopped = statusCode == 0;
            var isSeeding = statusCode == 6;
            var seedLimitReached = TransmissionSeedLimitEvaluator.HasReachedSeedLimit(
                isStopped,
                isSeeding,
                uploadRatio,
                seedRatioMode,
                seedRatioLimit,
                seedIdleMode,
                seedIdleLimit,
                secondsSeeding,
                sessionConfig.SeedRatioLimited,
                sessionConfig.SeedRatioLimit,
                sessionConfig.IdleSeedingLimitEnabled,
                sessionConfig.IdleSeedingLimit);
            var canBeRemoved = removeCompletedDownloads && seedLimitReached;

            return new DownloadClientItem
            {
                DownloadId = downloadId,
                Title = name,
                Category = primaryLabel,
                Status = MapDownloadItemStatus(statusCode, percentDone),
                TotalSize = totalSize,
                RemainingSize = leftUntilDone,
                RemainingTime = remainingTime,
                SeedRatio = uploadRatio,
                OutputPath = contentPath,
                Message = $"Status code: {statusCode}",
                Progress = percentDone,
                DownloadSpeed = rateDownload,
                CanBeRemoved = canBeRemoved,
                CanMoveFiles = canBeRemoved && isStopped,
                DownloadClientInfo = DownloadClientItemClientInfo.FromClient(
                    clientId: client.Id,
                    clientName: client.Name,
                    clientType: "transmission",
                    protocol: DownloadProtocol.Torrent,
                    removeCompletedDownloads: removeCompletedDownloads,
                    hasPostImportCategory: false)
            };
        }

        public static string MapQueueStatus(int statusCode, double percentDone)
        {
            var status = statusCode switch
            {
                0 => "paused",
                1 => "queued",
                2 => "downloading",
                3 => "queued",
                4 => "downloading",
                5 => "queued",
                6 => "seeding",
                7 => "failed",
                _ => "unknown"
            };

            return percentDone >= 100.0 && status is "seeding" or "queued" or "paused"
                ? "completed"
                : status;
        }

        public static DownloadItemStatus MapDownloadItemStatus(int statusCode, double percentDone)
        {
            if (percentDone >= 100.0 && statusCode is 0 or 3 or 5 or 6)
            {
                return DownloadItemStatus.Completed;
            }

            return statusCode switch
            {
                0 => DownloadItemStatus.Paused,
                1 => DownloadItemStatus.Queued,
                2 => DownloadItemStatus.Downloading,
                3 => DownloadItemStatus.Queued,
                4 => DownloadItemStatus.Downloading,
                5 => DownloadItemStatus.Queued,
                6 => DownloadItemStatus.Downloading,
                _ => DownloadItemStatus.Warning
            };
        }

        public static List<string> ExtractLabels(JsonElement torrent)
        {
            var labels = new List<string>();
            if (!torrent.TryGetProperty("labels", out var labelsProp) || labelsProp.ValueKind != JsonValueKind.Array)
            {
                return labels;
            }

            foreach (var label in labelsProp.EnumerateArray())
            {
                if (label.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = label.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    labels.Add(value.Trim());
                }
            }

            return labels;
        }

        private static string GetString(JsonElement value, string snakeCaseName, string? camelCaseName = null)
        {
            return TryGetProperty(value, snakeCaseName, camelCaseName, out var property)
                ? property.GetString() ?? string.Empty
                : string.Empty;
        }

        private static int GetInt32(JsonElement value, string snakeCaseName, string camelCaseName)
        {
            return TryGetProperty(value, snakeCaseName, camelCaseName, out var property)
                ? property.GetInt32()
                : 0;
        }

        private static long GetInt64(JsonElement value, string snakeCaseName, string camelCaseName)
        {
            return TryGetProperty(value, snakeCaseName, camelCaseName, out var property)
                ? property.GetInt64()
                : 0;
        }

        private static double GetDouble(JsonElement value, string snakeCaseName, string? camelCaseName = null)
        {
            return TryGetProperty(value, snakeCaseName, camelCaseName, out var property)
                ? property.GetDouble()
                : 0d;
        }

        private static bool TryGetProperty(JsonElement value, string snakeCaseName, string? camelCaseName, out JsonElement property)
        {
            if (value.TryGetProperty(snakeCaseName, out property))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(camelCaseName) && value.TryGetProperty(camelCaseName, out property))
            {
                return true;
            }

            property = default;
            return false;
        }
    }
}
