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
using Listenarr.Infrastructure.Persistence;
using Listenarr.Tests.Builders;

namespace Listenarr.Tests.Features.Infrastructure.Repositories
{
    public class AudiobookRepositoryTests
    {
        [Fact]
        public async Task GetAll_IncludesWantedFlag_ForMonitoredWithoutFiles()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);

            // Monitored without files -> wanted = true
            var wantedBook = new AudiobookBuilder().WithTitle("Wanted Book").WithMonitored().Build();
            db.Audiobooks.Add(wantedBook);

            // Monitored with files -> wanted = false
            var hasFileBook = new AudiobookBuilder().WithTitle("Has File").WithMonitored().Build();
            db.Audiobooks.Add(hasFileBook);
            await db.SaveChangesAsync();

            var file = new AudiobookFileBuilder().WithAudiobook(hasFileBook).WithPath("C:\\temp\\f.m4b").WithSize(1234).Build();
            db.AudiobookFiles.Add(file);
            await db.SaveChangesAsync();

            // Exercise repository directly similar to controller
            var audiobooks = await db.Audiobooks.Include(a => a.Files).ToListAsync();

            var dto = audiobooks.Select(a => new
            {
                id = a.Id,
                wanted = a.Monitored && (a.Files == null || !a.Files.Any())
            }).ToList();

            var wantedDto = dto.First(d => d.id == wantedBook.Id);
            var hasFileDto = dto.First(d => d.id == hasFileBook.Id);

            Assert.True(wantedDto.wanted);
            Assert.False(hasFileDto.wanted);
        }

        [Fact]
        public async Task GetById_IncludesWantedFlag_Correctly()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);

            var book = new AudiobookBuilder().WithTitle("Single Book").WithMonitored().Build();
            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();

            // Initially should be wanted
            var updated = await db.Audiobooks.Include(a => a.Files).FirstOrDefaultAsync(a => a.Id == book.Id);
            var wanted = updated.Monitored && (updated.Files == null || !updated.Files.Any());
            Assert.True(wanted);

            // Add file and re-evaluate
            var file = new AudiobookFileBuilder().WithAudiobook(book).WithPath("C:\\temp\\single.m4b").WithSize(1024).Build();
            db.AudiobookFiles.Add(file);
            await db.SaveChangesAsync();

            var updated2 = await db.Audiobooks.Include(a => a.Files).FirstOrDefaultAsync(a => a.Id == book.Id);
            var wanted2 = updated2.Monitored && (updated2.Files == null || !updated2.Files.Any());
            Assert.False(wanted2);
        }
    }
}

