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
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Persistence.Repositories
{
    public class EfRootFolderRepository : IRootFolderRepository
    {
        private readonly IDbContextFactory<ListenArrDbContext> _dbFactory;
        private readonly ILogger<EfRootFolderRepository> _logger;

        public EfRootFolderRepository(IDbContextFactory<ListenArrDbContext> dbFactory, ILogger<EfRootFolderRepository> logger)
        {
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<RootFolder> AddAsync(RootFolder root)
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            ctx.RootFolders.Add(root);
            await ctx.SaveChangesAsync();

            return root;
        }

        public async Task<List<RootFolder>> GetAllAsync()
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            return await ctx.RootFolders.OrderBy(r => r.Name).ToListAsync();
        }

        public async Task<RootFolder?> GetByIdAsync(int id)
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            return await ctx.RootFolders.FindAsync(id);
        }

        public async Task<RootFolder?> GetByPathAsync(string path)
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            return await ctx.RootFolders.FirstOrDefaultAsync(r => r.Path == path);
        }

        public async Task RemoveAsync(int id)
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            var r = await ctx.RootFolders.FindAsync(id);
            if (r == null) return;
            ctx.RootFolders.Remove(r);
            await ctx.SaveChangesAsync();
        }

        public async Task UpdateAsync(RootFolder root)
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            ctx.RootFolders.Update(root);
            await ctx.SaveChangesAsync();
        }

        public async Task<RootFolder?> GetDefaultAsync()
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            return await ctx.RootFolders.FirstOrDefaultAsync(r => r.IsDefault);
        }

        public async Task ClearDefaultExceptAsync(int? excludeId, CancellationToken ct = default)
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            var others = await ctx.RootFolders
                .Where(r => r.IsDefault && (excludeId == null || r.Id != excludeId.Value))
                .ToListAsync(ct);
            foreach (var o in others) o.IsDefault = false;
            if (others.Count > 0) await ctx.SaveChangesAsync(ct);
        }

        public async Task SaveChangesAsync(CancellationToken ct = default)
        {
            // No-op for factory-based repo; each method manages its own context
        }
    }
}
