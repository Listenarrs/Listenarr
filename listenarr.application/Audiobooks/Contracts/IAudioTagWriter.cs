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
    public interface IAudioTagWriter
    {
        Task WriteAsinTagAsync(string filePath, string asin);

        Task WriteAsinTagAsync(
            IAudiobookFileRegistrationLease registrationLease,
            string asin);

        /// <summary>
        /// Write the ASIN and, when supplied, embed cover artwork, in a single open and save.
        /// Splitting these into two calls would rewrite the file twice, and an audiobook is
        /// commonly several gigabytes.
        /// </summary>
        Task WriteTagsAsync(
            IAudiobookFileRegistrationLease registrationLease,
            string? asin,
            AudioCoverArt? coverArt);
    }
}
