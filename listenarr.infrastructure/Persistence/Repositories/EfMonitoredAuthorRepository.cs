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
    public class EfMonitoredAuthorRepository : IMonitoredAuthorRepository
    {
        private readonly ListenArrDbContext _db;

        public EfMonitoredAuthorRepository(ListenArrDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public async Task<List<MonitoredAuthor>> GetAllAsync(CancellationToken ct = default)
        {
            return await _db.MonitoredAuthors.AsNoTracking().ToListAsync(ct);
        }

        public async Task<MonitoredAuthor?> GetByIdAsync(int id, CancellationToken ct = default)
        {
            return await _db.MonitoredAuthors.FindAsync(new object[] { id }, ct);
        }

        public async Task<MonitoredAuthor> UpsertAsync(MonitoredAuthor author, CancellationToken ct = default)
        {
            var existing = author.Id > 0
                ? await _db.MonitoredAuthors.FindAsync(new object[] { author.Id }, ct)
                : null;

            // Serialize new profile references with profile deletion. A sync that only
            // updates timestamps or errors does not write the profile assignment.
            var profileId = author.QualityProfileId;
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
                _db.MonitoredAuthors.Add(author);
            }
            else
            {
                _db.Entry(existing).CurrentValues.SetValues(author);
            }
            await _db.SaveChangesAsync(ct);
            if (transaction != null) await transaction.CommitAsync(ct);
            return existing ?? author;
        }

        public async Task<MonitoredAuthor?> GetByNameRegionLanguageAsync(string normalizedName, string region, string language, CancellationToken ct = default)
        {
            return await _db.MonitoredAuthors
                .FirstOrDefaultAsync(a => a.AuthorNameNormalized == normalizedName && a.Region == region && a.Language == language, ct);
        }

        public async Task<List<MonitoredAuthor>> GetDueForSyncAsync(DateTime cutoff, CancellationToken ct = default)
        {
            return await _db.MonitoredAuthors
                .Where(a => a.LastCheckedAt == null || a.LastCheckedAt < cutoff)
                .OrderBy(a => a.LastCheckedAt ?? DateTime.MinValue)
                .ToListAsync(ct);
        }

        public async Task<MonitoredAuthor?> UpdateQualityProfileAsync(int id, int qualityProfileId, CancellationToken ct = default)
        {
            await using var transaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction == null
                ? await _db.Database.BeginTransactionAsync(ct)
                : null;
            var author = await _db.MonitoredAuthors.SingleOrDefaultAsync(monitor => monitor.Id == id, ct);
            if (author == null) return null;

            if (!await _db.QualityProfiles.AnyAsync(profile => profile.Id == qualityProfileId, ct))
            {
                throw new ArgumentException($"Quality profile with ID {qualityProfileId} no longer exists. Select a valid quality profile.");
            }

            author.QualityProfileId = qualityProfileId;
            author.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            if (transaction != null) await transaction.CommitAsync(ct);
            return author;
        }

        public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
        {
            var author = await _db.MonitoredAuthors.FindAsync(new object[] { id }, ct);
            if (author == null) return false;
            _db.MonitoredAuthors.Remove(author);
            await _db.SaveChangesAsync(ct);
            return true;
        }
    }
}
