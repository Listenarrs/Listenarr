/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
namespace Listenarr.Domain.ActivityHistory
{
    public static class HistoryEvents
    {
        public const string Grabbed = "Grabbed";
        public const string Downloading = "Downloading";
        public const string DownloadCompleted = "DownloadCompleted";
        public const string DownloadFailed = "DownloadFailed";
        public const string ImportStarted = "ImportStarted";
        public const string Imported = "Imported";
        public const string ImportFailed = "ImportFailed";
        public const string ImportRetry = "ImportRetry";
        public const string CleanupRequested = "CleanupRequested";
        public const string CleanupSucceeded = "CleanupSucceeded";
        public const string CleanupFailed = "CleanupFailed";
        public const string ScanQueued = "ScanQueued";
        public const string ScanCompleted = "ScanCompleted";
        public const string ScanFailed = "ScanFailed";
        public const string FileMoved = "FileMoved";
        public const string FileCopied = "FileCopied";
        public const string FileDeleted = "FileDeleted";
        public const string FileSkipped = "FileSkipped";
        public const string Renamed = "Renamed";
        public const string LibraryUpdated = "LibraryUpdated";
        public const string LibraryDeleted = "LibraryDeleted";
        public const string Paused = "Paused";
        public const string Resumed = "Resumed";
        public const string Removed = "Removed";
        public const string Checking = "Checking";
        public const string Warning = "Warning";

        public static string FromDownloadEvent(DownloadHistoryEventType eventType) => eventType switch
        {
            DownloadHistoryEventType.Grabbed => Grabbed,
            DownloadHistoryEventType.Downloading => Downloading,
            DownloadHistoryEventType.DownloadCompleted => DownloadCompleted,
            DownloadHistoryEventType.DownloadFailed => DownloadFailed,
            DownloadHistoryEventType.Imported => Imported,
            DownloadHistoryEventType.ImportFailed => ImportFailed,
            DownloadHistoryEventType.Paused => Paused,
            DownloadHistoryEventType.Resumed => Resumed,
            DownloadHistoryEventType.Removed => Removed,
            DownloadHistoryEventType.Checking => Checking,
            DownloadHistoryEventType.Warning => Warning,
            _ => eventType.ToString()
        };

        public static DownloadHistoryEventType ToDownloadEvent(string eventType) => eventType switch
        {
            Grabbed => DownloadHistoryEventType.Grabbed,
            Downloading => DownloadHistoryEventType.Downloading,
            DownloadCompleted => DownloadHistoryEventType.DownloadCompleted,
            DownloadFailed => DownloadHistoryEventType.DownloadFailed,
            Imported => DownloadHistoryEventType.Imported,
            ImportFailed => DownloadHistoryEventType.ImportFailed,
            Paused => DownloadHistoryEventType.Paused,
            Resumed => DownloadHistoryEventType.Resumed,
            Removed => DownloadHistoryEventType.Removed,
            Checking => DownloadHistoryEventType.Checking,
            _ => DownloadHistoryEventType.Warning
        };
    }
}
