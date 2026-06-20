/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */


namespace Listenarr.Infrastructure.DownloadClients.Qbittorrent;

internal static class QbittorrentTorrentAddPlanner
{
    public static QbittorrentTorrentAddPlan Create(
        DownloadClientConfiguration client,
        PreparedTorrentSubmission submission)
    {
        var category = client.Settings?.TryGetValue("category", out var categoryValue) is true
            ? categoryValue?.ToString()
            : null;
        var tags = client.Settings?.TryGetValue("tags", out var tagsValue) is true
            ? tagsValue?.ToString()
            : null;

        return new QbittorrentTorrentAddPlan(
            submission.InfoHash,
            client.DownloadPath ?? string.Empty,
            category,
            tags,
            submission.TorrentBytes,
            submission.MagnetUri,
            submission.FileName);
    }
}

internal sealed record QbittorrentTorrentAddPlan(
    string Hash,
    string SavePath,
    string? Category,
    string? Tags,
    byte[]? TorrentFileData,
    string? MagnetLink,
    string? FileName);
