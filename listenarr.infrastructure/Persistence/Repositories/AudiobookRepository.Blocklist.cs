/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories
{
    /// <summary>
    /// The one child table of an audiobook that relational cascade does not reach.
    ///
    /// Its own file because AudiobookRepository.cs is a handful of lines under the 500-line cap
    /// that BackendArchitectureTests enforces, and this would have taken it over.
    /// </summary>
    public partial class AudiobookRepository
    {
        /// <summary>
        /// Queue the removal of a book's blocklist entries. The caller saves, so this lands in the
        /// same transaction as the delete of the book itself and a failure cannot leave the rows
        /// orphaned with the book already gone.
        ///
        /// BlockedReleases carries an AudiobookId but no navigation property and therefore no
        /// foreign key. Giving it one means a second migration on top of the one that created the
        /// table, and in SQLite adding a foreign key rebuilds the table. Readarr sweeps the same
        /// rows the same way, from BlocklistService.HandleAsync(AuthorDeletedEvent), rather than
        /// leaning on the database.
        ///
        /// Without this the rows outlive the book: the blocklist endpoints are keyed on a book that
        /// no longer exists, and a book re-added later takes a new id and never sees them.
        /// </summary>
        private async Task RemoveBlocklistEntriesFor(int audiobookId)
        {
            var blockedReleases = await _db.BlockedReleases
                .Where(entry => entry.AudiobookId == audiobookId)
                .ToListAsync();
            if (blockedReleases.Count > 0)
            {
                _db.BlockedReleases.RemoveRange(blockedReleases);
            }
        }
    }
}
