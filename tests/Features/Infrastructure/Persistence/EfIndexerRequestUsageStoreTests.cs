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
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Persistence
{
    // The request-usage store against a migrated SQLite database, so the table, the
    // composite key, the DateTime round trip and the cascade from Indexers are the real ones.
    [Trait("Name", "EfIndexerRequestUsageStoreTests")]
    [Trait("Category", "Infrastructure")]
    public sealed class EfIndexerRequestUsageStoreTests : BaseTests
    {
        private static readonly DateTime TenAm = new(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task SaveAsync_InsertsThenOverwritesTheHourCount()
        {
            await using var database = await MigratedDatabase.CreateAsync();
            var indexerId = await database.AddIndexerAsync();
            var store = new EfIndexerRequestUsageStore(database.Factory);

            await store.SaveAsync(indexerId, TenAm, 1);
            await store.SaveAsync(indexerId, TenAm, 7);
            await store.SaveAsync(indexerId, TenAm.AddHours(1), 2);

            var rows = await store.LoadSinceAsync(TenAm);
            Assert.Equal(2, rows.Count);
            Assert.Equal(7, rows.Single(row => row.HourStartUtc == TenAm).Count);
            Assert.Equal(2, rows.Single(row => row.HourStartUtc == TenAm.AddHours(1)).Count);
        }

        [Fact]
        public async Task LoadSinceAsync_And_DeleteBeforeAsync_UseTheHourBoundary()
        {
            await using var database = await MigratedDatabase.CreateAsync();
            var indexerId = await database.AddIndexerAsync();
            var store = new EfIndexerRequestUsageStore(database.Factory);
            await store.SaveAsync(indexerId, TenAm.AddHours(-1), 3);
            await store.SaveAsync(indexerId, TenAm, 4);

            Assert.Equal(4, Assert.Single(await store.LoadSinceAsync(TenAm)).Count);

            await store.DeleteBeforeAsync(TenAm);
            Assert.Equal(TenAm, Assert.Single(await store.LoadSinceAsync(DateTime.MinValue)).HourStartUtc);
        }

        [Fact]
        public async Task DeletingAnIndexer_RemovesItsUsageRows()
        {
            await using var database = await MigratedDatabase.CreateAsync();
            var indexerId = await database.AddIndexerAsync();
            var store = new EfIndexerRequestUsageStore(database.Factory);
            await store.SaveAsync(indexerId, TenAm, 4);

            await using (var db = database.Factory.CreateDbContext())
            {
                db.Indexers.Remove(await db.Indexers.SingleAsync());
                await db.SaveChangesAsync();
            }

            Assert.Empty(await store.LoadSinceAsync(DateTime.MinValue));
        }

        [Fact]
        public async Task Budget_RebuiltOverTheSameDatabase_KeepsTheDaysUsage()
        {
            await using var database = await MigratedDatabase.CreateAsync();
            var indexer = new Indexer { Id = await database.AddIndexerAsync(), Name = "Usenet", DailyRequestLimit = 8 };
            var clock = new FixedClock(new DateTimeOffset(TenAm.AddMinutes(30)));

            var beforeRestart = new IndexerRequestBudget(new EfIndexerRequestUsageStore(database.Factory), clock, NullLogger<IndexerRequestBudget>.Instance);
            for (var i = 0; i < 6; i++)
            {
                Assert.True(await beforeRestart.TryAcquireAsync(indexer, SearchOrigin.Sweep, 25));
            }
            Assert.False(await beforeRestart.TryAcquireAsync(indexer, SearchOrigin.Sweep, 25));

            var afterRestart = new IndexerRequestBudget(new EfIndexerRequestUsageStore(database.Factory), clock, NullLogger<IndexerRequestBudget>.Instance);

            Assert.False(await afterRestart.TryAcquireAsync(indexer, SearchOrigin.Sweep, 25));
            Assert.True(await afterRestart.TryAcquireAsync(indexer, SearchOrigin.Interactive, 25));
        }

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }

        private sealed class MigratedDatabase : IAsyncDisposable
        {
            private readonly SqliteConnection _connection;

            private MigratedDatabase(SqliteConnection connection, DbContextOptions<ListenArrDbContext> options)
            {
                _connection = connection;
                Factory = new OptionsDbContextFactory(options);
            }

            public IDbContextFactory<ListenArrDbContext> Factory { get; }

            public static async Task<MigratedDatabase> CreateAsync()
            {
                var connection = new SqliteConnection("DataSource=:memory:");
                await connection.OpenAsync();
                var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                    .UseSqlite(connection, sqlite =>
                        sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
                    .Options;
                await using (var db = new ListenArrDbContext(options))
                {
                    await db.Database.MigrateAsync();
                }
                return new MigratedDatabase(connection, options);
            }

            public async Task<int> AddIndexerAsync()
            {
                await using var db = Factory.CreateDbContext();
                var indexer = new Indexer { Name = "Usenet", Type = "Usenet", Implementation = "Newznab", Url = "https://indexer.invalid" };
                db.Indexers.Add(indexer);
                await db.SaveChangesAsync();
                return indexer.Id;
            }

            public ValueTask DisposeAsync() => _connection.DisposeAsync();
        }

        private sealed class OptionsDbContextFactory(DbContextOptions<ListenArrDbContext> options) : IDbContextFactory<ListenArrDbContext>
        {
            public ListenArrDbContext CreateDbContext() => new(options);
        }
    }
}
