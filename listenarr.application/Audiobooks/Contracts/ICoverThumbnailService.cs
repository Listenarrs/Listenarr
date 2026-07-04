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
    /// Produces (and caches) downscaled JPEG thumbnails of already-cached cover images,
    /// so grid/list views can serve small images instead of full-resolution originals.
    /// </summary>
    public interface ICoverThumbnailService
    {
        /// <summary>
        /// Returns the full path to a cached thumbnail of <paramref name="sourceFullPath"/> at the
        /// named <paramref name="sizeKey"/> (e.g. "grid", "grid2x"), generating it on demand.
        /// Returns <c>null</c> when the size is unknown, the source is missing, or generation fails —
        /// callers should fall back to serving the original.
        /// </summary>
        Task<string?> GetOrCreateThumbnailAsync(string sourceFullPath, string sizeKey, CancellationToken cancellationToken = default);
    }
}
