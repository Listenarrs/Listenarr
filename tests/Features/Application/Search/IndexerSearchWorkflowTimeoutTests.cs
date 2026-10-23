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

using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Search
{
    /// <summary>
    /// An HttpClient.Timeout expiry is a TaskCanceledException — the one exception type the
    /// repository's catch clauses deliberately let through. Before the classifier, a single
    /// slow indexer aborted the whole fan-out (live: a 60s TorrentDownload answer turned a
    /// Wanted-page search into a 500 and discarded three indexers' results).
    /// </summary>
    [Trait("Name", "IndexerSearchWorkflowTimeoutTests")]
    [Trait("Category", "Search")]
    public class IndexerSearchWorkflowTimeoutTests : BaseTests
    {
        private sealed class StubProvider : IIndexerSearchProvider
        {
            public string IndexerType => "Torznab";

            public Task<List<IndexerSearchResult>> SearchAsync(Indexer indexer, string query, string? category = null, SearchRequest? request = null)
            {
                return indexer.Name switch
                {
                    "Slow" => throw new TaskCanceledException(
                        "The request was canceled due to the configured HttpClient.Timeout of 60 seconds elapsing.",
                        new TimeoutException()),
                    "Cancelled" => throw new OperationCanceledException(),
                    _ => Task.FromResult(new List<IndexerSearchResult>
                    {
                        new() { Title = $"{indexer.Name} hit", Source = indexer.Name }
                    })
                };
            }
        }

        private sealed class HangingHandler : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("unreachable");
            }
        }

        private static Indexer TorznabIndexer(string name) => new()
        {
            Id = name.Length,
            Name = name,
            Type = "Torrent",
            Implementation = "Torznab",
            Url = "http://localhost:9/api",
            IsEnabled = true
        };

        private static IndexerSearchWorkflow CreateWorkflow(params Indexer[] enabled)
        {
            var repository = new Mock<IIndexerRepository>();
            repository
                .Setup(r => r.GetEnabledAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(enabled.ToList());

            return new IndexerSearchWorkflow(
                new HttpClient(),
                Mock.Of<IConfigurationService>(),
                repository.Object,
                new IIndexerSearchProvider[] { new StubProvider() },
                new IndexerAdditionalSettingsParser(NullLogger<IndexerAdditionalSettingsParser>.Instance),
                NullLogger<IndexerSearchWorkflow>.Instance);
        }

        [Fact]
        public async Task SearchIndexersAsync_IndexerHttpTimeout_KeepsTheOtherIndexersResults()
        {
            var workflow = CreateWorkflow(TorznabIndexer("Slow"), TorznabIndexer("Fast"));

            var results = await workflow.SearchIndexersAsync("some book", isAutomaticSearch: true);

            var only = Assert.Single(results);
            Assert.Equal("Fast", only.Source);
        }

        [Fact]
        public async Task SearchIndexersAsync_CooperativeCancellation_StillPropagates()
        {
            var workflow = CreateWorkflow(TorznabIndexer("Cancelled"), TorznabIndexer("Fast"));

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => workflow.SearchIndexersAsync("some book", isAutomaticSearch: true));
        }

        [Fact]
        public async Task TorznabProvider_HttpClientTimeout_ReturnsNoResultsInsteadOfThrowing()
        {
            using var httpClient = new HttpClient(new HangingHandler()) { Timeout = TimeSpan.FromMilliseconds(100) };
            var provider = new TorznabNewznabSearchProvider(httpClient, NullLogger<TorznabNewznabSearchProvider>.Instance);

            var results = await provider.SearchAsync(TorznabIndexer("Slow"), "some book", "3030");

            Assert.Empty(results);
        }

        [Fact]
        public void HttpClientTimeoutClassifier_OnlyMatchesTheClientTimeoutShape()
        {
            Assert.True(HttpClientTimeoutClassifier.IsHttpClientTimeout(new TaskCanceledException("t", new TimeoutException())));
            Assert.False(HttpClientTimeoutClassifier.IsHttpClientTimeout(new TaskCanceledException("t")));
            Assert.False(HttpClientTimeoutClassifier.IsHttpClientTimeout(new OperationCanceledException()));
            Assert.False(HttpClientTimeoutClassifier.IsHttpClientTimeout(new TimeoutException()));
        }
    }
}
