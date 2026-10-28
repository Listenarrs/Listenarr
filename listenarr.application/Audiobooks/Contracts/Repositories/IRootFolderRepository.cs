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

using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Contracts.Repositories
{
    public interface IRootFolderRepository
    {
        Task<List<RootFolder>> GetAllAsync();
        Task<RootFolder?> GetByIdAsync(int id);
        Task<RootFolder?> GetByPathAsync(string path);
        Task AddAsync(RootFolder root);
        Task AddAndSetDefaultAsync(
            RootFolder root,
            int? expectedCurrentDefaultId,
            CancellationToken ct = default);
        Task UpdateAsync(RootFolder root);
        Task UpdateAndSetDefaultAsync(
            RootFolder root,
            int? expectedCurrentDefaultId,
            CancellationToken ct = default);
        Task RemoveAsync(int id);
        Task<RootFolder?> GetDefaultAsync();
        Task ClearDefaultExceptAsync(int? excludeId, CancellationToken ct = default);
        Task<bool> HasAudiobooksUnderPathAsync(
            string rootPath,
            FileSystemPathSemantics semantics,
            CancellationToken ct = default);
        Task<List<Audiobook>> GetAudiobooksUnderPathAsync(
            string rootPath,
            FileSystemPathSemantics semantics,
            CancellationToken ct = default);
        Task<List<int>> GetAllAudiobookIdsAsync(CancellationToken ct = default);
        Task<bool> HasNonRemovedDirectoryOwnershipAsync(
            int rootFolderId,
            CancellationToken ct = default);
        Task ReassignAudiobooksAndRemoveAsync(
            int sourceRootId,
            int targetRootId,
            FileSystemPathSemantics sourceSemantics,
            FileSystemPathSemantics targetSemantics,
            CancellationToken ct = default);
        Task SaveChangesAsync(CancellationToken ct = default);
    }
}
