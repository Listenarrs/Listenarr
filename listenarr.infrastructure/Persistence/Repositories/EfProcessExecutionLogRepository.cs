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
    public class EfProcessExecutionLogRepository : IProcessExecutionLogRepository
    {
        private readonly ListenArrDbContext _db;

        public EfProcessExecutionLogRepository(ListenArrDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public async Task AddAsync(ProcessExecutionLog log, CancellationToken ct = default)
        {
            _db.ProcessExecutionLogs.Add(log);
            await _db.SaveChangesAsync(ct);
        }

        public async Task<List<ProcessExecutionLog>> GetRecentAsync(int limit, CancellationToken ct = default)
        {
            return await _db.ProcessExecutionLogs
                .AsNoTracking()
                .OrderByDescending(l => l.Timestamp)
                .Take(limit)
                .ToListAsync(ct);
        }
    }
}
