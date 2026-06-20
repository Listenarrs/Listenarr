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
using System.Globalization;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.DownloadClients.Sabnzbd;

internal static class SabnzbdResponseMapper
{
    public static QueueItem? MapQueueSlotToQueueItem(
        DownloadClientConfiguration client,
        JsonElement slot,
        string configuredCategory,
        double speed)
    {
        var nzoId = GetString(slot, "nzo_id");
        var filename = GetString(slot, "filename", "Unknown");
        var status = GetString(slot, "status", "Unknown");
        var category = GetString(slot, "cat");

        if (!DownloadClientCategoryFilter.Matches(configuredCategory, category))
            return null;

        var sizeMb = GetDouble(slot, "mb");
        var mbLeft = GetDouble(slot, "mbleft");
        var downloadedMb = sizeMb - mbLeft;
        var percentage = GetDouble(slot, "percentage");
        var timeLeft = GetString(slot, "timeleft", "0:00:00");
        var etaSeconds = !string.IsNullOrEmpty(timeLeft) && timeLeft != "0:00:00"
            ? ParseTimeLeft(timeLeft)
            : 0;

        var mappedStatus = MapQueueStatus(status);
        var remotePath = client.DownloadPath ?? "";
        var contentPath = !string.IsNullOrEmpty(remotePath) && !string.IsNullOrEmpty(filename)
            ? FileUtils.CombineWithOptionalBase(remotePath, filename)
            : remotePath;

        return new QueueItem
        {
            Id = nzoId,
            Title = filename,
            Quality = category,
            Status = mappedStatus,
            Progress = percentage,
            Size = (long)(sizeMb * 1024 * 1024),
            Downloaded = (long)(downloadedMb * 1024 * 1024),
            DownloadSpeed = speed,
            Eta = etaSeconds > 0 ? etaSeconds : null,
            DownloadClient = client.Name,
            DownloadClientId = client.Id,
            DownloadClientType = "sabnzbd",
            AddedAt = DateTime.UtcNow,
            CanPause = mappedStatus == "downloading" || mappedStatus == "queued",
            CanRemove = true,
            RemotePath = remotePath,
            LocalPath = remotePath,
            ContentPath = contentPath
        };
    }

    public static QueueItem? MapHistorySlotToQueueItem(
        DownloadClientConfiguration client,
        JsonElement slot,
        string configuredCategory,
        ISet<string> existingNzoIds)
    {
        var nzoId = GetString(slot, "nzo_id");
        if (string.IsNullOrEmpty(nzoId) || existingNzoIds.Contains(nzoId))
            return null;

        var histCategory = GetString(slot, "category");
        if (!DownloadClientCategoryFilter.Matches(configuredCategory, histCategory))
            return null;

        var histStatus = GetString(slot, "status");
        var mappedStatus = histStatus.ToLowerInvariant() switch
        {
            "completed" => "completed",
            "failed" => "failed",
            _ => "completed"
        };

        var histBytes = slot.TryGetProperty("bytes", out var hb) && hb.TryGetInt64(out var hbl) ? hbl : 0L;
        var storagePath = GetString(slot, "storage");
        var remotePath = !string.IsNullOrEmpty(storagePath) ? storagePath : (client.DownloadPath ?? "");
        DateTime? completedAt = null;
        if (slot.TryGetProperty("completed", out var compEpoch) && compEpoch.TryGetInt64(out var epoch))
        {
            completedAt = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
        }

        return new QueueItem
        {
            Id = nzoId,
            Title = GetString(slot, "name", "Unknown"),
            Quality = histCategory,
            Status = mappedStatus,
            Progress = mappedStatus == "completed" ? 100 : 0,
            Size = histBytes,
            Downloaded = histBytes,
            DownloadSpeed = 0,
            Eta = null,
            DownloadClient = client.Name,
            DownloadClientId = client.Id,
            DownloadClientType = "sabnzbd",
            AddedAt = completedAt ?? DateTime.UtcNow,
            CompletionTime = completedAt,
            CanPause = false,
            CanRemove = true,
            RemotePath = remotePath,
            LocalPath = remotePath,
            ContentPath = remotePath
        };
    }

    public static DownloadClientItem? MapQueueSlotToDownloadClientItem(
        DownloadClientConfiguration client,
        JsonElement slot,
        string configuredCategory,
        double queueSpeed)
    {
        var category = GetString(slot, "cat");
        if (!DownloadClientCategoryFilter.Matches(configuredCategory, category))
            return null;

        var nzoId = GetString(slot, "nzo_id");
        var filename = GetString(slot, "filename", "Unknown");
        var status = GetString(slot, "status", "Unknown");
        var sizeMb = GetDouble(slot, "mb");
        var mbLeft = GetDouble(slot, "mbleft");
        var percentage = GetDouble(slot, "percentage");
        var timeLeft = GetString(slot, "timeleft", "0:00:00");
        var etaSeconds = !string.IsNullOrEmpty(timeLeft) && timeLeft != "0:00:00"
            ? ParseTimeLeft(timeLeft)
            : 0;

        var mappedStatus = MapDownloadItemStatus(status);
        var remotePath = client.DownloadPath ?? "";
        var contentPath = !string.IsNullOrEmpty(remotePath) && !string.IsNullOrEmpty(filename)
            ? FileUtils.CombineWithOptionalBase(remotePath, filename)
            : remotePath;

        return new DownloadClientItem
        {
            DownloadId = nzoId.ToUpperInvariant(),
            Title = filename,
            Category = category,
            Status = mappedStatus,
            TotalSize = (long)(sizeMb * 1024 * 1024),
            RemainingSize = (long)(mbLeft * 1024 * 1024),
            RemainingTime = etaSeconds > 0 ? TimeSpan.FromSeconds(etaSeconds) : null,
            OutputPath = contentPath,
            Message = status,
            Progress = percentage,
            DownloadSpeed = queueSpeed,
            CanBeRemoved = true,
            CanMoveFiles = mappedStatus == DownloadItemStatus.Completed,
            DownloadClientInfo = DownloadClientItemClientInfo.FromClient(
                clientId: client.Id,
                clientName: client.Name,
                clientType: "sabnzbd",
                protocol: DownloadProtocol.Usenet,
                removeCompletedDownloads: client.Settings?.TryGetValue("removeCompletedDownloads", out var removeVal) is true &&
                                         (removeVal is bool boolVal && boolVal),
                hasPostImportCategory: !string.IsNullOrEmpty(client.Settings?.GetValueOrDefault("postImportCategory")?.ToString()))
        };
    }

    public static double ParseSpeed(string speedStr)
    {
        if (string.IsNullOrWhiteSpace(speedStr)) return 0;

        speedStr = speedStr.Trim().ToLowerInvariant();
        var parts = speedStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return 0;

        if (!double.TryParse(parts[0], out var value)) return 0;

        if (parts.Length > 1)
        {
            var unit = parts[1];
            if (unit.StartsWith("k")) return value * 1024;
            if (unit.StartsWith("m")) return value * 1024 * 1024;
            if (unit.StartsWith("g")) return value * 1024 * 1024 * 1024;
        }

        return value;
    }

    public static double ParseJsonDouble(JsonElement element)
    {
        try
        {
            if (element.ValueKind == JsonValueKind.Number)
                return element.GetDouble();

            if (element.ValueKind == JsonValueKind.String)
            {
                var value = element.GetString();
                if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                    return parsed;
            }
        }
        catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
        {
            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
        }

        return 0.0;
    }

    private static int ParseTimeLeft(string timeLeft)
    {
        if (string.IsNullOrWhiteSpace(timeLeft)) return 0;

        var totalSeconds = 0;
        if (timeLeft.Contains("day", StringComparison.OrdinalIgnoreCase))
        {
            var partsWithDays = timeLeft.Split(new[] { " day ", " days " }, StringSplitOptions.None);
            if (partsWithDays.Length == 2 && int.TryParse(partsWithDays[0], out var days))
            {
                totalSeconds += days * 86400;
                timeLeft = partsWithDays[1];
            }
        }

        var parts = timeLeft.Split(':');
        if (parts.Length == 3)
        {
            if (int.TryParse(parts[0], out var hours) &&
                int.TryParse(parts[1], out var minutes) &&
                int.TryParse(parts[2], out var seconds))
            {
                return totalSeconds + hours * 3600 + minutes * 60 + seconds;
            }
        }
        else if (parts.Length == 2)
        {
            if (int.TryParse(parts[0], out var minutes) &&
                int.TryParse(parts[1], out var seconds))
            {
                return totalSeconds + minutes * 60 + seconds;
            }
        }

        return totalSeconds;
    }

    private static string MapQueueStatus(string status)
    {
        return status.ToLowerInvariant() switch
        {
            "downloading" => "downloading",
            "queued" => "queued",
            "paused" => "paused",
            "checking" => "downloading",
            "extracting" => "downloading",
            "moving" => "downloading",
            "completed" => "completed",
            "failed" => "failed",
            _ => "queued"
        };
    }

    private static DownloadItemStatus MapDownloadItemStatus(string status)
    {
        return status.ToLowerInvariant() switch
        {
            "downloading" => DownloadItemStatus.Downloading,
            "queued" => DownloadItemStatus.Queued,
            "paused" => DownloadItemStatus.Paused,
            "checking" => DownloadItemStatus.Downloading,
            "extracting" => DownloadItemStatus.Downloading,
            "moving" => DownloadItemStatus.Downloading,
            "completed" => DownloadItemStatus.Completed,
            "failed" => DownloadItemStatus.Failed,
            _ => DownloadItemStatus.Queued
        };
    }

    private static string GetString(JsonElement element, string propertyName, string defaultValue = "")
    {
        return element.TryGetProperty(propertyName, out var property)
            ? property.GetString() ?? defaultValue
            : defaultValue;
    }

    private static double GetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return 0;

        if (property.ValueKind == JsonValueKind.Number)
            return property.GetDouble();

        if (property.ValueKind == JsonValueKind.String && double.TryParse(property.GetString() ?? "0", out var value))
            return value;

        return 0;
    }
}
