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
    /// Interface for AudiobookDbService to allow easier testing and DI.
    /// </summary>
    public interface IAudiobookDbService
    {
        /// <summary>
        /// Resolve a known Audible ASIN to its AudiobookDB Book record, including every known
        /// Release of that book (each a distinct recording with its own narrator/publisher).
        /// Used to surface alternate narrated editions of a book the user already matched via
        /// Audible, e.g. a self-published original narration later superseded by an official
        /// re-recording that Audible's own catalog no longer carries.
        /// </summary>
        Task<AudiobookDbBook?> GetBookByAudibleAsinAsync(string asin);
    }

    public sealed record AudiobookDbBook(
        string Id,
        string? Title,
        List<AudiobookDbRelease> Releases);

    public sealed record AudiobookDbRelease(
        string Id,
        string? Title,
        string? ReleaseDate,
        string? Duration,
        string? Disambiguation,
        string? PublisherName,
        List<string> Narrators);
}
