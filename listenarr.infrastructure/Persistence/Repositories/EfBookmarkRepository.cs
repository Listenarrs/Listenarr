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
using Listenarr.Application.Audiobooks.Bookmarks;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories
{
    public class EfBookmarkRepository(ListenArrDbContext db) : IBookmarkRepository
    {
        public async Task<IReadOnlyList<Bookmark>> GetByAudiobookIdAsync(int audiobookId, CancellationToken ct = default)
        {
            return await db.Bookmarks
                .AsNoTracking()
                .Where(b => b.AudiobookId == audiobookId)
                .OrderBy(b => b.CreatedUtc)
                .ToListAsync(ct);
        }

        public async Task<Bookmark> AddAsync(Bookmark bookmark, CancellationToken ct = default)
        {
            db.Bookmarks.Add(bookmark);
            await db.SaveChangesAsync(ct);
            return bookmark;
        }

        public async Task<bool> DeleteAsync(int audiobookId, int bookmarkId, CancellationToken ct = default)
        {
            var bookmark = await db.Bookmarks
                .FirstOrDefaultAsync(b => b.Id == bookmarkId && b.AudiobookId == audiobookId, ct);
            if (bookmark is null) return false;

            db.Bookmarks.Remove(bookmark);
            await db.SaveChangesAsync(ct);
            return true;
        }
    }
}
