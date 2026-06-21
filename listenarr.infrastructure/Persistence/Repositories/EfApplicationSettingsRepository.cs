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
using Listenarr.Application.Common.Exceptions;

namespace Listenarr.Infrastructure.Persistence.Repositories
{
    public class EfApplicationSettingsRepository : IApplicationSettingsRepository
    {
        private readonly ListenArrDbContext _db;

        public EfApplicationSettingsRepository(ListenArrDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public async Task<ApplicationSettings?> GetAsync(CancellationToken ct = default)
        {
            return await _db.ApplicationSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        }

        public async Task<ApplicationSettings> SaveAsync(ApplicationSettings settings, CancellationToken ct = default)
        {
            settings.Id = 1;

            var existing = await _db.ApplicationSettings.FindAsync([1], ct);

            if (existing == null)
            {
                settings.Version = 1;
                _db.ApplicationSettings.Add(settings);
                await _db.SaveChangesAsync(ct);
                return settings;
            }

            var persistedVersion = existing.Version;
            var expectedVersion = settings.Version == 0
                ? persistedVersion
                : settings.Version;

            if (expectedVersion != persistedVersion)
            {
                throw new ApplicationConflictException(
                    "settings_concurrency_conflict",
                    "Application settings were changed by another request. Reload and try again.");
            }

            settings.Version = persistedVersion + 1;

            // Detach the existing tracked entity to avoid identity-map conflicts when
            // `settings` is the same object reference that was previously Add()ed.
            if (!ReferenceEquals(existing, settings))
            {
                _db.Entry(existing).State = EntityState.Detached;
            }

            _db.ApplicationSettings.Update(settings);
            try
            {
                _db.Entry(settings).Property(item => item.Version).OriginalValue = persistedVersion;
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException exception)
            {
                throw new ApplicationConflictException(
                    "settings_concurrency_conflict",
                    "Application settings were changed by another request. Reload and try again.",
                    exception);
            }
            return settings;
        }
    }
}
