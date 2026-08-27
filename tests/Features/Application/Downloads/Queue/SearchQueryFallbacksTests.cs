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
namespace Listenarr.Tests.Features.Application.Downloads.Queue
{
    [Trait("Area", "Search")]
    public class SearchQueryFallbacksTests
    {
        [Fact]
        public void Expand_KeepsCombinedQueryFirst()
        {
            var queries = SearchQueryFallbacks.Expand("Red Rising Pierce Brown", "Red Rising");

            Assert.Equal("Red Rising Pierce Brown", queries[0]);
        }

        [Fact]
        public void Expand_FallsBackToTitleOnly()
        {
            var queries = SearchQueryFallbacks.Expand("Project Hail Mary Andy Weir", "Project Hail Mary");

            Assert.Contains("Project Hail Mary", queries);
        }

        [Fact]
        public void Expand_FallsBackToTitleWithoutEditionSuffix()
        {
            var queries = SearchQueryFallbacks.Expand(
                "Red Rising (Part 1 of 2) (Dramatized Adaptation) Pierce Brown",
                "Red Rising (Part 1 of 2) (Dramatized Adaptation)");

            Assert.Equal("Red Rising", queries[^1]);
        }

        [Fact]
        public void Expand_DoesNotRepeatIdenticalQueries()
        {
            var queries = SearchQueryFallbacks.Expand("Dune", "Dune");

            Assert.Single(queries);
        }

        [Fact]
        public void Expand_IgnoresMissingTitle()
        {
            var queries = SearchQueryFallbacks.Expand("Dune Frank Herbert", null);

            Assert.Equal(new[] { "Dune Frank Herbert" }, queries);
        }
    }
}
