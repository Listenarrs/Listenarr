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

using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.HostedServices.Search
{
    // #639: the sweep stops at a per-cycle book cap and at each indexer's unattended share of
    // its daily request limit, and the reserve is still there for a search a person starts.
    // These run the real cycle, search service and indexer workflow against a counting provider.
    [Trait("Name", "AutomaticSearchCycleLimitTests")]
    [Trait("Category", "Infrastructure")]
    public sealed class AutomaticSearchCycleLimitTests : BaseTests
    {
        private static readonly (string Title, string Author)[] Books =
        {
            ("A Princess of Mars", "Edgar Rice Burroughs"),
            ("The Wonderful Wizard of Oz", "L. Frank Baum"),
            ("The Three Musketeers", "Alexandre Dumas"),
            ("The Jungle Book", "Rudyard Kipling"),
            ("Little Women", "Louisa May Alcott")
        };

        [Fact]
        public async Task SweepSpendsItsShare_ThenStops_AndAnInteractiveSearchStillGetsThroughOnTheReserve()
        {
            // Limit 7 with the 25% reserve: the sweep may send floor(5.25) = 5, a person all 7.
            var provider = new CountingProvider();
            Init(builder => builder.WithSingleton<IIndexerSearchProvider>(provider));
            await ConfigureAsync(maxBooksPerCycle: 0, reservePercent: 25);
            await AddIndexerAsync(dailyRequestLimit: 7);
            var books = await SeedBooksAsync();

            await RunCycleAsync();

            // A book with no results costs two requests (full query, then the author-free
            // fallback). Books one and two spend four; book three's full query is the fifth and
            // its fallback is refused as a sweep request; the cycle then stops before book four.
            Assert.Equal(5, provider.Calls);
            Assert.Equal(3, await CountStampedAsync(books));

            using (var scope = _provider.CreateScope())
            {
                var search = scope.ServiceProvider.GetRequiredService<ISearchService>();
                await search.SearchIndexersAsync("Tarzan of the Apes Edgar Rice Burroughs");
                await search.SearchIndexersAsync("The Return of Tarzan Edgar Rice Burroughs");
                Assert.Equal(7, provider.Calls);

                await search.SearchIndexersAsync("Pellucidar Edgar Rice Burroughs");
                Assert.Equal(7, provider.Calls);
            }
        }

        [Fact]
        public async Task SweepStoppedByTheBudget_LeavesTheRestUnstampedForTheNextCycle()
        {
            var provider = new CountingProvider();
            Init(builder => builder.WithSingleton<IIndexerSearchProvider>(provider));
            await ConfigureAsync(maxBooksPerCycle: 0, reservePercent: 25);
            await AddIndexerAsync(dailyRequestLimit: 8);
            var books = await SeedBooksAsync();

            await RunCycleAsync();
            await RunCycleAsync();

            // The second cycle finds no headroom before its first book and sends nothing.
            Assert.Equal(6, provider.Calls);
            Assert.Equal(3, await CountStampedAsync(books));
        }

        [Fact]
        public async Task SweepRequests_StopAtTheShare_WhileAnUnbudgetedIndexerKeepsTheCycleGoing()
        {
            // The budgeted indexer (limit 4, sweep share 3) runs out part-way through; the
            // unbudgeted one keeps the cycle going, and every later sweep request, full query
            // and fallback alike, is refused on the budgeted one.
            var provider = new CountingProvider();
            Init(builder => builder.WithSingleton<IIndexerSearchProvider>(provider));
            await ConfigureAsync(maxBooksPerCycle: 0, reservePercent: 25);
            await AddIndexerAsync(dailyRequestLimit: 4, name: "Budgeted");
            await AddIndexerAsync(dailyRequestLimit: null, name: "Unbudgeted");
            var books = await SeedBooksAsync();

            await RunCycleAsync();

            Assert.Equal(3, provider.CallsTo("Budgeted"));
            Assert.Equal(10, provider.CallsTo("Unbudgeted"));
            Assert.Equal(books.Count, await CountStampedAsync(books));
        }

        [Fact]
        public async Task IndexerWithoutALimit_KeepsTheSweepGoing()
        {
            var provider = new CountingProvider();
            Init(builder => builder.WithSingleton<IIndexerSearchProvider>(provider));
            await ConfigureAsync(maxBooksPerCycle: 0, reservePercent: 25);
            await AddIndexerAsync(dailyRequestLimit: null);
            var books = await SeedBooksAsync();

            await RunCycleAsync();

            Assert.Equal(10, provider.Calls);
            Assert.Equal(books.Count, await CountStampedAsync(books));
        }

        [Fact]
        public async Task PerCycleCap_CountsOnlyBooksThatWereSearched()
        {
            var provider = new CountingProvider();
            Init(builder => builder.WithSingleton<IIndexerSearchProvider>(provider));
            await ConfigureAsync(maxBooksPerCycle: 2, reservePercent: 25);
            await AddIndexerAsync(dailyRequestLimit: null);
            var books = await SeedBooksAsync();
            // The first book already has a download in flight, so it is skipped without a search
            // and must not use up one of the two slots.
            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobookId(books[0].Id)
                .WithStatus(DownloadStatus.Downloading)
                .Build());

            await RunCycleAsync();

            Assert.Equal(4, provider.Calls);
            Assert.Equal(3, await CountStampedAsync(books));
        }

        [Fact]
        public async Task PerCycleCap_TakesNeverSearchedBooksFirst_ThenTheLeastRecentlySearched()
        {
            var provider = new CountingProvider();
            Init(builder => builder.WithSingleton<IIndexerSearchProvider>(provider));
            await ConfigureAsync(maxBooksPerCycle: 2, reservePercent: 25);
            await AddIndexerAsync(dailyRequestLimit: null);
            var books = await SeedBooksAsync();
            var twoDaysAgo = DateTime.UtcNow.AddDays(-2);
            var threeDaysAgo = DateTime.UtcNow.AddDays(-3);
            // Table order is books[0..4]; give the front of the table the most recent searches.
            await SetLastSearchAsync(books[0], twoDaysAgo);
            await SetLastSearchAsync(books[1], threeDaysAgo);
            await SetLastSearchAsync(books[2], twoDaysAgo);
            await SetLastSearchAsync(books[3], twoDaysAgo);

            await RunCycleAsync();

            // books[4] was never searched and books[1] is the oldest search: those two go first.
            var searchedNow = await SearchedSinceAsync(books, DateTime.UtcNow.AddMinutes(-5));
            Assert.Equal(new[] { books[1].Id, books[4].Id }, searchedNow.OrderBy(id => id));
        }

        private async Task SetLastSearchAsync(Audiobook book, DateTime lastSearch)
        {
            book.LastSearchTime = lastSearch;
            await _audiobookRepository.UpdateAsync(book);
        }

        private async Task<List<int>> SearchedSinceAsync(IEnumerable<Audiobook> books, DateTime since)
        {
            var ids = new List<int>();
            foreach (var book in books)
            {
                using var scope = _provider.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
                if ((await repository.GetByIdAsync(book.Id))?.LastSearchTime > since)
                    ids.Add(book.Id);
            }
            return ids;
        }

        private async Task ConfigureAsync(int maxBooksPerCycle, int reservePercent)
        {
            var settings = await _applicationSettingsRepository.GetAsync() ?? new ApplicationSettings();
            settings.AutomaticSearchBookDelaySeconds = 0;
            settings.AutomaticSearchMaxBooksPerCycle = maxBooksPerCycle;
            settings.IndexerInteractiveReservePercent = reservePercent;
            await _applicationSettingsRepository.SaveAsync(settings);
        }

        private async Task AddIndexerAsync(int? dailyRequestLimit, string name = "Usenet") =>
            await _indexerRepository.AddAsync(new IndexerBuilder()
                .WithName(name)
                .WithType("Usenet")
                .WithImplementation(CountingProvider.Implementation)
                .WithUrl("https://indexer.invalid")
                .WithEnabled()
                .WithInteractiveSearch()
                .WithDailyRequestLimit(dailyRequestLimit)
                .Build());

        private async Task<List<Audiobook>> SeedBooksAsync()
        {
            var seeded = new List<Audiobook>();
            foreach (var (title, author) in Books)
            {
                seeded.Add(await _audiobookRepository.AddAsync(new AudiobookBuilder()
                    .WithTitle(title)
                    .WithAuthor(author)
                    .WithMonitored()
                    .WithQualityProfile(new QualityProfile { Name = "Any" })
                    .Build()));
            }
            return seeded;
        }

        private async Task<int> CountStampedAsync(IEnumerable<Audiobook> books)
        {
            var stamped = 0;
            foreach (var book in books)
            {
                using var scope = _provider.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
                if ((await repository.GetByIdAsync(book.Id))?.LastSearchTime != null)
                    stamped++;
            }
            return stamped;
        }

        private async Task RunCycleAsync()
        {
            var processor = new AutomaticSearchProcessor(
                NullLogger<AutomaticSearchProcessor>.Instance,
                _provider.GetRequiredService<IServiceScopeFactory>());
            await processor.RunCycleAsync(CancellationToken.None);
        }

        private sealed class CountingProvider : IIndexerSearchProvider
        {
            public const string Implementation = "CountingTestIndexer";
            private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _callsByIndexer = new();

            public string IndexerType => Implementation;

            public int Calls => _callsByIndexer.Values.Sum();

            public int CallsTo(string indexerName) => _callsByIndexer.GetValueOrDefault(indexerName);

            public Task<List<IndexerSearchResult>> SearchAsync(Indexer indexer, string query, string? category = null, SearchRequest? request = null)
            {
                _callsByIndexer.AddOrUpdate(indexer.Name, 1, (_, calls) => calls + 1);
                return Task.FromResult(new List<IndexerSearchResult>());
            }
        }
    }
}
