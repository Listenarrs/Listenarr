using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Search.Indexers.Common
{
    [Trait("Name", "IndexerSearchWorkflowTests")]
    [Trait("Category", "IndexerSearchWorkflow")]
    public class IndexerSearchWorkflowTests : BaseTests
    {
        private static IndexerSearchWorkflow CreateWorkflow(
            IEnumerable<Indexer> enabledIndexers,
            IEnumerable<IIndexerSearchProvider> providers)
        {
            var indexerRepository = new Mock<IIndexerRepository>();
            indexerRepository
                .Setup(r => r.GetEnabledAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(enabledIndexers.ToList());

            var additionalSettingsParser = new IndexerAdditionalSettingsParser(
                Mock.Of<ILogger<IndexerAdditionalSettingsParser>>());

            return new IndexerSearchWorkflow(
                new HttpClient(),
                Mock.Of<IConfigurationService>(),
                indexerRepository.Object,
                providers,
                additionalSettingsParser,
                Mock.Of<ILogger<IndexerSearchWorkflow>>());
        }

        [Fact]
        public async Task SearchIndexersAsync_OneIndexerTimesOut_ReturnsHealthyIndexerResults()
        {
            // Given: two enabled indexers, one whose provider times out (TaskCanceledException)
            // and one that returns results
            var timingOutIndexer = new IndexerBuilder()
                .WithName("SlowIndexer")
                .WithImplementation("Slow")
                .Build();
            var healthyIndexer = new IndexerBuilder()
                .WithName("FastIndexer")
                .WithImplementation("Fast")
                .Build();

            var healthyResult = new IndexerSearchResult
            {
                Id = "healthy-1",
                Title = "The Healthy Result",
                Source = "FastIndexer",
                Seeders = 42
            };

            var providers = new IIndexerSearchProvider[]
            {
                new FakeSearchProvider("Slow", () => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout")),
                new FakeSearchProvider("Fast", () => new List<IndexerSearchResult> { healthyResult })
            };

            var workflow = CreateWorkflow(new[] { timingOutIndexer, healthyIndexer }, providers);

            // When: an automatic search runs across both indexers
            var results = await workflow.SearchIndexersAsync("some query", isAutomaticSearch: true);

            // Then: the healthy indexer's results survive and no exception escapes
            Assert.Single(results);
            Assert.Equal("healthy-1", results[0].Id);
            Assert.Equal("FastIndexer", results[0].Source);
        }

        [Fact]
        public async Task SearchIndexersAsync_AllIndexersTimeOut_ReturnsEmptyWithoutThrowing()
        {
            // Given: every enabled indexer's provider times out
            var indexerA = new IndexerBuilder().WithName("A").WithImplementation("SlowA").Build();
            var indexerB = new IndexerBuilder().WithName("B").WithImplementation("SlowB").Build();

            var providers = new IIndexerSearchProvider[]
            {
                new FakeSearchProvider("SlowA", () => throw new TaskCanceledException()),
                new FakeSearchProvider("SlowB", () => throw new TaskCanceledException())
            };

            var workflow = CreateWorkflow(new[] { indexerA, indexerB }, providers);

            // When: a search runs across both timing-out indexers
            var results = await workflow.SearchIndexersAsync("some query", isAutomaticSearch: true);

            // Then: the timeouts are contained and the search returns an empty list, not an exception
            Assert.Empty(results);
        }

        private sealed class FakeSearchProvider : IIndexerSearchProvider
        {
            private readonly Func<List<IndexerSearchResult>> _behavior;

            public FakeSearchProvider(string indexerType, Func<List<IndexerSearchResult>> behavior)
            {
                IndexerType = indexerType;
                _behavior = behavior;
            }

            public string IndexerType { get; }

            public Task<List<IndexerSearchResult>> SearchAsync(
                Indexer indexer,
                string query,
                string? category = null,
                SearchRequest? request = null)
            {
                return Task.FromResult(_behavior());
            }
        }
    }
}
