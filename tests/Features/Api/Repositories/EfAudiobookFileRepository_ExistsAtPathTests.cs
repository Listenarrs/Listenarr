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
using Xunit;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Persistence;
using Listenarr.Infrastructure.Persistence.Repositories;

namespace Listenarr.Tests.Features.Api.Repositories
{
    public class EfAudiobookFileRepository_ExistsAtPathTests
    {
        private static ListenArrDbContext NewDb() =>
            new(new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        [Fact]
        public async Task ExistsAtPath_ExactMatch_ReturnsTrue()
        {
            using var db = NewDb();
            var book = new Audiobook { Title = "Elantris", BasePath = "/audiobooks/Brandon Sanderson/Elantris/Jack Garrett" };
            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();
            db.AudiobookFiles.Add(new AudiobookFile
            {
                AudiobookId = book.Id,
                Path = "/audiobooks/Brandon Sanderson/Elantris/Jack Garrett/Elantris.mp3",
            });
            await db.SaveChangesAsync();

            var repo = new EfAudiobookFileRepository(db);

            Assert.True(await repo.ExistsAtPathAsync(book.Id,
                "/audiobooks/Brandon Sanderson/Elantris/Jack Garrett/Elantris.mp3"));
        }

        [Fact]
        public async Task ExistsAtPath_StoredBareRelative_MatchesIncomingAbsolute()
        {
            // The regression: an older scan stored a bare filename ("Elantris.mp3"). A later scan finds
            // the file at its absolute path and must recognise it as already-registered — not duplicate it.
            using var db = NewDb();
            var book = new Audiobook { Title = "Elantris", BasePath = "/audiobooks/Brandon Sanderson/Elantris/Jack Garrett" };
            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();
            db.AudiobookFiles.Add(new AudiobookFile { AudiobookId = book.Id, Path = "Elantris.mp3" });
            await db.SaveChangesAsync();

            var repo = new EfAudiobookFileRepository(db);

            Assert.True(await repo.ExistsAtPathAsync(book.Id,
                "/audiobooks/Brandon Sanderson/Elantris/Jack Garrett/Elantris.mp3"));
        }

        [Fact]
        public async Task ExistsAtPath_DifferentFile_ReturnsFalse()
        {
            using var db = NewDb();
            var book = new Audiobook { Title = "Elantris", BasePath = "/audiobooks/Brandon Sanderson/Elantris/Jack Garrett" };
            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();
            db.AudiobookFiles.Add(new AudiobookFile { AudiobookId = book.Id, Path = "Elantris.mp3" });
            await db.SaveChangesAsync();

            var repo = new EfAudiobookFileRepository(db);

            Assert.False(await repo.ExistsAtPathAsync(book.Id,
                "/audiobooks/Brandon Sanderson/Elantris/Jack Garrett/Hope-of-Elantris.mp3"));
        }

        [Fact]
        public async Task ExistsAtPath_NoFileRows_ReturnsFalse()
        {
            using var db = NewDb();
            var book = new Audiobook { Title = "Empty", BasePath = "/audiobooks/x" };
            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();

            var repo = new EfAudiobookFileRepository(db);

            Assert.False(await repo.ExistsAtPathAsync(book.Id, "/audiobooks/x/anything.mp3"));
        }
    }
}
