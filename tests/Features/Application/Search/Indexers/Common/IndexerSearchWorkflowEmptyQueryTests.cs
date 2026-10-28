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

namespace Listenarr.Tests.Features.Application.Search.Indexers.Common
{
    [Trait("Name", "IndexerSearchWorkflowEmptyQueryTests")]
    [Trait("Category", "Application")]
    public class IndexerSearchWorkflowEmptyQueryTests : BaseTests
    {
        [Theory]
        [InlineData("-")]        // dash-only
        [InlineData("- - -")]    // dashes collapse to nothing
        [InlineData("()")]       // punctuation-only
        [InlineData("   ")]      // whitespace-only
        public async Task SearchIndexerResultsAsync_QueryEmptyAfterSanitization_SkipsProviderAndReturnsNothing(
            string query)
        {
            var indexer = new Indexer
            {
                Id = 5,
                Name = "NZBgeek",
                Implementation = "Newznab",
                IsEnabled = true
            };

            var indexerRepo = new Mock<IIndexerRepository>();
            indexerRepo
                .Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>()))
                .ReturnsAsync(indexer);

            var provider = new Mock<IIndexerSearchProvider>();
            provider.SetupGet(p => p.IndexerType).Returns("Newznab");
            // An unrelated release the indexer would hand back for an empty "browse" query.
            // It must never surface, because the provider must never be called.
            provider
                .Setup(p => p.SearchAsync(
                    It.IsAny<Indexer>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<SearchRequest?>()))
                .ReturnsAsync(new List<IndexerSearchResult>
                {
                    new IndexerSearchResult { Title = "Unrelated Release" }
                });

            using var httpClient = new HttpClient();
            var workflow = new IndexerSearchWorkflow(
                httpClient,
                new Mock<IConfigurationService>().Object,
                indexerRepo.Object,
                new[] { provider.Object },
                new IndexerAdditionalSettingsParser(
                    NullLogger<IndexerAdditionalSettingsParser>.Instance),
                NullLogger<IndexerSearchWorkflow>.Instance);

            var results = await workflow.SearchIndexerResultsAsync("5", query);

            Assert.Empty(results);
            provider.Verify(
                p => p.SearchAsync(
                    It.IsAny<Indexer>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<SearchRequest?>()),
                Times.Never);
        }
    }
}
