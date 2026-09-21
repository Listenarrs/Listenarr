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
using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Tests.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Repositories
{
    // UpsertCachedAuthorAsync and UpsertCachedSeriesAsync resolve an existing row with a read and
    // then write in a separate statement, against tables whose unique index is on
    // (NameNormalized, Region). Two writers that miss the read together both insert, and the
    // second one to reach the index loses its write.
    //
    // These use the real SQLite provider. The EF InMemory provider does not enforce unique
    // indexes at all, so a test written on that provider cannot fail on this defect no matter
    // what the production code does.
    //
    // The losing interleaving is forced rather than raced for. SavingChanges fires after the
    // caller under test has read and decided to insert but before its INSERT reaches the
    // database, so committing the other writer's row from that handler reproduces the exact
    // ordering every run instead of most runs.
    [Trait("Name", "AudiobookRepository_MetadataCacheUpsertTests")]
    [Trait("Category", "Infrastructure")]
    public sealed class AudiobookRepository_MetadataCacheUpsertTests : BaseTests
    {
        private sealed class SharedDb : IDisposable
        {
            private readonly SqliteConnection _connection;
            private readonly List<ListenArrDbContext> _contexts = new();

            public SharedDb()
            {
                _connection = new SqliteConnection("DataSource=:memory:");
                _connection.Open();
                NewContext().Database.EnsureCreated();
            }

            // Separate contexts, one shared database: a caller per DI scope, which is how the
            // repository is registered.
            public ListenArrDbContext NewContext()
            {
                var context = new ListenArrDbContext(
                    new DbContextOptionsBuilder<ListenArrDbContext>().UseSqlite(_connection).Options);
                _contexts.Add(context);
                return context;
            }

            public void Dispose()
            {
                foreach (var context in _contexts)
                {
                    context.Dispose();
                }

                _connection.Dispose();
            }
        }

        private static AuthorCacheEntry Author(string name, string? asin = null, string? imageUrl = null) =>
            new()
            {
                AuthorName = name,
                AuthorAsin = asin,
                ImageUrl = imageUrl,
                Region = "us"
            };

        private static SeriesCacheEntry Series(string name, string? asin = null, string? imageUrl = null) =>
            new()
            {
                SeriesName = name,
                SeriesAsin = asin,
                ImageUrl = imageUrl,
                Region = "us"
            };

        // The defect. Without the retry this throws UniqueConstraintViolationException and the
        // caller's write is dropped.
        [Fact]
        public async Task UpsertCachedAuthor_WhenAnotherWriterInsertsFirst_KeepsOneRowAndAppliesBothWrites()
        {
            using var db = new SharedDb();
            var losing = db.NewContext();
            var winning = db.NewContext();
            var repository = new AudiobookRepository(losing);

            var interleaved = false;
            losing.SavingChanges += (_, _) =>
            {
                if (interleaved)
                {
                    return;
                }

                interleaved = true;
                new AudiobookRepository(winning)
                    .UpsertCachedAuthorAsync(Author("Mary Shelley", imageUrl: "https://example.invalid/first.jpg"))
                    .GetAwaiter()
                    .GetResult();
            };

            var result = await repository.UpsertCachedAuthorAsync(
                Author("Mary Shelley", asin: "B000AP9A2E"));

            Assert.True(interleaved, "the interleaving never happened, so this test proved nothing");

            var rows = await db.NewContext().AuthorCacheEntries.AsNoTracking().ToListAsync();
            var row = Assert.Single(rows);
            Assert.Equal("mary shelley", row.AuthorNameNormalized);
            Assert.Equal(row.Id, result.Id);
            // The losing caller's own field landed, so the write was applied rather than dropped.
            Assert.Equal("B000AP9A2E", row.AuthorAsin);
            // And the winner's field was not discarded by the retry.
            Assert.Equal("https://example.invalid/first.jpg", row.ImageUrl);
        }

        [Fact]
        public async Task UpsertCachedSeries_WhenAnotherWriterInsertsFirst_KeepsOneRowAndAppliesBothWrites()
        {
            using var db = new SharedDb();
            var losing = db.NewContext();
            var winning = db.NewContext();
            var repository = new AudiobookRepository(losing);

            var interleaved = false;
            losing.SavingChanges += (_, _) =>
            {
                if (interleaved)
                {
                    return;
                }

                interleaved = true;
                new AudiobookRepository(winning)
                    .UpsertCachedSeriesAsync(Series("Frankenstein", imageUrl: "https://example.invalid/first.jpg"))
                    .GetAwaiter()
                    .GetResult();
            };

            var result = await repository.UpsertCachedSeriesAsync(
                Series("Frankenstein", asin: "B002V0QC5I"));

            Assert.True(interleaved, "the interleaving never happened, so this test proved nothing");

            var rows = await db.NewContext().SeriesCacheEntries.AsNoTracking().ToListAsync();
            var row = Assert.Single(rows);
            Assert.Equal("frankenstein", row.SeriesNameNormalized);
            Assert.Equal(row.Id, result.Id);
            Assert.Equal("B002V0QC5I", row.SeriesAsin);
            Assert.Equal("https://example.invalid/first.jpg", row.ImageUrl);
        }

        // Control. An uncontended write still inserts, so the retry has not turned the happy path
        // into a no-op.
        [Fact]
        public async Task UpsertCachedAuthor_UncontendedWrite_CreatesTheRow()
        {
            using var db = new SharedDb();
            var repository = new AudiobookRepository(db.NewContext());

            await repository.UpsertCachedAuthorAsync(Author("Mary Shelley", asin: "B000AP9A2E"));

            var row = Assert.Single(await db.NewContext().AuthorCacheEntries.AsNoTracking().ToListAsync());
            Assert.Equal("mary shelley", row.AuthorNameNormalized);
            Assert.Equal("B000AP9A2E", row.AuthorAsin);
        }

        // Control. A second write for the same key updates the row it found rather than inserting
        // a duplicate, which is the behaviour the method name has always promised.
        [Fact]
        public async Task UpsertCachedAuthor_SecondWriteForTheSameKey_UpdatesInPlace()
        {
            using var db = new SharedDb();
            var repository = new AudiobookRepository(db.NewContext());

            var first = await repository.UpsertCachedAuthorAsync(Author("Mary Shelley"));
            var second = await repository.UpsertCachedAuthorAsync(
                Author("Mary Shelley", imageUrl: "https://example.invalid/updated.jpg"));

            Assert.Equal(first.Id, second.Id);
            var row = Assert.Single(await db.NewContext().AuthorCacheEntries.AsNoTracking().ToListAsync());
            Assert.Equal("https://example.invalid/updated.jpg", row.ImageUrl);
        }

        // Control. A genuinely different key still gets its own row, so the retry has not been
        // written in a way that collapses distinct authors onto one another.
        [Fact]
        public async Task UpsertCachedAuthor_DifferentNormalizedName_CreatesASecondRow()
        {
            using var db = new SharedDb();
            var repository = new AudiobookRepository(db.NewContext());

            await repository.UpsertCachedAuthorAsync(Author("Mary Shelley"));
            await repository.UpsertCachedAuthorAsync(Author("Bram Stoker"));

            var rows = await db.NewContext().AuthorCacheEntries.AsNoTracking().ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, row => row.AuthorNameNormalized == "mary shelley");
            Assert.Contains(rows, row => row.AuthorNameNormalized == "bram stoker");
        }

        [Fact]
        public async Task UpsertCachedSeries_DifferentNormalizedName_CreatesASecondRow()
        {
            using var db = new SharedDb();
            var repository = new AudiobookRepository(db.NewContext());

            await repository.UpsertCachedSeriesAsync(Series("Frankenstein"));
            await repository.UpsertCachedSeriesAsync(Series("Dracula"));

            Assert.Equal(2, await db.NewContext().SeriesCacheEntries.CountAsync());
        }

        // Control with teeth. The unique violation raised on the UPDATE path is a different
        // defect: the by-ASIN lookup resolved a row belonging to another name, and renaming it
        // collides with that other name's own row. Re-reading cannot change that outcome, so it
        // must still reach the caller. If the retry had been written to catch the violation
        // unconditionally this test would hang or report success instead.
        [Fact]
        public async Task UpsertCachedAuthor_WhenTheResolvedRowCannotTakeTheIncomingName_StillThrows()
        {
            using var db = new SharedDb();
            var repository = new AudiobookRepository(db.NewContext());

            await repository.UpsertCachedAuthorAsync(Author("Mary Shelley", asin: "B000AP9A2E"));
            await repository.UpsertCachedAuthorAsync(Author("Bram Stoker"));

            await Assert.ThrowsAsync<UniqueConstraintViolationException>(() =>
                repository.UpsertCachedAuthorAsync(Author("Bram Stoker", asin: "B000AP9A2E")));
        }
    }
}
