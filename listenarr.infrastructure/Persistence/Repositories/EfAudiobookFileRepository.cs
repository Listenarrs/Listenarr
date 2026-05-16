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
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories
{
    public class EfAudiobookFileRepository : IAudiobookFileRepository
    {
        private readonly ListenArrDbContext _db;

        public EfAudiobookFileRepository(ListenArrDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public async Task<AudiobookFile?> GetByIdAsync(int id, CancellationToken ct = default)
        {
            return await _db.AudiobookFiles.FindAsync(new object[] { id }, ct);
        }

        public async Task<List<AudiobookFile>> GetByAudiobookIdAsync(int audiobookId, CancellationToken ct = default)
        {
            return await _db.AudiobookFiles
                .AsNoTracking()
                .Where(f => f.AudiobookId == audiobookId)
                .ToListAsync(ct);
        }

        public async Task<List<AudiobookFile>> GetMissingMetadataAsync(int max, CancellationToken ct = default)
        {
            return await _db.AudiobookFiles
                .AsNoTracking()
                .Where(f => f.DurationSeconds == null || f.Format == null || f.SampleRate == null)
                .OrderBy(f => f.Id)
                .Take(max)
                .ToListAsync(ct);
        }

        public async Task<AudiobookFile> AddAsync(AudiobookFile file, CancellationToken ct = default)
        {
            _db.AudiobookFiles.Add(file);
            await _db.SaveChangesAsync(ct);
            return file;
        }

        public async Task UpdateAsync(AudiobookFile file, CancellationToken ct = default)
        {
            _db.AudiobookFiles.Update(file);
            await _db.SaveChangesAsync(ct);
        }

        public async Task DeleteByAudiobookIdAsync(int audiobookId, CancellationToken ct = default)
        {
            var files = await _db.AudiobookFiles.Where(f => f.AudiobookId == audiobookId).ToListAsync(ct);
            _db.AudiobookFiles.RemoveRange(files);
            await _db.SaveChangesAsync(ct);
        }

        public async Task DeleteAsync(int id, CancellationToken ct = default)
        {
            var file = await _db.AudiobookFiles.FindAsync(new object[] { id }, ct);
            if (file != null)
            {
                _db.AudiobookFiles.Remove(file);
                await _db.SaveChangesAsync(ct);
            }
        }

        public async Task<bool> ExistsAtPathAsync(int audiobookId, string path, CancellationToken ct = default)
        {
            return await _db.AudiobookFiles.AnyAsync(f => f.AudiobookId == audiobookId && f.Path == path, ct);
        }

        public async Task<bool> IsPathUsedByOtherAsync(int audiobookId, string path, CancellationToken ct = default)
        {
            return await _db.AudiobookFiles.AnyAsync(f => f.AudiobookId != audiobookId && f.Path == path, ct);
        }

        public async Task<List<string>> GetAllFilePathsAsync(CancellationToken ct = default)
        {
            return await _db.AudiobookFiles
                .Where(f => f.Path != null)
                .Select(f => f.Path!)
                .ToListAsync(ct);
        }

        public async Task<List<AudiobookFile>> GetAllAsync(CancellationToken ct = default)
        {
            return await _db.AudiobookFiles
                .AsNoTracking()
                .ToListAsync(ct);
        }
    }
}
