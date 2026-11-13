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

namespace Listenarr.Tests.Features.Application.Search.Indexers.Common
{
    // The budget is enforced at the one place a provider request is sent, so every public
    // entry point is charged against the same per-indexer count, and the fail-closed returns
    // before that point never spend it.
    [Trait("Name", "IndexerSearchWorkflowBudgetTests")]
    [Trait("Category", "IndexerSearchWorkflow")]
    public sealed class IndexerSearchWorkflowBudgetTests : BaseTests
    {
        [Fact]
        public async Task EveryEntryPoint_IsChargedAgainstTheSameIndexerBudget()
        {
            var provider = new CountingProvider("Counting");
            var indexer = BudgetedIndexer(1, "Counting", limit: 3);
            var workflow = CreateWorkflow(new[] { indexer }, provider, CreateBudget());

            await workflow.SearchIndexersAsync("The Gods of Mars");
            await workflow.SearchByApiAsync("1", "The Gods of Mars");
            await workflow.SearchIndexerResultsAsync("1", "The Gods of Mars");
            await workflow.SearchIndexersAsync("The Gods of Mars");
            await workflow.SearchByApiAsync("1", "The Gods of Mars");

            Assert.Equal(3, provider.Calls);
        }

        [Fact]
        public async Task SweepOrigin_StopsAtItsShare_InteractiveStillReachesTheIndexer()
        {
            var provider = new CountingProvider("Counting");
            var indexer = BudgetedIndexer(1, "Counting", limit: 4);
            var workflow = CreateWorkflow(new[] { indexer }, provider, CreateBudget(), reservePercent: 25);

            for (var i = 0; i < 5; i++)
            {
                await workflow.SearchIndexersAsync("The Warlord of Mars", isAutomaticSearch: true, origin: SearchOrigin.Sweep);
            }
            Assert.Equal(3, provider.Calls);

            await workflow.SearchIndexersAsync("The Warlord of Mars");
            await workflow.SearchIndexersAsync("The Warlord of Mars");
            Assert.Equal(4, provider.Calls);
        }

        [Fact]
        public async Task FailClosedPaths_SpendNothing()
        {
            var budget = CreateBudget();
            var provider = new CountingProvider("Counting");
            var indexer = BudgetedIndexer(1, "Counting", limit: 1);
            var unsupported = BudgetedIndexer(2, "NoSuchProvider", limit: 1);
            var workflow = CreateWorkflow(new[] { indexer, unsupported }, provider, budget);

            await workflow.SearchIndexersAsync("---");
            Assert.Equal(0, provider.Calls);

            await workflow.SearchIndexersAsync("Pellucidar");
            Assert.Equal(1, provider.Calls);
            Assert.True(await budget.TryAcquireAsync(unsupported, SearchOrigin.Interactive, 25));
        }

        private static IndexerRequestBudget CreateBudget()
        {
            var store = new Mock<IIndexerRequestUsageStore>();
            store.Setup(s => s.LoadSinceAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<IndexerRequestUsage>());
            return new IndexerRequestBudget(store.Object, TimeProvider.System, NullLogger<IndexerRequestBudget>.Instance);
        }

        private static Indexer BudgetedIndexer(int id, string implementation, int limit) =>
            new IndexerBuilder()
                .WithId(id)
                .WithName($"Indexer {id}")
                .WithImplementation(implementation)
                .WithEnabled()
                .WithInteractiveSearch()
                .WithDailyRequestLimit(limit)
                .Build();

        private static IndexerSearchWorkflow CreateWorkflow(
            IReadOnlyList<Indexer> indexers,
            IIndexerSearchProvider provider,
            IIndexerRequestBudget budget,
            int reservePercent = 25)
        {
            var indexerRepository = new Mock<IIndexerRepository>();
            indexerRepository
                .Setup(r => r.GetEnabledAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(indexers.ToList());
            indexerRepository
                .Setup(r => r.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int id, CancellationToken _) => indexers.FirstOrDefault(indexer => indexer.Id == id));
            var configurationService = new Mock<IConfigurationService>();
            configurationService
                .Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings { IndexerInteractiveReservePercent = reservePercent });

            return new IndexerSearchWorkflow(
                new HttpClient(),
                configurationService.Object,
                indexerRepository.Object,
                new[] { provider },
                new IndexerAdditionalSettingsParser(NullLogger<IndexerAdditionalSettingsParser>.Instance),
                NullLogger<IndexerSearchWorkflow>.Instance,
                requestBudget: budget);
        }

        private sealed class CountingProvider(string indexerType) : IIndexerSearchProvider
        {
            private int _calls;

            public string IndexerType { get; } = indexerType;

            public int Calls => Volatile.Read(ref _calls);

            public Task<List<IndexerSearchResult>> SearchAsync(Indexer indexer, string query, string? category = null, SearchRequest? request = null)
            {
                Interlocked.Increment(ref _calls);
                return Task.FromResult(new List<IndexerSearchResult>());
            }
        }
    }
}
