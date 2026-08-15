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
using Listenarr.Application.Common.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories
{
    public class EfApplicationSettingsRepository : IApplicationSettingsRepository
    {
        private static readonly SemaphoreSlim SingletonSettingsWriteLock = new(1, 1);
        private readonly ListenArrDbContext _db;

        public EfApplicationSettingsRepository(ListenArrDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public async Task<ApplicationSettings?> GetAsync(CancellationToken ct = default)
        {
            return await _db.ApplicationSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        }

        public async Task<ApplicationSettings> InitializeIfMissingAsync(
            ApplicationSettings defaults,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(defaults);
            defaults.Id = 1;

            await SingletonSettingsWriteLock.WaitAsync(ct);
            try
            {
                var existing = await _db.ApplicationSettings
                    .AsNoTracking()
                    .FirstOrDefaultAsync(settings => settings.Id == 1, ct);
                if (existing != null)
                {
                    return existing;
                }

                defaults.Version = 1;
                _db.ApplicationSettings.Add(defaults);
                try
                {
                    await _db.SaveChangesAsync(ct);
                    return defaults;
                }
                catch (UniqueConstraintViolationException)
                {
                    _db.Entry(defaults).State = EntityState.Detached;
                    return await _db.ApplicationSettings
                        .AsNoTracking()
                        .SingleAsync(settings => settings.Id == 1, ct);
                }
            }
            finally
            {
                SingletonSettingsWriteLock.Release();
            }
        }

        public async Task<ApplicationSettings> SaveAsync(ApplicationSettings settings, CancellationToken ct = default)
        {
            settings.Id = 1;

            // ApplicationSettings is a singleton row. Several hosted services can
            // request settings at once during fresh startup, so serialize the local
            // write path to avoid noisy duplicate-key failures while still retaining
            // database-level recovery for external/multi-process races.
            await SingletonSettingsWriteLock.WaitAsync(ct);
            try
            {
                var existing = await _db.ApplicationSettings.FindAsync([1], ct);

                if (existing == null)
                {
                    settings.Version = 1;
                    _db.ApplicationSettings.Add(settings);
                    try
                    {
                        await _db.SaveChangesAsync(ct);
                        return settings;
                    }
                    catch (UniqueConstraintViolationException exception)
                    {
                        _db.Entry(settings).State = EntityState.Detached;
                        throw new ApplicationConflictException(
                            "settings_concurrency_conflict",
                            "Application settings were initialized by another request. Reload and try again.",
                            exception);
                    }
                }

                var persistedVersion = existing.Version;
                if (settings.Version <= 0)
                {
                    throw new ApplicationConflictException(
                        "settings_concurrency_conflict",
                        "Application settings must include the current version. Reload and try again.");
                }

                var expectedVersion = settings.Version;
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
            finally
            {
                SingletonSettingsWriteLock.Release();
            }
        }
    }
}
