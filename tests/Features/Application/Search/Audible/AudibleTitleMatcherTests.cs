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
    [Trait("Name", "AudibleTitleMatcherTests")]
    [Trait("Category", "Search")]
    public class AudibleTitleMatcherTests : BaseTests
    {
        private static readonly AudibleSearchResult DragonsEtSerpents = new()
        {
            Asin = "B077XZC72T",
            Title = "Dragons et serpents",
            Subtitle = "Les cités des Anciens 1",
        };

        [Theory]
        [InlineData("Dragons et serpents")]
        [InlineData("Les cités des Anciens")]
        [InlineData("Dragons et serpents (Les cités des Anciens 1)")]
        [InlineData("Dragons et serpents - Les cités des Anciens 1")]
        [InlineData("Dragons et serpents: Les cites des Anciens 1")]
        [InlineData("DRAGONS ET SERPENTS [Les Cités des Anciens 1]")]
        public void Matches_title_subtitle_and_their_combination(string query)
        {
            Assert.True(AudibleTitleMatcher.Matches(DragonsEtSerpents, query));
        }

        [Theory]
        [InlineData("Le vol des dragons (Les cités des Anciens 7)")]
        [InlineData("Dragons et serpents (Les cités des Anciens 2)")]
        [InlineData("L'apprenti assassin")]
        [InlineData("()")]
        public void Rejects_other_books(string query)
        {
            Assert.False(AudibleTitleMatcher.Matches(DragonsEtSerpents, query));
        }

        [Theory]
        [InlineData("Arsène Lupin contre Herlock Sholmès", "Arsène Lupin 1", "Arsène Lupin contre Herlock Sholmès (Arsène Lupin 1)", true)]
        [InlineData("Arsène Lupin contre Herlock Sholmès", "Arsène Lupin 1", "Arsene Lupin contre Herlock Sholmes - Arsène Lupin 1", true)]
        [InlineData("Arsène Lupin contre Herlock Sholmès", "Arsène Lupin 10", "Arsène Lupin contre Herlock Sholmès (Arsène Lupin 1)", false)]
        [InlineData("Dragons et serpents", "Les cités des Anciens 10", "Dragons et serpents (Les cités des Anciens 1)", false)]
        public void Combined_title_and_subtitle_match_whole_words_only(string title, string subtitle, string query, bool expected)
        {
            var book = new AudibleSearchResult { Asin = "B00U6GTQ6C", Title = title, Subtitle = subtitle };

            Assert.Equal(expected, AudibleTitleMatcher.Matches(book, query));
        }

        [Fact]
        public void Normalizes_case_accents_and_punctuation()
        {
            Assert.Equal("les cites des anciens 1", AudibleTitleMatcher.Normalize("  (Les Cités   des Anciens, 1) "));
        }
    }
}
