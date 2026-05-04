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
    public class EfRemotePathMappingRepository : IRemotePathMappingRepository
    {
        private readonly ListenArrDbContext _db;

        public EfRemotePathMappingRepository(ListenArrDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public async Task<List<RemotePathMapping>> GetAllAsync(CancellationToken ct = default)
        {
            return await _db.RemotePathMappings.AsNoTracking().ToListAsync(ct);
        }

        public async Task<RemotePathMapping?> GetByIdAsync(int id, CancellationToken ct = default)
        {
            return await _db.RemotePathMappings.FindAsync(new object[] { id }, ct);
        }

        public async Task<List<RemotePathMapping>> GetByClientIdAsync(string downloadClientId, CancellationToken ct = default)
        {
            return await _db.RemotePathMappings
                .AsNoTracking()
                .Where(m => m.DownloadClientId == downloadClientId)
                .OrderByDescending(m => m.RemotePath.Length)
                .ToListAsync(ct);
        }

        public async Task<RemotePathMapping> SaveAsync(RemotePathMapping mapping, CancellationToken ct = default)
        {
            var existing = await _db.RemotePathMappings.FindAsync(new object[] { mapping.Id }, ct);
            if (existing == null)
            {
                _db.RemotePathMappings.Add(mapping);
            }
            else
            {
                _db.Entry(existing).CurrentValues.SetValues(mapping);
            }
            await _db.SaveChangesAsync(ct);
            return existing ?? mapping;
        }

        public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
        {
            var mapping = await _db.RemotePathMappings.FindAsync(new object[] { id }, ct);
            if (mapping == null) return false;
            _db.RemotePathMappings.Remove(mapping);
            await _db.SaveChangesAsync(ct);
            return true;
        }
    }
}
