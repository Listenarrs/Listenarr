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
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Persistence
{
    // #639: under a per-cycle cap the sweep must reach every eligible book in turn, so the
    // eligibility query returns never-searched books first and then the least recently
    // searched. Run against migrated SQLite, which is what production orders with.
    [Trait("Name", "AudiobookRepositorySearchOrderTests")]
    [Trait("Category", "Infrastructure")]
    public sealed class AudiobookRepositorySearchOrderTests : BaseTests
    {
        private static readonly DateTime Now = new(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task GetMonitoredAudiobooksForSearchAsync_NeverSearchedFirst_ThenOldestSearch()
        {
            await using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;
            await using var db = new ListenArrDbContext(options);
            await db.Database.MigrateAsync();
            var profile = new QualityProfile { Name = "Any" };
            db.QualityProfiles.Add(profile);
            await db.SaveChangesAsync();

            // Inserted out of the expected order. Without ORDER BY, SQLite can still return this
            // order by scanning the LastSearchTime index, so this test pins the contract on the
            // real provider; the fail-without control is the cycle ordering test.
            var searchedTwoDaysAgo = await AddAsync(db, profile, "The Gods of Mars", Now.AddDays(-2));
            var searchedThreeDaysAgo = await AddAsync(db, profile, "The Warlord of Mars", Now.AddDays(-3));
            var neverSearched = await AddAsync(db, profile, "Pellucidar", null);
            var alsoNeverSearched = await AddAsync(db, profile, "Tanar of Pellucidar", null);
            await AddAsync(db, profile, "A Princess of Mars", Now.AddHours(-1));

            var eligible = await new AudiobookRepository(db)
                .GetMonitoredAudiobooksForSearchAsync(Now.AddHours(-24));

            Assert.Equal(
                new[] { neverSearched, alsoNeverSearched, searchedThreeDaysAgo, searchedTwoDaysAgo },
                eligible.Select(book => book.Id));
        }

        private static async Task<int> AddAsync(ListenArrDbContext db, QualityProfile profile, string title, DateTime? lastSearch)
        {
            var book = new AudiobookBuilder()
                .WithTitle(title)
                .WithAuthor("Edgar Rice Burroughs")
                .WithMonitored()
                .Build();
            book.QualityProfileId = profile.Id;
            book.LastSearchTime = lastSearch;
            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();
            return book.Id;
        }
    }
}
