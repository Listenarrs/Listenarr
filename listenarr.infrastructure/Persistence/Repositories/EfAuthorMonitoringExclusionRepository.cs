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
    public class EfAuthorMonitoringExclusionRepository : IAuthorMonitoringExclusionRepository
    {
        private readonly ListenArrDbContext _db;

        public EfAuthorMonitoringExclusionRepository(ListenArrDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public async Task AddAsync(AuthorMonitoringExclusion exclusion, CancellationToken ct = default)
        {
            // Idempotent per identity: excluding the same book twice (e.g. it was
            // re-added and deleted again) must not stack duplicate rows. Match on
            // ASIN when present, otherwise on the title+author key.
            var asin = string.IsNullOrWhiteSpace(exclusion.Asin) ? null : exclusion.Asin;
            var key = string.IsNullOrWhiteSpace(exclusion.TitleAuthorKey) ? null : exclusion.TitleAuthorKey;

            var exists = await _db.AuthorMonitoringExclusions.AnyAsync(
                e => (asin != null && e.Asin == asin)
                     || (asin == null && key != null && e.TitleAuthorKey == key), ct);
            if (exists) return;

            _db.AuthorMonitoringExclusions.Add(exclusion);
            await _db.SaveChangesAsync(ct);
        }

        public Task<List<AuthorMonitoringExclusion>> GetAllAsync(CancellationToken ct = default)
        {
            return _db.AuthorMonitoringExclusions
                .AsNoTracking()
                .OrderByDescending(e => e.CreatedAt)
                .ToListAsync(ct);
        }

        public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
        {
            var exclusion = await _db.AuthorMonitoringExclusions.FindAsync(new object[] { id }, ct);
            if (exclusion == null) return false;
            _db.AuthorMonitoringExclusions.Remove(exclusion);
            await _db.SaveChangesAsync(ct);
            return true;
        }
    }
}
