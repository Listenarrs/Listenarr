// Listenarr - Audiobook Management System
// Copyright (C) 2024-2026 Listenarr Contributors

namespace Listenarr.Infrastructure.Downloads.DirectDownload;

public sealed class DirectDownloadImportSourceResolver : IDirectDownloadImportSourceResolver
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
            DownloadClientId = "DDL",
            DownloadClientType = "ddl",
            LocalPath = download.DownloadPath,
            ContentPath = download.DownloadPath,
            SourceFiles = sourceFiles
        };
    }
}
