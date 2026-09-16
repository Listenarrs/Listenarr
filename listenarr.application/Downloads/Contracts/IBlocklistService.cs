/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */


namespace Listenarr.Application.Downloads.Contracts
{
    public interface IBlocklistService
    {
        /// <summary>Record that a release failed for a book and should not be grabbed for it again.</summary>
        Task BlockAsync(int audiobookId, string releaseIdentifier, string title, long? size, string reason);

        /// <summary>The release identifiers already blocked for one book.</summary>
        Task<IReadOnlyCollection<string>> GetBlockedIdentifiersAsync(int audiobookId);

        Task<IReadOnlyList<BlockedRelease>> GetForAudiobookAsync(int audiobookId);

        /// <summary>
        /// Remove one entry, so a release blocked by a failure that has since been fixed can be
        /// grabbed again. False when no entry with that id exists.
        /// </summary>
        Task<bool> DeleteAsync(int id);

        /// <summary>
        /// Remove every entry for one book and answer how many rows went. This is the way out of
        /// the case the per-entry delete is awkward for: a book that failed repeatedly while a
        /// download client was misconfigured has an entry per release, and the operator wants all
        /// of them gone at once rather than one at a time.
        /// </summary>
        Task<int> ClearForAudiobookAsync(int audiobookId);
    }
}
