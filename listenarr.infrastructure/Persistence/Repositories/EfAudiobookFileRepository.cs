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

        public async Task ReassignAsync(int fileId, int newAudiobookId, string? newPath, CancellationToken ct = default)
        {
            // Targeted column update via a detached stub: Update(entity) on rows
            // loaded with their navigation graphs trips EF's identity map on the
            // second file of a bulk transfer ("instance with the same key is
            // already being tracked"). Marking only the reassigned columns
            // modified loads and tracks no graphs. (ExecuteUpdate would also
            // work, but isn't supported by the InMemory test provider.)
            var tracked = _db.AudiobookFiles.Local.FirstOrDefault(f => f.Id == fileId);
            if (tracked != null)
            {
                _db.Entry(tracked).State = EntityState.Detached;
            }

            var stub = new AudiobookFile { Id = fileId, AudiobookId = newAudiobookId, Path = newPath };
            var entry = _db.Entry(stub);
            entry.Property(f => f.AudiobookId).IsModified = true;
            if (newPath != null)
            {
                entry.Property(f => f.Path).IsModified = true;
            }

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            finally
            {
                // Always detach: a failed save (e.g. unique-key collision) must not
                // leave a dirty stub behind for an unrelated later SaveChanges.
                entry.State = EntityState.Detached;
            }
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

        public async Task<List<AudiobookFormatSummary>> GetFormatSummariesAsync(CancellationToken ct = default)
        {
            // One row per (AudiobookId, Format, Codec, Container) — avoids materialising chapter-level rows
            var rows = await _db.AudiobookFiles
                .AsNoTracking()
                .GroupBy(f => new { f.AudiobookId, f.Format, f.Codec, f.Container })
                .Select(g => new
                {
                    g.Key.AudiobookId,
                    g.Key.Format,
                    g.Key.Codec,
                    g.Key.Container,
                    Bitrate = g.Max(f => f.Bitrate),
                    Path = g.Min(f => f.Path),
                })
                .ToListAsync(ct);

            return rows.Select(r => new AudiobookFormatSummary
            {
                AudiobookId = r.AudiobookId,
                Format = r.Format,
                Codec = r.Codec,
                Container = r.Container,
                Bitrate = r.Bitrate,
                Path = r.Path,
            }).ToList();
        }

        public async Task<Dictionary<int, int>> GetCountsByAudiobookIdAsync(CancellationToken ct = default)
        {
            return await _db.AudiobookFiles
                .AsNoTracking()
                .GroupBy(f => f.AudiobookId)
                .Select(g => new { AudiobookId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(r => r.AudiobookId, r => r.Count, ct);
        }
    }
}
