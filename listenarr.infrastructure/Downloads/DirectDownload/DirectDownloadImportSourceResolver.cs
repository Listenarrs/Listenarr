/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Infrastructure.Downloads.DirectDownload;

internal sealed class DirectDownloadImportSourceResolver : IDirectDownloadImportSourceResolver
{
    public QueueItem Resolve(Download download)
    {
        var sourceFiles = File.Exists(download.DownloadPath)
            ? new List<string> { download.DownloadPath }
            : Directory.Exists(download.DownloadPath)
                ? Directory.EnumerateFiles(download.DownloadPath, "*", SearchOption.AllDirectories).ToList()
                : [];

        return new QueueItem
        {
            Id = download.Id,
            Title = download.Title,
            Status = "completed",
            Progress = 100,
            Size = download.TotalSize,
            Downloaded = download.DownloadedSize,
            DownloadClient = "Direct Download",
            DownloadClientId = DirectDownloadMetadataKeys.ClientId,
            DownloadClientType = "ddl",
            LocalPath = download.DownloadPath,
            ContentPath = download.DownloadPath,
            SourceFiles = sourceFiles
        };
    }
}
