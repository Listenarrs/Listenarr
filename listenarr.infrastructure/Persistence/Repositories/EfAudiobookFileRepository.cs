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
using Listenarr.Application.Audiobooks;
using Listenarr.Domain.Common;
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
            // Fast path: exact string match. Scans and imports store canonical absolute paths, so the
            // incoming path normally string-matches the stored one — an index-friendly lookup that
            // covers the overwhelming majority of calls.
            if (await _db.AudiobookFiles.AnyAsync(f => f.AudiobookId == audiobookId && f.Path == path, ct))
            {
                return true;
            }

            // Robust path: a stored row may represent the *same* file under a different path shape —
            // most commonly a legacy bare/relative path (e.g. "Elantris.mp3") left by an older scan,
            // which never string-matches today's absolute path. Without this, a rescan re-registers
            // the file and spawns a duplicate row. Resolve this book's rows to a canonical absolute
            // form (anchoring relative paths on its BasePath) and compare. A book has only a handful
            // of file rows, so this stays cheap and only runs when the exact match misses.
            var storedPaths = await _db.AudiobookFiles
                .Where(f => f.AudiobookId == audiobookId && f.Path != null)
                .Select(f => f.Path!)
                .ToListAsync(ct);

            if (storedPaths.Count == 0)
            {
                return false;
            }

            var basePath = await _db.Audiobooks
                .Where(a => a.Id == audiobookId)
                .Select(a => a.BasePath)
                .FirstOrDefaultAsync(ct);

            var target = CanonicalizePath(path, basePath);
            return storedPaths.Any(stored =>
                string.Equals(CanonicalizePath(stored, basePath), target, StringComparison.OrdinalIgnoreCase));
        }

        public async Task<bool> IsPathUsedByOtherAsync(int audiobookId, string path, CancellationToken ct = default)
        {
            // Cross-book guard: exact match is sufficient here because stored paths are canonical
            // absolute by convention, and a file physically lives under exactly one book's folder
            // (resolving every other book's relative paths would require each book's BasePath).
            return await _db.AudiobookFiles.AnyAsync(f => f.AudiobookId != audiobookId && f.Path == path, ct);
        }

        // Resolve a stored or incoming path to a canonical absolute form so the same file is
        // recognised regardless of shape (legacy relative vs. absolute, separator/`..` differences).
        private static string CanonicalizePath(string path, string? basePath)
            => FileUtils.NormalizeStoredPath(FileUtils.CombineWithOptionalBase(basePath, path));

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
