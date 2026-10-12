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

namespace Listenarr.Tests.Features.Application.Audiobooks.Quality
{
    /// <summary>
    /// Direct tests for the published-date parse the scorer's age gates depend on.
    /// </summary>
    /// <remarks>
    /// These assert on the parse rather than on a score, because a test that drives the whole
    /// scorer cannot tell the fix from the bug on a host whose local time is UTC. The bug was
    /// DateTime.TryParse returning the host's local time with Kind=Local, and on a UTC host the
    /// local time and the UTC time are the same instant. CI runs on UTC images, so a test that
    /// only checks the instant would pass with the fix reverted. The Kind differs on every host,
    /// which is why it is asserted here alongside the instant.
    /// </remarks>
    [Trait("Name", "SearchResultScorerPublishedDateTests")]
    [Trait("Category", "Scoring")]
    public class SearchResultScorerPublishedDateTests : BaseTests
    {
        [Theory]
        [InlineData("2026-03-01T09:00:00+09:00")]
        [InlineData("2026-03-01T00:00:00Z")]
        [InlineData("2026-03-01T00:00:00")]
        public void TryParsePublishedDateUtc_Returns_The_Same_Instant_Marked_Utc(string published)
        {
            // All three name the same moment: an offset that has to be subtracted, an explicit
            // Z, and a bare local-looking string an indexer sends meaning UTC.
            var parsed = SearchResultScorer.TryParsePublishedDateUtc(published, out var publishedUtc);

            Assert.True(parsed);
            Assert.Equal(DateTimeKind.Utc, publishedUtc.Kind);
            Assert.Equal(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), publishedUtc);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not a date")]
        public void TryParsePublishedDateUtc_Returns_False_When_There_Is_Nothing_To_Read(string? published)
        {
            var parsed = SearchResultScorer.TryParsePublishedDateUtc(published, out var publishedUtc);

            Assert.False(parsed);
            Assert.Equal(default, publishedUtc);
        }
    }
}
