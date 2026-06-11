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
using Listenarr.Application.Mapping;
using Listenarr.Tests.Builders;

namespace Listenarr.Tests.Features.Api.Models
{
    public class AudiobookDtoFactoryTests
    {
        [Fact]
        public async Task BuildFromEntity_MapsFieldsAndFiles_AndComputesWanted()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);

            var book = new Audiobook
            {
                Title = "Factory Book",
                Authors = new System.Collections.Generic.List<string> { "Author One" },
                Edition = "Collector's Edition",
                Series = "Primary Series",
                SeriesNumber = "2",
                SeriesMemberships = new System.Collections.Generic.List<AudiobookSeriesMembership>
                {
                    new()
                    {
                        SeriesName = "Primary Series",
                        SeriesNumber = "2",
                        IsPrimary = true,
                        SortOrder = 0
                    },
                    new()
                    {
                        SeriesName = "Shared Universe",
                        SeriesNumber = "7",
                        IsPrimary = false,
                        SortOrder = 1
                    }
                },
                BasePath = "C:\\test\\book",
                Monitored = true
            };

            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();

            var file = new AudiobookFileBuilder().WithAudiobook(book).WithPath("C:\\test\\book\\file1.m4b").WithSize(12345).Build();
            db.AudiobookFiles.Add(file);
            await db.SaveChangesAsync();

            var updated = await db.Audiobooks
                .Include(a => a.Files)
                .Include(a => a.SeriesMemberships)
                .FirstOrDefaultAsync(a => a.Id == book.Id);

            var dto = AudiobookDtoFactory.BuildFromEntity(updated);

            Assert.Equal(book.Id, dto.Id);
            Assert.Equal(book.Title, dto.Title);
            Assert.Contains("Author One", dto.Authors ?? new string[] { });
            Assert.Equal(book.Edition, dto.Edition);
            Assert.Equal(book.BasePath, dto.BasePath);
            Assert.NotNull(dto.SeriesMemberships);
            Assert.Equal(2, dto.SeriesMemberships!.Length);
            Assert.Equal("Primary Series", dto.SeriesMemberships[0].SeriesName);
            Assert.True(dto.SeriesMemberships[0].IsPrimary);
            Assert.NotNull(dto.Files);
            Assert.Single(dto.Files);
            // With a file record present in DB, wanted should be false (has content)
            Assert.False(dto.Wanted == true, "With a file present, wanted should be false");
        }

        [Fact]
        public async Task BuildFromEntity_ComputesWantedWhenNoFiles()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);

            var book = new Audiobook
            {
                Title = "NoFiles Book",
                Monitored = true
            };

            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();

            var updated = await db.Audiobooks.Include(a => a.Files).FirstOrDefaultAsync(a => a.Id == book.Id);
            var dto = AudiobookDtoFactory.BuildFromEntity(updated);

            Assert.True(dto.Wanted == true);
        }
    }
}
