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
using System.Xml.Linq;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.DownloadClients.Nzbget;

internal static class NzbgetResponseMapper
{
    public static DownloadClientItem MapGroupToDownloadClientItem(
        DownloadClientConfiguration client,
        XElement structElement)
    {
        var members = ReadMembers(structElement);

        var id = members.GetValueOrDefault("GroupID", null)
            ?? members.GetValueOrDefault("LastID", null)
            ?? Guid.NewGuid().ToString("N");

        var title = members.GetValueOrDefault("NZBName", string.Empty);
        var statusRaw = members.GetValueOrDefault("Status", string.Empty);
        var category = members.GetValueOrDefault("Category", string.Empty);
        var sizeMb = ParseDouble(members.GetValueOrDefault("FileSizeMB", "0"));
        var remainingMb = ParseDouble(members.GetValueOrDefault("RemainingSizeMB", "0"));
        var downloadRate = ParseDouble(members.GetValueOrDefault("DownloadRate", "0"));
        var destDir = members.GetValueOrDefault("DestDir", string.Empty);

        var sizeBytes = Convert.ToInt64(Math.Max(0, sizeMb) * 1024 * 1024);
        var remainingBytes = Convert.ToInt64(Math.Max(0, remainingMb) * 1024 * 1024);

        TimeSpan? remainingTime = null;
        if (downloadRate > 0 && remainingMb > 0)
        {
            var remainingBytesExact = remainingMb * 1024 * 1024;
            var etaSeconds = (int)Math.Max(0, remainingBytesExact / downloadRate);
            remainingTime = TimeSpan.FromSeconds(etaSeconds);
        }

        var normalizedStatus = (statusRaw ?? "QUEUED").ToUpperInvariant();
        var status = normalizedStatus switch
        {
            "QUEUED" => DownloadItemStatus.Queued,
            "DOWNLOADING" => DownloadItemStatus.Downloading,
            "PAUSED" => DownloadItemStatus.Paused,
            "FETCHING" => DownloadItemStatus.Downloading,
            "SCANNING" => DownloadItemStatus.Downloading,
            "PP_QUEUED" => DownloadItemStatus.Downloading,
            "PP_PROCESSING" => DownloadItemStatus.Downloading,
            _ when normalizedStatus.StartsWith("SUCCESS", StringComparison.Ordinal) => DownloadItemStatus.Completed,
            _ when normalizedStatus.StartsWith("FAILURE", StringComparison.Ordinal) || normalizedStatus.StartsWith("FAILED", StringComparison.Ordinal) => DownloadItemStatus.Failed,
            _ => DownloadItemStatus.Queued
        };

        var contentPath = !string.IsNullOrEmpty(destDir) && !string.IsNullOrEmpty(title)
            ? FileUtils.CombineWithOptionalBase(destDir, title)
            : (destDir ?? string.Empty);

        var progress = sizeMb > 0 ? Math.Clamp((sizeMb - remainingMb) / sizeMb * 100, 0, 100) : 0;

        return new DownloadClientItem
        {
            DownloadId = id.ToUpperInvariant(),
            Title = title ?? string.Empty,
            Category = category ?? string.Empty,
            Status = status,
            TotalSize = sizeBytes,
            RemainingSize = remainingBytes,
            RemainingTime = remainingTime,
            OutputPath = contentPath ?? string.Empty,
            Message = statusRaw ?? "QUEUED",
            Progress = progress,
            DownloadSpeed = downloadRate,
            CanBeRemoved = true,
            CanMoveFiles = status == DownloadItemStatus.Completed,
            DownloadClientInfo = DownloadClientItemClientInfo.FromClient(
                clientId: client.Id,
                clientName: client.Name,
                clientType: "nzbget",
                protocol: DownloadProtocol.Usenet,
                removeCompletedDownloads: client.Settings?.TryGetValue("removeCompletedDownloads", out var removeVal) is true &&
                                         (removeVal is bool boolVal && boolVal),
                hasPostImportCategory: !string.IsNullOrEmpty(client.Settings?.GetValueOrDefault("postImportCategory")?.ToString()))
        };
    }

    public static QueueItem MapGroup(DownloadClientConfiguration client, XElement structElement)
    {
        var members = ReadMembers(structElement);

        var id = members.GetValueOrDefault("GroupID", null)
            ?? members.GetValueOrDefault("LastID", null)
            ?? Guid.NewGuid().ToString("N");

        var title = members.GetValueOrDefault("NZBName", string.Empty);
        var statusRaw = members.GetValueOrDefault("Status", string.Empty);
        var category = members.GetValueOrDefault("Category", string.Empty);
        var sizeMb = ParseDouble(members.GetValueOrDefault("FileSizeMB", "0"));
        var remainingMb = ParseDouble(members.GetValueOrDefault("RemainingSizeMB", "0"));
        var downloadedMb = sizeMb - remainingMb;
        var downloadRate = ParseDouble(members.GetValueOrDefault("DownloadRate", "0"));
        var destDir = members.GetValueOrDefault("DestDir", string.Empty);

        var sizeBytes = Convert.ToInt64(Math.Max(0, sizeMb) * 1024 * 1024);
        var downloadedBytes = Convert.ToInt64(Math.Max(0, downloadedMb) * 1024 * 1024);

        int? etaSeconds = null;
        if (downloadRate > 0 && remainingMb > 0)
        {
            var remainingBytes = remainingMb * 1024 * 1024;
            etaSeconds = (int)Math.Max(0, remainingBytes / downloadRate);
        }

        var normalizedStatus = (statusRaw ?? "QUEUED").ToUpperInvariant();
        string status = normalizedStatus switch
        {
            "QUEUED" => "queued",
            "DOWNLOADING" => "downloading",
            "PAUSED" => "paused",
            "FETCHING" => "downloading",
            "SCANNING" => "downloading",
            "PP_QUEUED" => "downloading",
            "PP_PROCESSING" => "downloading",
            _ when normalizedStatus.StartsWith("SUCCESS", StringComparison.Ordinal) => "completed",
            _ when normalizedStatus.StartsWith("FAILURE", StringComparison.Ordinal) || normalizedStatus.StartsWith("FAILED", StringComparison.Ordinal) => "failed",
            _ => "queued"
        };

        var contentPath = !string.IsNullOrEmpty(destDir) && !string.IsNullOrEmpty(title)
            ? FileUtils.CombineWithOptionalBase(destDir, title)
            : destDir;

        return new QueueItem
        {
            Id = id,
            Title = title ?? string.Empty,
            Quality = category ?? string.Empty,
            Status = status,
            Progress = sizeMb > 0 ? Math.Clamp(downloadedMb / sizeMb * 100, 0, 100) : 0,
            Size = sizeBytes,
            Downloaded = downloadedBytes,
            DownloadSpeed = downloadRate,
            Eta = etaSeconds > 0 ? etaSeconds : null,
            DownloadClient = client.Name ?? client.Id ?? "NZBGet",
            DownloadClientId = client.Id ?? string.Empty,
            DownloadClientType = "nzbget",
            AddedAt = DateTime.UtcNow,
            CanPause = status is "downloading" or "queued",
            CanRemove = true,
            RemotePath = destDir,
            LocalPath = destDir,
            ContentPath = contentPath
        };
    }

    public static QueueItem MapHistoryToQueueItem(
        DownloadClientConfiguration client,
        NzbgetHistoryEntry entry)
    {
        var isCompleted = entry.Outcome == NzbgetHistoryOutcome.Completed;
        var downloadedBytes = isCompleted
            ? entry.TotalSizeBytes
            : entry.DownloadedSizeBytes;
        var progress = isCompleted
            ? 100
            : entry.TotalSizeBytes > 0
                ? Math.Clamp(
                    downloadedBytes * 100d / entry.TotalSizeBytes,
                    0,
                    100)
                : 0;
        var completedPath = isCompleted
            ? entry.CompletedPath
            : string.Empty;

        return new QueueItem
        {
            Id = entry.CanonicalNzbId,
            Title = entry.Title,
            Quality = entry.Category,
            Status = isCompleted ? "completed" : "failed",
            Progress = progress,
            Size = entry.TotalSizeBytes,
            Downloaded = downloadedBytes,
            DownloadSpeed = 0,
            Eta = null,
            DownloadClient = client.Name ?? client.Id ?? "NZBGet",
            DownloadClientId = client.Id ?? string.Empty,
            DownloadClientType = "nzbget",
            AddedAt = DateTime.UtcNow,
            ErrorMessage = isCompleted ? null : entry.RawStatus,
            CanPause = false,
            CanRemove = true,
            RemotePath = completedPath,
            LocalPath = completedPath,
            ContentPath = completedPath
        };
    }

    public static DownloadClientItem MapHistoryToDownloadClientItem(
        DownloadClientConfiguration client,
        NzbgetHistoryEntry entry)
    {
        var isCompleted = entry.Outcome == NzbgetHistoryOutcome.Completed;
        var downloadedBytes = isCompleted
            ? entry.TotalSizeBytes
            : entry.DownloadedSizeBytes;
        var remainingBytes = isCompleted
            ? 0
            : Math.Max(0, entry.TotalSizeBytes - downloadedBytes);
        var progress = isCompleted
            ? 100
            : entry.TotalSizeBytes > 0
                ? Math.Clamp(
                    downloadedBytes * 100d / entry.TotalSizeBytes,
                    0,
                    100)
                : 0;

        return new DownloadClientItem
        {
            DownloadId = entry.CanonicalNzbId.ToUpperInvariant(),
            Title = entry.Title,
            Category = entry.Category,
            Status = isCompleted
                ? DownloadItemStatus.Completed
                : DownloadItemStatus.Failed,
            TotalSize = entry.TotalSizeBytes,
            RemainingSize = remainingBytes,
            RemainingTime = null,
            OutputPath = isCompleted ? entry.CompletedPath : string.Empty,
            Message = entry.RawStatus,
            Progress = progress,
            DownloadSpeed = 0,
            CanBeRemoved = true,
            CanMoveFiles = isCompleted,
            DownloadClientInfo = DownloadClientItemClientInfo.FromClient(
                clientId: client.Id,
                clientName: client.Name,
                clientType: "nzbget",
                protocol: DownloadProtocol.Usenet,
                removeCompletedDownloads: client.Settings?.TryGetValue("removeCompletedDownloads", out var removeVal) is true &&
                                         (removeVal is bool boolVal && boolVal),
                hasPostImportCategory: !string.IsNullOrEmpty(client.Settings?.GetValueOrDefault("postImportCategory")?.ToString()))
        };
    }

    private static IReadOnlyDictionary<string, string?> ReadMembers(XElement structElement)
    {
        var members = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var member in structElement.Elements("member"))
        {
            members[member.Element("name")?.Value ?? string.Empty] =
                member.Element("value")?.Elements().FirstOrDefault()?.Value ?? string.Empty;
        }

        return members;
    }

    private static double ParseDouble(string? value)
    {
        return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0d;
    }
}
