/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Domain.Downloads
{
    /// <summary>
    /// One release that failed for one book, and should not be grabbed for that book again.
    ///
    /// Scoped to the book rather than global on purpose: a release that is broken for the
    /// title it was grabbed for says nothing about the indexer, and nothing about the other
    /// releases of the same title. This mirrors how the *arr family scopes a blocklist
    /// entry to the media item it was rejected for.
    /// </summary>
    public class BlockedRelease
    {
        public int Id { get; set; }

        public int AudiobookId { get; set; }

        /// <summary>
        /// Stable identity for the release. A torrent info-hash where one is known, since
        /// that identifies the same release across indexers, otherwise a hash of the
        /// indexer URL the release came from. Never the download client's own id, which is
        /// per submission and would not match the same release on a later search.
        /// </summary>
        public string ReleaseIdentifier { get; set; } = string.Empty;

        /// <summary>Release title as it was seen, so a human reading the list can tell what it is.</summary>
        public string Title { get; set; } = string.Empty;

        public long? Size { get; set; }

        public DateTime BlockedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Why it was blocked. Not load-bearing for behaviour, only for explaining it later.</summary>
        public string Reason { get; set; } = string.Empty;
    }
}
