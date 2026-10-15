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

namespace Listenarr.Tests.Features.Application.Search.Audible
{
    [Trait("Area", "Search")]
    [Trait("Name", "BookSearchRulesTests")]
    [Trait("Category", "BookSearchRules")]
    public class BookSearchRulesTests : BaseTests
    {
        [Theory]
        [Trait("Method", "LooksLikeAsin")]
        [InlineData("B0BK9W82PQ", true)]
        [InlineData("b0bk9w82pq", true)]
        [InlineData("1980006520", true)]
        [InlineData("198000652X", true)]
        [InlineData("Alchemised", false)]
        [InlineData("Bartimaeus", false)]
        [InlineData("erg", false)]
        [InlineData("", false)]
        public void LooksLikeAsin_OnlyAcceptsAudibleAsins(string value, bool expected)
        {
            Assert.Equal(expected, BookSearchRules.LooksLikeAsin(value));
        }

        [Theory]
        [Trait("Method", "CleanBookName")]
        [InlineData("04 Primal Hunter - Primal Hunter - Primal Hunter, Tome 4 - Zogarth", "Zogarth", "Primal Hunter, Tome 4")]
        [InlineData("04 - Primal Hunter 4 - A LitRPG Adventure (The Primal Hunter)", null, "Primal Hunter 4 A LitRPG Adventure (The Primal Hunter)")]
        [InlineData("Zogarth - Primal Hunter 12", "Zogarth", "Primal Hunter 12")]
        [InlineData("03 - Le Fléau", "Stephen King", "Le Fléau 3")]
        [InlineData("The Land (3)", null, "The Land 3")]
        [InlineData("Carrie [MP3 64kbps]", null, "Carrie")]
        [InlineData("Le_Fleau.Tome_1", null, "Le Fleau Tome 1")]
        [InlineData("not a match", null, "not a match")]
        [InlineData("Jack of Shadows", null, "Jack of Shadows")]
        [InlineData("1984", null, "1984")]
        public void CleanBookName_ExtractsTheBookName(string name, string? author, string expected)
        {
            Assert.Equal(expected, BookSearchRules.CleanBookName(name, author));
        }

        [Fact]
        [Trait("Method", "LibraryImportQuery")]
        public void LibraryImportQuery_UsesTheFolderForSeveralFilesAndTheFileForOne()
        {
            var multi = BookSearchRules.LibraryImportQuery(
                "/in/Zogarth/04 Primal Hunter - Primal Hunter - Primal Hunter, Tome 4 - Zogarth/01.mp3",
                "Zogarth/04 Primal Hunter - Primal Hunter - Primal Hunter, Tome 4 - Zogarth",
                30,
                "Zogarth",
                null);
            Assert.Equal("Primal Hunter, Tome 4", multi.Title);
            Assert.Equal("Zogarth", multi.Author);
            Assert.Null(multi.Asin);

            var single = BookSearchRules.LibraryImportQuery(
                "/in/Zelazny/Jack of Shadows.m4b", "Zelazny", 1, "Roger Zelazny", "b0dqr9d4yg");
            Assert.Equal("Jack of Shadows", single.Title);
            Assert.Equal("B0DQR9D4YG", single.Asin);

            Assert.Null(BookSearchRules.LibraryImportQuery("/in/a/b.mp3", "a", 1, null, "erg").Asin);
        }

        [Theory]
        [Trait("Method", "TitleFits")]
        [InlineData("not a match", "The Perfect Match", null, null, null, false)]
        [InlineData("04 - Primal Hunter 4 - A LitRPG Adventure (The Primal Hunter)", "Primal Hunter 4", null, null, null, true)]
        [InlineData("Primal Hunter 4", "Primal Hunter", "Primal Hunter, Tome 4", null, null, true)]
        [InlineData("Les cites des Anciens 1", "Dragons et serpents", null, "Les Cités des Anciens", "1", true)]
        [InlineData("Primal Hunter 4", "Primal Hunter 7", null, null, null, false)]
        public void TitleFits_RequiresTheTitleToMatchTheSearchedName(
            string searched, string title, string? subtitle, string? series, string? position, bool expected)
        {
            Assert.Equal(expected, BookSearchRules.TitleFits(searched, title, subtitle, new[] { series, position }));
        }

        [Fact]
        [Trait("Method", "PickAutoMatch")]
        public void PickAutoMatch_TakesTheAsinResultOrTheFirstFittingOnePreferringTheTagAuthor()
        {
            var perfect = new BookSearchRules.MatchCandidate("B0CY5SRLQQ", "The Perfect Match", null, Array.Empty<string?>(), "Lyla Mars");
            var other = new BookSearchRules.MatchCandidate("B0OTHER004", "Primal Hunter, Tome 4", null, Array.Empty<string?>(), "Someone");
            var zogarth = new BookSearchRules.MatchCandidate("B0FXXVV42J", "Primal Hunter", "Primal Hunter, Tome 4", Array.Empty<string?>(), "Zogarth");

            Assert.Equal(0, BookSearchRules.PickAutoMatch(new[] { perfect }, new BookSearchQuery { Asin = "B0CY5SRLQQ" }));
            Assert.Equal(-1, BookSearchRules.PickAutoMatch(new[] { perfect }, new BookSearchQuery { Title = "not a match" }));
            Assert.Equal(1, BookSearchRules.PickAutoMatch(new[] { other, zogarth }, new BookSearchQuery { Title = "Primal Hunter, Tome 4", Author = "Zogarth" }));
            Assert.Equal(0, BookSearchRules.PickAutoMatch(new[] { other, zogarth }, new BookSearchQuery { Title = "Primal Hunter, Tome 4" }));
            Assert.Equal(-1, BookSearchRules.PickAutoMatch(Array.Empty<BookSearchRules.MatchCandidate>(), new BookSearchQuery { Title = "x" }));
        }
    }
}
