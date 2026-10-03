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

        var initialState = Setting(client, "initialState");

        return new QbittorrentTorrentAddPlan(
            submission.InfoHash,
            client.DownloadPath ?? string.Empty,
            category,
            tags,
            submission.TorrentBytes,
            submission.MagnetUri,
            submission.FileName,
            AddPaused: string.Equals(initialState, "pause", StringComparison.OrdinalIgnoreCase),
            ForceStart: string.Equals(initialState, "forceStart", StringComparison.OrdinalIgnoreCase),
            SequentialDownload: Flag(client, "sequentialOrder"),
            FirstLastPiecePriority: Flag(client, "firstAndLastFirst"),
            ContentLayout: ResolveContentLayout(Setting(client, "contentLayout")));
    }

    private static string? Setting(DownloadClientConfiguration client, string key)
        => client.Settings?.TryGetValue(key, out var value) is true ? value?.ToString() : null;

    private static bool Flag(DownloadClientConfiguration client, string key)
    {
        var raw = Setting(client, key);
        return !string.IsNullOrWhiteSpace(raw) && bool.TryParse(raw, out var parsed) && parsed;
    }

    /// <summary>
    /// Map the stored value onto the spelling qBittorrent accepts.
    /// </summary>
    /// <remarks>
    /// contentLayout is case sensitive. Passing the stored lowercase value straight through is
    /// silently ignored and the torrent keeps its default layout, which is the same shape of
    /// failure as sending a value the API does not know at all. Verified against qBittorrent
    /// 5.2.3, Web API 2.15.1.
    /// </remarks>
    private static string? ResolveContentLayout(string? stored) => stored?.ToLowerInvariant() switch
    {
        "original" => "Original",
        "subfolder" => "Subfolder",
        "nosubfolder" => "NoSubfolder",
        _ => null
    };
}

internal sealed record QbittorrentTorrentAddPlan(
    string Hash,
    string SavePath,
    string? Category,
    string? Tags,
    byte[]? TorrentFileData,
    string? MagnetLink,
    string? FileName,
    bool AddPaused = false,
    bool ForceStart = false,
    bool SequentialDownload = false,
    bool FirstLastPiecePriority = false,
    string? ContentLayout = null);
