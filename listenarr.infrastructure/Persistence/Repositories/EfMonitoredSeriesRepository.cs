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
    public class EfMonitoredSeriesRepository : IMonitoredSeriesRepository
    {
        private readonly ListenArrDbContext _db;

        public EfMonitoredSeriesRepository(ListenArrDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public async Task<List<MonitoredSeries>> GetAllAsync(CancellationToken ct = default)
        {
            return await _db.MonitoredSeries.AsNoTracking().ToListAsync(ct);
        }

        public async Task<MonitoredSeries?> GetByIdAsync(int id, CancellationToken ct = default)
        {
            return await _db.MonitoredSeries.FindAsync(new object[] { id }, ct);
        }

        public async Task<MonitoredSeries> UpsertAsync(MonitoredSeries series, CancellationToken ct = default)
        {
            var existing = series.Id > 0
                ? await _db.MonitoredSeries.FindAsync(new object[] { series.Id }, ct)
                : null;

            if (existing == null)
            {
                _db.MonitoredSeries.Add(series);
                await _db.SaveChangesAsync(ct);
                return series;
            }

            _db.Entry(existing).CurrentValues.SetValues(series);
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        public async Task<MonitoredSeries?> GetByNameRegionLanguageAsync(string normalizedName, string region, string language, CancellationToken ct = default)
        {
            return await _db.MonitoredSeries
                .FirstOrDefaultAsync(s => s.SeriesNameNormalized == normalizedName && s.Region == region && s.Language == language, ct);
        }

        public async Task<List<MonitoredSeries>> GetDueForSyncAsync(DateTime cutoff, CancellationToken ct = default)
        {
            return await _db.MonitoredSeries
                .Where(s => s.LastCheckedAt == null || s.LastCheckedAt < cutoff)
                .OrderBy(s => s.LastCheckedAt ?? DateTime.MinValue)
                .ToListAsync(ct);
        }

        public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
        {
            var series = await _db.MonitoredSeries.FindAsync(new object[] { id }, ct);
            if (series == null) return false;
            _db.MonitoredSeries.Remove(series);
            await _db.SaveChangesAsync(ct);
            return true;
        }
    }
}
