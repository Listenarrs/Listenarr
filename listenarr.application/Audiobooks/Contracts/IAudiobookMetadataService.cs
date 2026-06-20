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
    /// Provides audiobook metadata lookup across configured providers (Audible, Audnexus, etc.).
    /// </summary>
    public interface IAudiobookMetadataService
    {
        /// <summary>
        /// Gets audiobook metadata from configured providers in priority order.
        /// </summary>
        /// <param name="asin">ASIN identifier.</param>
        /// <param name="region">Region code (default: us).</param>
        /// <param name="cache">Whether provider caching should be used.</param>
        /// <returns>Metadata payload (provider-specific shape) or null when unavailable.</returns>
        Task<object?> GetMetadataAsync(string asin, string region = "us", bool cache = true);

        /// <summary>
        /// Gets audiobook metadata directly from Audible.
        /// </summary>
        /// <param name="asin">ASIN identifier.</param>
        /// <param name="region">Region code (default: us).</param>
        /// <param name="cache">Whether provider caching should be used.</param>
        /// <returns>Audible metadata or null when unavailable.</returns>
        Task<AudibleBookResponse?> GetAudibleMetadataAsync(string asin, string region = "us", bool cache = true);
    }
}
