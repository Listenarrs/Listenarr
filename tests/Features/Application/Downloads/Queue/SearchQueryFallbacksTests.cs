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

namespace Listenarr.Tests.Features.Application.Downloads.Queue
{
    [Trait("Name", "SearchQueryFallbacksTests")]
    [Trait("Category", "Search")]
    public class SearchQueryFallbacksTests : BaseTests
    {
        [Fact]
        public void RetryQueries_TriesTheTitleThenTheTitleWithoutEditionSuffixes()
        {
            var queries = SearchQueryFallbacks.RetryQueries(
                "Red Rising (Part 1 of 2) (Dramatized Adaptation) Pierce Brown",
                "Red Rising (Part 1 of 2) (Dramatized Adaptation)");

            Assert.Equal(new[] { "Red Rising (Part 1 of 2) (Dramatized Adaptation)", "Red Rising" }, queries);
        }

        [Fact]
        public void RetryQueries_SkipsATitleEqualToThePrimaryQuery()
        {
            var queries = SearchQueryFallbacks.RetryQueries("Dune", "Dune");

            Assert.Empty(queries);
        }

        [Fact]
        public void RetryQueries_IsEmptyWithoutATitle()
        {
            var queries = SearchQueryFallbacks.RetryQueries("Dune Frank Herbert", null);

            Assert.Empty(queries);
        }

        [Fact]
        public async Task SearchAsync_RunsThePrimarySearchWhenTheQueryIsEmpty()
        {
            var search = SearchServiceReturningResultsFor();

            var (query, results) = await SearchQueryFallbacks.SearchAsync(search.Object, string.Empty, null);

            Assert.Equal(string.Empty, query);
            Assert.Empty(results);
            VerifySearched(search, string.Empty, null);
            search.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task SearchAsync_DoesNotRetryWhenThePrimarySearchFindsResults()
        {
            var search = SearchServiceReturningResultsFor("Red Rising Pierce Brown");

            var (query, results) = await SearchQueryFallbacks.SearchAsync(search.Object, "Red Rising Pierce Brown", "Red Rising");

            Assert.Equal("Red Rising Pierce Brown", query);
            Assert.Single(results);
            VerifySearched(search, "Red Rising Pierce Brown", null);
            search.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task SearchAsync_RetriesTheTitleInTheAudiobookCategoryAndStopsAtTheFirstResults()
        {
            // Audible credits this book to "Stephen King (alias Richard Bachman)", which no release name contains.
            var search = SearchServiceReturningResultsFor("Marche ou crève");

            var (query, results) = await SearchQueryFallbacks.SearchAsync(
                search.Object, "Marche ou crève Stephen King (alias Richard Bachman)", "Marche ou crève");

            Assert.Equal("Marche ou crève", query);
            Assert.Single(results);
            VerifySearched(search, "Marche ou crève Stephen King (alias Richard Bachman)", null);
            VerifySearched(search, "Marche ou crève", SearchQueryFallbacks.RetryCategory);
            search.VerifyNoOtherCalls();
        }

        private static Mock<ISearchService> SearchServiceReturningResultsFor(params string[] queriesWithResults)
        {
            var search = new Mock<ISearchService>();
            search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<List<string>?>(),
                    It.IsAny<SearchSortBy>(), It.IsAny<SearchSortDirection>(), true))
                .ReturnsAsync((string query, string? _, List<string>? _, SearchSortBy _, SearchSortDirection _, bool _) =>
                    queriesWithResults.Contains(query) ? [new SearchResult { Title = query }] : []);
            return search;
        }

        private static void VerifySearched(Mock<ISearchService> search, string query, string? category)
        {
            search.Verify(s => s.SearchAsync(query, category, It.IsAny<List<string>?>(),
                It.IsAny<SearchSortBy>(), It.IsAny<SearchSortDirection>(), true), Times.Once);
        }
    }
}
