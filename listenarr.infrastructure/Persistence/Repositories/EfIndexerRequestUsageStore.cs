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
    public sealed class EfIndexerRequestUsageStore(IDbContextFactory<ListenArrDbContext> contextFactory) : IIndexerRequestUsageStore
    {
        public async Task<IReadOnlyList<IndexerRequestUsage>> LoadSinceAsync(DateTime sinceHourUtc, CancellationToken ct = default)
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct);
            return await db.IndexerRequestUsages
                .AsNoTracking()
                .Where(usage => usage.HourStartUtc >= sinceHourUtc)
                .ToListAsync(ct);
        }

        public async Task SaveAsync(int indexerId, DateTime hourStartUtc, int count, CancellationToken ct = default)
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct);
            var existing = await db.IndexerRequestUsages.FindAsync(new object[] { indexerId, hourStartUtc }, ct);
            if (existing == null)
            {
                db.IndexerRequestUsages.Add(new IndexerRequestUsage
                {
                    IndexerId = indexerId,
                    HourStartUtc = hourStartUtc,
                    Count = count
                });
            }
            else
            {
                existing.Count = count;
            }
            await db.SaveChangesAsync(ct);
        }

        public async Task DeleteBeforeAsync(DateTime beforeHourUtc, CancellationToken ct = default)
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct);
            var expired = await db.IndexerRequestUsages
                .Where(usage => usage.HourStartUtc < beforeHourUtc)
                .ToListAsync(ct);
            if (expired.Count == 0)
                return;

            db.IndexerRequestUsages.RemoveRange(expired);
            await db.SaveChangesAsync(ct);
        }
    }
}
