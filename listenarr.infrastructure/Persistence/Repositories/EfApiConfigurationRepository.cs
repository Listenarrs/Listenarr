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
    public class EfApiConfigurationRepository : IApiConfigurationRepository
    {
        private readonly ListenArrDbContext _db;

        public EfApiConfigurationRepository(ListenArrDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public async Task<List<ApiConfiguration>> GetAllAsync(CancellationToken ct = default)
        {
            return await _db.ApiConfigurations.AsNoTracking().OrderBy(c => c.Priority).ToListAsync(ct);
        }

        public async Task<ApiConfiguration?> GetByIdAsync(string id, CancellationToken ct = default)
        {
            return await _db.ApiConfigurations.FirstOrDefaultAsync(c => c.Id == id, ct);
        }

        public async Task<ApiConfiguration> SaveAsync(ApiConfiguration config, CancellationToken ct = default)
        {
            var existing = await _db.ApiConfigurations.FirstOrDefaultAsync(c => c.Id == config.Id, ct);
            if (existing == null)
            {
                _db.ApiConfigurations.Add(config);
                await _db.SaveChangesAsync(ct);
                return config;
            }

            _db.Entry(existing).CurrentValues.SetValues(config);
            existing.HeadersJson = config.HeadersJson;
            existing.ParametersJson = config.ParametersJson;
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
        {
            var config = await _db.ApiConfigurations.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (config == null) return false;
            _db.ApiConfigurations.Remove(config);
            await _db.SaveChangesAsync(ct);
            return true;
        }
    }
}
