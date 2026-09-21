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

            var saves = 0;
            var interleaved = false;
            losing.SavingChanges += (_, _) =>
            {
                saves++;
            };
            losing.SavingChanges += (_, _) =>
            {
                if (interleaved)
                {
                    return;
                }

                interleaved = true;
                // Task.Run keeps this off the runner's synchronization context. SavingChanges is
                // a synchronous event, so the other writer has to be waited on here, and blocking
                // on a continuation that wanted to post back to a busy context is how that hangs.
                Task.Run(() => new AudiobookRepository(winning)
                        .UpsertCachedAuthorAsync(Author("Mary Shelley", imageUrl: "https://example.invalid/first.jpg")))
                    .GetAwaiter()
                    .GetResult();
            };

            var result = await repository.UpsertCachedAuthorAsync(
                Author("Mary Shelley", asin: "B000AP9A2E"));

            Assert.True(interleaved, "the interleaving never happened, so this test proved nothing");
            // One failed attempt and one that succeeded. More than two would mean the retry is
            // not resolving the winner's row on the second pass.
            Assert.Equal(2, saves);

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

            var saves = 0;
            var interleaved = false;
            losing.SavingChanges += (_, _) =>
            {
                saves++;
            };
            losing.SavingChanges += (_, _) =>
            {
                if (interleaved)
                {
                    return;
                }

                interleaved = true;
                Task.Run(() => new AudiobookRepository(winning)
                        .UpsertCachedSeriesAsync(Series("Frankenstein", imageUrl: "https://example.invalid/first.jpg")))
                    .GetAwaiter()
                    .GetResult();
            };

            var result = await repository.UpsertCachedSeriesAsync(
                Series("Frankenstein", asin: "B002V0QC5I"));

            Assert.True(interleaved, "the interleaving never happened, so this test proved nothing");
            Assert.Equal(2, saves);

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



        // Control with teeth, and the assertion that gives it teeth is the save count rather
        // than the throw. The unique violation raised on the UPDATE path is a different defect:
        // the by-ASIN lookup resolved a row belonging to another name, and renaming it collides
        // with that other name's own row. Re-reading resolves the same row every pass, so it
        // must not be retried.
        //
        // The throw alone proves nothing here. Drop the `inserting` term from the retry filter
        // and this still throws, because the attempt bound stops it either way. What separates
        // the two is how many times it reaches the database: once when the filter is right,
        // three times when it is not.
        [Fact]
        public async Task UpsertCachedAuthor_WhenTheResolvedRowCannotTakeTheIncomingName_ThrowsWithoutRetrying()
        {
            using var db = new SharedDb();
            var context = db.NewContext();
            var repository = new AudiobookRepository(context);

            await repository.UpsertCachedAuthorAsync(Author("Mary Shelley", asin: "B000AP9A2E"));
            await repository.UpsertCachedAuthorAsync(Author("Bram Stoker"));

            var saves = 0;
            context.SavingChanges += (_, _) => saves++;

            await Assert.ThrowsAsync<UniqueConstraintViolationException>(() =>
                repository.UpsertCachedAuthorAsync(Author("Bram Stoker", asin: "B000AP9A2E")));

            Assert.Equal(1, saves);
        }

        [Fact]
        public async Task UpsertCachedSeries_WhenTheResolvedRowCannotTakeTheIncomingName_ThrowsWithoutRetrying()
        {
            using var db = new SharedDb();
            var context = db.NewContext();
            var repository = new AudiobookRepository(context);

            await repository.UpsertCachedSeriesAsync(Series("Frankenstein", asin: "B002V0QC5I"));
            await repository.UpsertCachedSeriesAsync(Series("Dracula"));

            var saves = 0;
            context.SavingChanges += (_, _) => saves++;

            await Assert.ThrowsAsync<UniqueConstraintViolationException>(() =>
                repository.UpsertCachedSeriesAsync(Series("Dracula", asin: "B002V0QC5I")));

            Assert.Equal(1, saves);
        }

        // A name made only of punctuation normalizes to the empty string, and two such names
        // collide on the unique index with no concurrency involved. Retrying cannot help: the
        // key written is blank, and the name lookup is skipped for a blank key, so every pass
        // repeats the same insert. Resolving on the empty string instead would be worse, since
        // it would merge two authors whose names differ only in punctuation onto one row.
        //
        // So this collision is left to surface on the first attempt, the way it does today. The
        // save count is the assertion that matters; without the blank-name term in the retry
        // filter it is three.
        [Fact]
        public async Task UpsertCachedAuthor_BlankNormalizedName_ThrowsWithoutRetrying()
        {
            using var db = new SharedDb();
            var context = db.NewContext();
            var repository = new AudiobookRepository(context);

            await new AudiobookRepository(db.NewContext()).UpsertCachedAuthorAsync(Author("???"));

            var saves = 0;
            context.SavingChanges += (_, _) => saves++;

            await Assert.ThrowsAsync<UniqueConstraintViolationException>(() =>
                repository.UpsertCachedAuthorAsync(Author("!!!")));

            Assert.Equal(1, saves);
        }
    }
}
