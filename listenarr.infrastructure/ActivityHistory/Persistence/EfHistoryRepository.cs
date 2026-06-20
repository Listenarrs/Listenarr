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
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.ActivityHistory.Persistence
{
    public class EfHistoryRepository : IHistoryRepository
    {
        private readonly ListenArrDbContext _db;

        public EfHistoryRepository(ListenArrDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public async Task<HistoryPage> QueryAsync(HistoryQuery query, CancellationToken ct = default)
        {
            var limit = Math.Clamp(query.Limit, 1, 500);
            var offset = Math.Max(0, query.Offset);
            IQueryable<History> filtered = _db.History.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(query.EventType))
                filtered = filtered.Where(h => h.EventType == query.EventType);
            if (query.Outcome.HasValue)
                filtered = filtered.Where(h => h.Outcome == query.Outcome.Value);
            if (query.From.HasValue)
                filtered = filtered.Where(h => h.Timestamp >= query.From.Value);
            if (query.To.HasValue)
                filtered = filtered.Where(h => h.Timestamp <= query.To.Value);
            if (query.AudiobookId.HasValue)
                filtered = filtered.Where(h => h.AudiobookId == query.AudiobookId.Value);
            if (!string.IsNullOrWhiteSpace(query.DownloadId))
                filtered = filtered.Where(h => h.DownloadId == query.DownloadId);
            if (!string.IsNullOrWhiteSpace(query.DownloadClientId))
                filtered = filtered.Where(h => h.DownloadClientId == query.DownloadClientId);
            if (!string.IsNullOrWhiteSpace(query.CorrelationId))
                filtered = filtered.Where(h => h.CorrelationId == query.CorrelationId);

            var total = await filtered.CountAsync(ct);
            var ascending = string.Equals(query.SortDirection, "asc", StringComparison.OrdinalIgnoreCase);
            filtered = query.SortBy.ToLowerInvariant() switch
            {
                "eventtype" => ascending ? filtered.OrderBy(h => h.EventType) : filtered.OrderByDescending(h => h.EventType),
                "outcome" => ascending ? filtered.OrderBy(h => h.Outcome) : filtered.OrderByDescending(h => h.Outcome),
                "source" => ascending ? filtered.OrderBy(h => h.Source) : filtered.OrderByDescending(h => h.Source),
                _ => ascending ? filtered.OrderBy(h => h.Timestamp) : filtered.OrderByDescending(h => h.Timestamp)
            };

            var records = await filtered.Skip(offset).Take(limit).ToListAsync(ct);
            return new HistoryPage(records, total, limit, offset);
        }

        public Task<History?> GetByIdAsync(int id, CancellationToken ct = default) =>
            _db.History.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id, ct);

        public async Task<List<History>> GetByCorrelationIdAsync(string correlationId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(correlationId)) return [];
            return await _db.History
                .AsNoTracking()
                .Where(h => h.CorrelationId == correlationId)
                .OrderBy(h => h.Timestamp)
                .ThenBy(h => h.Id)
                .ToListAsync(ct);
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
