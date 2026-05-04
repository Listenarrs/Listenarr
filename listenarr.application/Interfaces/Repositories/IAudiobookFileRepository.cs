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

namespace Listenarr.Application.Interfaces.Repositories
{
    public interface IAudiobookFileRepository
    {
        Task<AudiobookFile?> GetByIdAsync(int id, CancellationToken ct = default);
        Task<List<AudiobookFile>> GetByAudiobookIdAsync(int audiobookId, CancellationToken ct = default);
        Task<List<AudiobookFile>> GetMissingMetadataAsync(int max, CancellationToken ct = default);
        Task<AudiobookFile> AddAsync(AudiobookFile file, CancellationToken ct = default);
        Task UpdateAsync(AudiobookFile file, CancellationToken ct = default);
        Task DeleteByAudiobookIdAsync(int audiobookId, CancellationToken ct = default);
        Task DeleteAsync(int id, CancellationToken ct = default);
        Task<bool> ExistsAtPathAsync(int audiobookId, string path, CancellationToken ct = default);
        Task<bool> IsPathUsedByOtherAsync(int audiobookId, string path, CancellationToken ct = default);
        Task<List<string>> GetAllFilePathsAsync(CancellationToken ct = default);
        Task<List<AudiobookFile>> GetAllAsync(CancellationToken ct = default);
    }
}
