/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Domain.Downloads;

namespace Listenarr.Application.Downloads.Contracts
{
    public interface IBlocklistService
    {
        /// <summary>Record that a release failed for a book and should not be grabbed for it again.</summary>
        Task BlockAsync(int audiobookId, string releaseIdentifier, string title, long? size, string reason);

        /// <summary>The release identifiers already blocked for one book.</summary>
        Task<IReadOnlyCollection<string>> GetBlockedIdentifiersAsync(int audiobookId);

        Task<IReadOnlyList<BlockedRelease>> GetForAudiobookAsync(int audiobookId);
    }
}
