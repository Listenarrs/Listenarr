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
        var normalizedStatus = (statusRaw ?? "QUEUED").ToUpperInvariant();
        var isTerminalLikeActiveStatus = IsTerminalLikeActiveStatus(normalizedStatus);

        var sizeBytes = Convert.ToInt64(Math.Max(0, sizeMb) * 1024 * 1024);
        var remainingBytes = Convert.ToInt64(Math.Max(0, remainingMb) * 1024 * 1024);
        if (isTerminalLikeActiveStatus && sizeBytes > 0 && remainingBytes == 0)
        {
            // Active terminal-looking rows are not import-ready by themselves.
            // Keep one byte remaining so normalized active telemetry cannot look
            // indistinguishable from completed history before FinalDir/DestDir exists.
            remainingBytes = 1;
        }

        TimeSpan? remainingTime = null;
        if (downloadRate > 0 && remainingMb > 0)
        {
            var remainingBytesExact = remainingMb * 1024 * 1024;
            var etaSeconds = (int)Math.Max(0, remainingBytesExact / downloadRate);
            remainingTime = TimeSpan.FromSeconds(etaSeconds);
        }

        var status = normalizedStatus switch
        {
            "QUEUED" => DownloadItemStatus.Queued,
            "PAUSED" => DownloadItemStatus.Paused,
            "DOWNLOADING" => DownloadItemStatus.Downloading,
            "FETCHING" => DownloadItemStatus.Downloading,
            "SCANNING" => DownloadItemStatus.Downloading,
            "PP_QUEUED" => DownloadItemStatus.Downloading,
            "PP_PROCESSING" => DownloadItemStatus.Downloading,
            _ when IsTerminalLikeActiveStatus(normalizedStatus) => DownloadItemStatus.Downloading,
            _ => DownloadItemStatus.Queued
        };

        var progress = CalculateActiveProgress(sizeMb, remainingMb, isTerminalLikeActiveStatus);

        // Active NZBGet groups are progress telemetry only. They can briefly
        // report terminal-looking statuses before history exposes the final
        // result and path. Do not let active telemetry drive completed/failed
        // transitions or import paths; completed history owns FinalDir/DestDir.

        return new DownloadClientItem
        {
            DownloadId = id.ToUpperInvariant(),
            Title = title ?? string.Empty,
            Category = category ?? string.Empty,
            Status = status,
            TotalSize = sizeBytes,
            RemainingSize = remainingBytes,
            RemainingTime = remainingTime,
            OutputPath = string.Empty,
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
        var normalizedStatus = (statusRaw ?? "QUEUED").ToUpperInvariant();
        var isTerminalLikeActiveStatus = IsTerminalLikeActiveStatus(normalizedStatus);

        var sizeBytes = Convert.ToInt64(Math.Max(0, sizeMb) * 1024 * 1024);
        var downloadedBytes = Convert.ToInt64(Math.Max(0, downloadedMb) * 1024 * 1024);
        if (isTerminalLikeActiveStatus && sizeBytes > 0 && downloadedBytes >= sizeBytes)
        {
            // Active terminal-looking rows do not carry the final import path.
            // Avoid making generic monitor conversion complete the download from
            // bytes alone before the history row supplies FinalDir/DestDir.
            downloadedBytes = sizeBytes - 1;
        }

        int? etaSeconds = null;
        if (downloadRate > 0 && remainingMb > 0)
        {
            var remainingBytes = remainingMb * 1024 * 1024;
            etaSeconds = (int)Math.Max(0, remainingBytes / downloadRate);
        }

        string status = normalizedStatus switch
        {
            "QUEUED" => "queued",
            "PAUSED" => "paused",
            "DOWNLOADING" => "downloading",
            "FETCHING" => "downloading",
            "SCANNING" => "downloading",
            "PP_QUEUED" => "downloading",
            "PP_PROCESSING" => "downloading",
            _ when IsTerminalLikeActiveStatus(normalizedStatus) => "downloading",
            _ => "queued"
        };

        var remotePath = string.IsNullOrWhiteSpace(destDir) ? null : destDir;

        // Active NZBGet groups do not reliably expose the final import path or
        // final state. Keep DestDir as queue telemetry, but leave import paths
        // empty until completed history provides FinalDir or DestDir.
        return new QueueItem
        {
            Id = id,
            Title = title ?? string.Empty,
            Quality = category ?? string.Empty,
            Status = status,
            Progress = CalculateActiveProgress(sizeMb, remainingMb, isTerminalLikeActiveStatus),
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
            RemotePath = remotePath,
            LocalPath = remotePath,
            ContentPath = null,
            SourceFiles = []
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
        var completedPath = isCompleted && !string.IsNullOrWhiteSpace(entry.CompletedPath)
            ? entry.CompletedPath
            : null;
        var diagnosticPath = isCompleted
            ? null
            : GetDiagnosticPath(entry);

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
            ErrorMessage = isCompleted ? null : NzbgetFailureMessageMapper.Map(entry),
            ClientFailureReason = isCompleted ? null : entry.RawStatus,
            CanPause = false,
            CanRemove = true,
            RemotePath = isCompleted ? completedPath : diagnosticPath,
            LocalPath = isCompleted ? completedPath : diagnosticPath,
            ContentPath = completedPath,
            SourceFiles = isCompleted ? null : []
        };
    }

    public static long? MapStatusDownloadRate(XElement valueElement)
    {
        var structElement = valueElement.Element("struct");
        if (structElement == null)
        {
            return null;
        }

        var members = ReadMembers(structElement);
        var splitRate = ParseLoHiLong(members, "DownloadRateLo", "DownloadRateHi");
        if (splitRate.HasValue && splitRate.Value > 0)
        {
            return splitRate.Value;
        }

        var legacyRate = ParseLong(members.GetValueOrDefault("DownloadRate"));
        return legacyRate > 0 ? legacyRate : null;
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
        var outputPath = isCompleted && !string.IsNullOrWhiteSpace(entry.CompletedPath)
            ? entry.CompletedPath
            : string.Empty;

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
            OutputPath = outputPath,
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

    private static long ParseLong(string? value)
    {
        return long.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0L;
    }

    private static double CalculateActiveProgress(
        double sizeMb,
        double remainingMb,
        bool isTerminalLikeActiveStatus)
    {
        if (sizeMb <= 0)
        {
            return 0;
        }

        var progress = Math.Clamp((sizeMb - remainingMb) / sizeMb * 100, 0, 100);
        return isTerminalLikeActiveStatus && progress >= 100
            ? 99.9
            : progress;
    }

    private static bool IsTerminalLikeActiveStatus(string normalizedStatus)
    {
        return normalizedStatus.StartsWith("SUCCESS", StringComparison.Ordinal) ||
            normalizedStatus.StartsWith("FAILURE", StringComparison.Ordinal) ||
            normalizedStatus.StartsWith("FAILED", StringComparison.Ordinal);
    }

    private static string? GetDiagnosticPath(NzbgetHistoryEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.FinalDir))
        {
            return entry.FinalDir;
        }

        return !string.IsNullOrWhiteSpace(entry.DestDir)
            ? entry.DestDir
            : null;
    }

    private static long? ParseLoHiLong(
        IReadOnlyDictionary<string, string?> members,
        string lowName,
        string highName)
    {
        if (!members.TryGetValue(lowName, out var lowValue) ||
            !members.TryGetValue(highName, out var highValue) ||
            !long.TryParse(lowValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var low) ||
            !long.TryParse(highValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var high))
        {
            return null;
        }

        var combined = ((ulong)(uint)high << 32) | (uint)low;
        return combined > long.MaxValue ? long.MaxValue : (long)combined;
    }
}
