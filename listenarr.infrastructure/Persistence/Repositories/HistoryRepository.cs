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

using Listenarr.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories
{
    public class HistoryRepository
    {
        private readonly ListenArrDbContext _context;

        public HistoryRepository(ListenArrDbContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public async Task<History> AddAsync(History history)
        {
            _context.History.Add(history);
            await _context.SaveChangesAsync();
            return history;
        }

        public async Task<History?> GetByIdAsync(int id)
        {
            return await _context.History.FindAsync(id);
        }

        public async Task<List<History>> GetAllAsync()
        {
            return await _context.History
                .OrderByDescending(h => h.Timestamp)
                .ToListAsync();
        }

        public async Task<List<History>> GetByAudiobookIdAsync(int audiobookId)
        {
            return await _context.History
                .Where(h => h.AudiobookId == audiobookId)
                .OrderByDescending(h => h.Timestamp)
                .ToListAsync();
        }

        public async Task UpdateAsync(History history)
        {
            _context.History.Update(history);
            await _context.SaveChangesAsync();
        }

        public async Task DeleteAsync(int id)
        {
            var history = await _context.History.FindAsync(id);
            if (history != null)
            {
                _context.History.Remove(history);
                await _context.SaveChangesAsync();
            }
        }
    }
}
