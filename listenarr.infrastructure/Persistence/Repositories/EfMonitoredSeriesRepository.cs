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

            // Serialize new profile references with profile deletion. A sync that only
            // updates timestamps or errors does not write the profile assignment.
            var profileId = series.QualityProfileId;
            var assigningProfile = profileId.HasValue && (existing == null
                || _db.Entry(existing).Property(monitor => monitor.QualityProfileId).OriginalValue != profileId);
            await using var transaction = assigningProfile && _db.Database.IsRelational()
                && _db.Database.CurrentTransaction == null
                ? await _db.Database.BeginTransactionAsync(ct)
                : null;
            if (assigningProfile && !await _db.QualityProfiles.AnyAsync(profile => profile.Id == profileId, ct))
            {
                throw new ArgumentException($"Quality profile with ID {profileId} no longer exists. Select a valid quality profile.");
            }

            if (existing == null)
            {
                _db.MonitoredSeries.Add(series);
            }
            else
            {
                _db.Entry(existing).CurrentValues.SetValues(series);
            }
            await _db.SaveChangesAsync(ct);
            if (transaction != null) await transaction.CommitAsync(ct);
            return existing ?? series;
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

        public async Task<MonitoredSeries?> UpdateQualityProfileAsync(int id, int qualityProfileId, CancellationToken ct = default)
        {
            await using var transaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction == null
                ? await _db.Database.BeginTransactionAsync(ct)
                : null;
            var series = await _db.MonitoredSeries.SingleOrDefaultAsync(monitor => monitor.Id == id, ct);
            if (series == null) return null;

            if (!await _db.QualityProfiles.AnyAsync(profile => profile.Id == qualityProfileId, ct))
            {
                throw new ArgumentException($"Quality profile with ID {qualityProfileId} no longer exists. Select a valid quality profile.");
            }

            series.QualityProfileId = qualityProfileId;
            series.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            if (transaction != null) await transaction.CommitAsync(ct);
            return series;
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
