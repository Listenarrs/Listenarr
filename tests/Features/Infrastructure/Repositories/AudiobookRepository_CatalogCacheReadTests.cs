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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Listenarr.Infrastructure.Persistence.Repositories;

namespace Listenarr.Tests.Features.Infrastructure.Repositories
{
    // Regression for the author/series catalog cache read paths.
    //
    // These deliberately use the real SQLite provider (not the EF InMemory provider) so that
    // query translation is actually exercised. The cache reads order by recency; using
    // Nullable.GetValueOrDefault there is not translatable by the SQLite provider and throws
    // at runtime, which the cache callers swallow as a best-effort miss — silently disabling
    // the cache so every author/series page re-fetched from Audible. The InMemory provider
    // tolerates that LINQ, so it cannot catch this class of bug.
    public class AudiobookRepository_CatalogCacheReadTests
    {
        private sealed class TestDb : IDisposable
        {
            private readonly SqliteConnection _connection;
            public ListenArrDbContext Db { get; }

            public TestDb()
            {
                _connection = new SqliteConnection("DataSource=:memory:");
                _connection.Open();
                Db = new ListenArrDbContext(
                    new DbContextOptionsBuilder<ListenArrDbContext>().UseSqlite(_connection).Options);
                Db.Database.EnsureCreated();
            }

            public void Dispose()
            {
                Db.Dispose();
                _connection.Dispose();
            }
        }

        [Fact]
        public async Task GetCachedAuthorByName_ReadsBackUnderSqlite()
        {
            using var ctx = new TestDb();
            var repository = new AudiobookRepository(ctx.Db);

            await repository.UpsertCachedAuthorAsync(new AuthorCacheEntry
            {
                AuthorName = "Brandon Sanderson",
                AuthorAsin = "B001IGFHW6",
                Region = "us"
            });

            var byName = await repository.GetCachedAuthorByNameAsync("Brandon Sanderson", "us");
            var byAsin = await repository.GetCachedAuthorByAsinAsync("B001IGFHW6", "us");

            Assert.NotNull(byName);
            Assert.Equal("B001IGFHW6", byName!.AuthorAsin);
            Assert.NotNull(byAsin);
            Assert.Equal("Brandon Sanderson", byAsin!.AuthorName);
        }

        [Fact]
        public async Task GetCachedSeriesByName_ReadsBackUnderSqlite()
        {
            using var ctx = new TestDb();
            var repository = new AudiobookRepository(ctx.Db);

            await repository.UpsertCachedSeriesAsync(new SeriesCacheEntry
            {
                SeriesName = "The Stormlight Archive",
                SeriesAsin = "B00INWST01",
                Region = "us"
            });

            var byName = await repository.GetCachedSeriesByNameAsync("The Stormlight Archive", "us");
            var byAsin = await repository.GetCachedSeriesByAsinAsync("B00INWST01", "us");

            Assert.NotNull(byName);
            Assert.Equal("The Stormlight Archive", byName!.SeriesName);
            Assert.NotNull(byAsin);
            Assert.Equal("The Stormlight Archive", byAsin!.SeriesName);
        }

        [Fact]
        public async Task GetCachedSeriesByAsin_OrdersByRecency_WhenLastFetchedAtIsNull()
        {
            using var ctx = new TestDb();
            var db = ctx.Db;
            var repository = new AudiobookRepository(db);

            // Two distinct series rows can legitimately share one ASIN (e.g. two name slugs
            // resolved to the same Audible series), so the by-ASIN read orders by recency to
            // return the freshest. Insert directly to control LastFetchedAt/UpdatedAt — the
            // (SeriesNameNormalized, Region) uniqueness only allows this on differing names.
            db.SeriesCacheEntries.Add(new SeriesCacheEntry
            {
                SeriesNameNormalized = "mistborn",
                SeriesName = "Mistborn",
                SeriesAsin = "B00SHARED1",
                Region = "us",
                LastFetchedAt = DateTime.UtcNow.AddDays(-2),
                CreatedAt = DateTime.UtcNow.AddDays(-2),
                UpdatedAt = DateTime.UtcNow.AddDays(-2)
            });
            // Newer row with no LastFetchedAt: recency must coalesce to UpdatedAt (the branch
            // GetValueOrDefault could not translate), and the query must not throw.
            db.SeriesCacheEntries.Add(new SeriesCacheEntry
            {
                SeriesNameNormalized = "the final empire",
                SeriesName = "The Final Empire (newer)",
                SeriesAsin = "B00SHARED1",
                Region = "us",
                LastFetchedAt = null,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow.AddDays(1)
            });
            await db.SaveChangesAsync();

            var resolved = await repository.GetCachedSeriesByAsinAsync("B00SHARED1", "us");

            Assert.NotNull(resolved);
            Assert.Equal("The Final Empire (newer)", resolved!.SeriesName);
        }
    }
}
