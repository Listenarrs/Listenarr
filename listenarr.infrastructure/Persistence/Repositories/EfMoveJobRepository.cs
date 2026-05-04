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
    public class EfMoveJobRepository : IMoveJobRepository
    {
        private readonly ListenArrDbContext _db;

        public EfMoveJobRepository(ListenArrDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public async Task<MoveJob?> GetByIdAsync(Guid id, CancellationToken ct = default)
        {
            return await _db.MoveJobs.FindAsync(new object[] { id }, ct);
        }

        public async Task<List<MoveJob>> GetByStatusAsync(IEnumerable<string> statuses, CancellationToken ct = default)
        {
            return await _db.MoveJobs
                .AsNoTracking()
                .Where(j => statuses.Contains(j.Status))
                .ToListAsync(ct);
        }

        public async Task<MoveJob> AddAsync(MoveJob job, CancellationToken ct = default)
        {
            _db.MoveJobs.Add(job);
            await _db.SaveChangesAsync(ct);
            return job;
        }

        public async Task UpdateAsync(MoveJob job, CancellationToken ct = default)
        {
            _db.MoveJobs.Update(job);
            await _db.SaveChangesAsync(ct);
        }
    }
}
