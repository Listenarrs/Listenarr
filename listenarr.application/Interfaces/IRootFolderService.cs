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
using Listenarr.Domain.Models;

namespace Listenarr.Application.Interfaces
{
    public interface IRootFolderService
    {
        Task<RootFolder?> GetDefaultAsync();
        Task<List<RootFolder>> GetAllAsync();
        Task<RootFolder?> GetByIdAsync(int id);
        Task<RootFolder> CreateAsync(RootFolder root);
        // moveFiles: when true, enqueue move jobs for affected audiobooks; when false, perform DB-only reassign
        Task<RootFolder> UpdateAsync(RootFolder root, bool moveFiles = false, bool deleteEmptySource = true);

        /// <summary>
        /// Removes an unused root folder
        /// </summary>
        /// <param name="id">ID of the root folder to remove</param>
        /// <param name="reassignRootId"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException">When other audiobooks still use this root folder</exception>
        Task DeleteAsync(int id, int? reassignRootId = null);
    }
}
