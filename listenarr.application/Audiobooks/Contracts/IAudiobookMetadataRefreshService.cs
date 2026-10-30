/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */

namespace Listenarr.Application.Audiobooks.Contracts
{
    /// <summary>
    /// Fetches upstream metadata for an audiobook using its ASIN and fills in fields that are
    /// currently empty. Used to auto-populate a book immediately after a scan discovers an ASIN
    /// embedded in the file, so the user doesn't have to separately click "Rescan Metadata".
    /// Existing (non-empty) fields are never overwritten.
    /// </summary>
    public interface IAudiobookMetadataRefreshService
    {
        /// <summary>
        /// Populates missing metadata on <paramref name="audiobook"/> from its ASIN.
        /// Returns true if any field was filled in (and the audiobook was saved).
        /// </summary>
        Task<bool> TryPopulateMissingMetadataAsync(Audiobook audiobook, string? region = null, CancellationToken cancellationToken = default);
    }
}
