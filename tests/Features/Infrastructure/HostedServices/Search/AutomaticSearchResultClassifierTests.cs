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
    [Trait("Name", "AutomaticSearchResultClassifierTests")]
    [Trait("Category", "Infrastructure")]
    public class AutomaticSearchResultClassifierTests : BaseTests
    {
        private readonly AutomaticSearchResultClassifier _classifier =
            new(NullLogger.Instance);

        [Fact]
        public void BuildTitleFallbackSearchQuery_DropsTheAuthor_KeepsTitleAndSeries()
        {
            var book = new AudiobookBuilder()
                .WithTitle("Artemis")
                .WithAuthor("Andy Weir")
                .WithSeries("Standalone")
                .Build();

            var full = _classifier.BuildSearchQuery(book);
            var fallback = _classifier.BuildTitleFallbackSearchQuery(book);

            // The full query carries the author; the fallback drops it but keeps title + series.
            Assert.Contains("Andy Weir", full);
            Assert.DoesNotContain("Andy Weir", fallback);
            Assert.Contains("Artemis", fallback);
            Assert.Contains("Standalone", fallback);
        }

        [Fact]
        public void ConstrainFallbackResultsToTitle_KeepsExactTitleWithMatchingAuthor()
        {
            var book = new AudiobookBuilder()
                .WithTitle("Artemis")
                .WithAuthor("Andy Weir")
                .Build();
            var results = new List<SearchResult>
            {
                new SearchResult { Title = "Andy Weir - Artemis - 2017 - M4B" }, // this book
                new SearchResult { Title = "Andy Weir - The Martian - 2014" },   // a different book
            };

            var kept = _classifier.ConstrainFallbackResultsToTitle(results, book);

            Assert.Single(kept);
            Assert.Equal("Andy Weir - Artemis - 2017 - M4B", kept[0].Title);
        }

        [Fact]
        public void ConstrainFallbackResultsToTitle_RejectsSameWordDifferentTitle()
        {
            // "Artemis Fowl" must never be grabbed for "Artemis".
            var book = new AudiobookBuilder()
                .WithTitle("Artemis")
                .WithAuthor("Andy Weir")
                .Build();
            var results = new List<SearchResult>
            {
                new SearchResult { Title = "Eoin Colfer - Artemis Fowl - 2009" },
            };

            Assert.Empty(_classifier.ConstrainFallbackResultsToTitle(results, book));
        }

        [Fact]
        public void ConstrainFallbackResultsToTitle_RejectsSameTitleDifferentAuthor()
        {
            // A different author's book of the same name must not be grabbed.
            var book = new AudiobookBuilder()
                .WithTitle("Revival")
                .WithAuthor("Stephen King")
                .Build();
            var results = new List<SearchResult>
            {
                new SearchResult { Title = "Chris Weitz - Revival" },
            };

            Assert.Empty(_classifier.ConstrainFallbackResultsToTitle(results, book));
        }

        [Fact]
        public void ConstrainFallbackResultsToTitle_SkipsUncorroboratedAuthorlessResult()
        {
            // Exact title but no author / artist / ASIN evidence — skipped rather than grabbed.
            var book = new AudiobookBuilder()
                .WithTitle("Artemis")
                .WithAuthor("Andy Weir")
                .Build();
            var results = new List<SearchResult>
            {
                new SearchResult { Title = "Artemis" },
            };

            Assert.Empty(_classifier.ConstrainFallbackResultsToTitle(results, book));
        }

        [Fact]
        public void ConstrainFallbackResultsToTitle_KeepsWhenArtistFieldCorroborates()
        {
            var book = new AudiobookBuilder()
                .WithTitle("Artemis")
                .WithAuthor("Andy Weir")
                .Build();
            var results = new List<SearchResult>
            {
                new SearchResult { Title = "Artemis", Artist = "Andy Weir" },
            };

            Assert.Single(_classifier.ConstrainFallbackResultsToTitle(results, book));
        }

        [Fact]
        public void ConstrainFallbackResultsToTitle_EmptyInput_ReturnsEmpty()
        {
            var book = new AudiobookBuilder().WithTitle("Artemis").Build();

            Assert.Empty(_classifier.ConstrainFallbackResultsToTitle(
                new List<SearchResult>(), book));
        }
    }
}
