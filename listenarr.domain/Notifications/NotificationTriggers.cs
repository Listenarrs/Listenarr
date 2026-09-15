/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
namespace Listenarr.Domain.Notifications
{
    /// <summary>
    /// Canonical notification trigger names covering the book-acquisition lifecycle, plus a catalog
    /// (display name, description, ordering) so the backend is the single source of truth. The
    /// settings UI and any consumer read the catalog rather than hardcoding trigger lists.
    ///
    /// A trigger corresponds to a lifecycle transition that is already recorded in the activity
    /// history (see <see cref="ActivityHistory.HistoryEvents"/>); the notification is fired at the
    /// same point so external integrations (Discord, NTFY, custom scripts, …) can subscribe to any
    /// stage without Listenarr knowing what they do with it.
    /// </summary>
    public static class NotificationTriggers
    {
        public const string BookWanted = "book-wanted";
        public const string BookGrabbed = "book-grabbed";
        public const string BookDownloading = "book-downloading";
        public const string BookDownloadCompleted = "book-download-completed";
        public const string BookImported = "book-imported";
        public const string BookAvailable = "book-available";
        public const string BookCompleted = "book-completed";
        public const string BookDownloadFailed = "book-download-failed";
        public const string BookImportFailed = "book-import-failed";

        /// <summary>Any add to the library (monitored or not). Retained for back-compat.</summary>
        public const string BookAdded = "book-added";

        // Library-management lifecycle (beyond acquisition).
        public const string BookUpgraded = "book-upgraded";
        public const string BookDeleted = "book-deleted";
        public const string BookRenamed = "book-renamed";

        /// <summary>One catalog entry: the trigger id, a human label, a description, and lifecycle order.</summary>
        public sealed record Definition(string Id, string DisplayName, string Description, int Order);

        /// <summary>The full trigger catalog, ordered by lifecycle stage.</summary>
        public static readonly IReadOnlyList<Definition> Catalog = new List<Definition>
        {
            new(BookWanted, "Book Wanted", "A monitored book is wanted and eligible for acquisition.", 10),
            new(BookGrabbed, "Book Grabbed", "A release was accepted and sent to a download client.", 20),
            new(BookDownloading, "Downloading", "A download has started.", 30),
            new(BookDownloadCompleted, "Download Completed", "The download client finished downloading.", 40),
            new(BookImported, "Imported", "The downloaded file was imported into the library.", 50),
            new(BookAvailable, "Available", "The book's files are registered and available in the library.", 60),
            new(BookCompleted, "Completed", "Post-import processing (move/organize) finished.", 70),
            new(BookDownloadFailed, "Download Failed", "A download failed.", 80),
            new(BookImportFailed, "Import Failed", "An import failed.", 90),
            new(BookAdded, "Book Added", "A book was added to the library.", 100),
            new(BookUpgraded, "Book Upgraded", "An existing book's file was replaced by a higher-quality one.", 110),
            new(BookRenamed, "Book Renamed", "A book's files were renamed.", 120),
            new(BookDeleted, "Book Deleted", "A book or its files were removed from the library.", 130),
        };

        /// <summary>Trigger ids enabled by default on a fresh install (users may narrow this).</summary>
        public static readonly IReadOnlyList<string> DefaultEnabled =
            Catalog.OrderBy(d => d.Order).Select(d => d.Id).ToList();

        public static bool IsKnown(string trigger) =>
            Catalog.Any(d => string.Equals(d.Id, trigger, System.StringComparison.Ordinal));
    }
}
