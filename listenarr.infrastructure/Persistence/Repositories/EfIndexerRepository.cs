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
    public class EfIndexerRepository : IIndexerRepository
    {
        private readonly ListenArrDbContext _db;

        public EfIndexerRepository(ListenArrDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public async Task<Indexer?> GetByIdAsync(int id, CancellationToken ct = default)
        {
            return await _db.Indexers.FindAsync(new object[] { id }, ct);
        }

        public async Task<Indexer?> GetByNameAsync(string name, CancellationToken ct = default)
        {
            return await _db.Indexers.AsNoTracking().FirstOrDefaultAsync(i => i.Name == name, ct);
        }

        public async Task<List<Indexer>> GetAllAsync(CancellationToken ct = default)
        {
            return await _db.Indexers.AsNoTracking().ToListAsync(ct);
        }

        public async Task<List<Indexer>> GetEnabledAsync(bool isAutomaticSearch, CancellationToken ct = default)
        {
            return await _db.Indexers
                .AsNoTracking()
                .Where(i => i.IsEnabled && (isAutomaticSearch ? i.EnableAutomaticSearch : i.EnableInteractiveSearch))
                .OrderBy(i => i.Priority)
                .ToListAsync(ct);
        }

        public async Task<Indexer> AddAsync(Indexer indexer, CancellationToken ct = default)
        {
            _db.Indexers.Add(indexer);
            await _db.SaveChangesAsync(ct);
            return indexer;
        }

        public async Task UpdateAsync(Indexer indexer, CancellationToken ct = default)
        {
            var existing = await _db.Indexers.FindAsync(new object[] { indexer.Id }, ct);
            if (existing == null) throw new InvalidOperationException($"Indexer {indexer.Id} not found.");
            _db.Entry(existing).CurrentValues.SetValues(indexer);
            await _db.SaveChangesAsync(ct);
        }

        public async Task DeleteAsync(int id, CancellationToken ct = default)
        {
            var indexer = await _db.Indexers.FindAsync(new object[] { id }, ct);
            if (indexer == null) return;
            _db.Indexers.Remove(indexer);
            await _db.SaveChangesAsync(ct);
        }
    }
}
