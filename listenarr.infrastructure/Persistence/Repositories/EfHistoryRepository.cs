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
    public class EfHistoryRepository : IHistoryRepository
    {
        private readonly ListenArrDbContext _db;

        public EfHistoryRepository(ListenArrDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public async Task<List<History>> GetPagedAsync(int limit, int offset, CancellationToken ct = default)
        {
            return await _db.History
                .AsNoTracking()
                .OrderByDescending(h => h.Timestamp)
                .Skip(offset)
                .Take(limit)
                .ToListAsync(ct);
        }

        public async Task<int> CountAsync(CancellationToken ct = default)
        {
            return await _db.History.CountAsync(ct);
        }

        public async Task<List<History>> GetByAudiobookIdAsync(int audiobookId, CancellationToken ct = default)
        {
            return await _db.History
                .AsNoTracking()
                .Where(h => h.AudiobookId == audiobookId)
                .OrderByDescending(h => h.Timestamp)
                .ToListAsync(ct);
        }

        public async Task<List<History>> GetByEventTypeAsync(string eventType, int? limit = null, CancellationToken ct = default)
        {
            var query = _db.History
                .AsNoTracking()
                .Where(h => h.EventType == eventType)
                .OrderByDescending(h => h.Timestamp);

            return limit.HasValue
                ? await query.Take(limit.Value).ToListAsync(ct)
                : await query.ToListAsync(ct);
        }

        public async Task<List<History>> GetBySourceAsync(string source, int? limit = null, CancellationToken ct = default)
        {
            var query = _db.History
                .AsNoTracking()
                .Where(h => h.Source == source)
                .OrderByDescending(h => h.Timestamp);

            return limit.HasValue
                ? await query.Take(limit.Value).ToListAsync(ct)
                : await query.ToListAsync(ct);
        }

        public async Task<List<History>> GetRecentAsync(int limit, CancellationToken ct = default)
        {
            return await _db.History
                .AsNoTracking()
                .OrderByDescending(h => h.Timestamp)
                .Take(limit)
                .ToListAsync(ct);
        }

        public async Task<History> AddAsync(History entry, CancellationToken ct = default)
        {
            _db.History.Add(entry);
            await _db.SaveChangesAsync(ct);
            return entry;
        }

        public async Task UpdateAsync(History entry, CancellationToken ct = default)
        {
            _db.History.Update(entry);
            await _db.SaveChangesAsync(ct);
        }

        public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
        {
            var entry = await _db.History.FindAsync(new object[] { id }, ct);
            if (entry == null) return false;
            _db.History.Remove(entry);
            await _db.SaveChangesAsync(ct);
            return true;
        }

        public async Task DeleteAllAsync(CancellationToken ct = default)
        {
            _db.History.RemoveRange(_db.History);
            await _db.SaveChangesAsync(ct);
        }

        public async Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken ct = default)
        {
            var old = await _db.History.Where(h => h.Timestamp < cutoff).ToListAsync(ct);
            _db.History.RemoveRange(old);
            await _db.SaveChangesAsync(ct);
            return old.Count;
        }
    }
}
