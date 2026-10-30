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

namespace Listenarr.Application.Search.Contracts
{
    /// <summary>
    /// Interface for AudiobookshelfService to allow easier testing and DI.
    /// </summary>
    public interface IAudiobookshelfService
    {
        /// <summary>
        /// Whether an Audiobookshelf base URL and API key are currently configured.
        /// </summary>
        Task<bool> IsConfiguredAsync();

        /// <summary>
        /// Fetch every book-type library item across every Audiobookshelf library, with the
        /// metadata Audiobookshelf has already matched/scanned for each (including narrator,
        /// since Audiobookshelf reads that from the same embedded file tags, or its own
        /// metadata providers, that Listenarr itself did not previously consult).
        /// Returns an empty list if Audiobookshelf isn't configured or is unreachable.
        /// </summary>
        Task<List<AudiobookshelfLibraryItem>> GetLibraryItemsAsync(CancellationToken ct = default);
    }

    public sealed record AudiobookshelfLibraryItem(
        string Id,
        string? Title,
        string? AuthorName,
        string? NarratorName,
        string? Asin,
        string? Isbn);
}
