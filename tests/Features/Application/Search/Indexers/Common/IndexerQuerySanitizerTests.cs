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

using Listenarr.Application.Search.Indexers.Common;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Search.Indexers.Common
{
    [Trait("Name", "IndexerQuerySanitizerTests")]
    [Trait("Category", "Application")]
    public class IndexerQuerySanitizerTests : BaseTests
    {
        [Theory]
        // Hyphens/dashes become spaces so a Newznab indexer does not read "-Cast" as an
        // exclusion operator (issue #1068).
        [InlineData("Full-Cast", "Full Cast")]
        [InlineData("Spider-Man", "Spider Man")]
        [InlineData("Harry Potter and the Philosopher's Stone (Full-Cast Edition)",
            "Harry Potter and the Philosophers Stone Full Cast Edition")]
        // A leading hyphen must not survive to the first token.
        [InlineData("-Cast Edition", "Cast Edition")]
        [InlineData("Full--Cast", "Full Cast")]
        // En dash / em dash / minus sign are treated the same way.
        [InlineData("Title – Subtitle", "Title Subtitle")]
        [InlineData("X—Men", "X Men")]
        // Existing behaviour: forbidden operator characters are dropped, quotes removed,
        // whitespace collapsed.
        [InlineData("A&B+C", "ABC")]
        [InlineData("X-Men: Days", "X Men Days")]
        [InlineData("  spaced   out  ", "spaced out")]
        public void Sanitize_NormalizesQuery(string input, string expected)
        {
            Assert.Equal(expected, IndexerQuerySanitizer.Sanitize(input));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Sanitize_BlankInput_ReturnsEmpty(string? input)
        {
            Assert.Equal(string.Empty, IndexerQuerySanitizer.Sanitize(input!));
        }
    }
}
