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

namespace Listenarr.Tests.Features.Application.Search.Scoring
{
    [Trait("Area", "Search")]
    [Trait("Name", "SeriesPositionMismatchRuleTests")]
    [Trait("Category", "Scoring")]
    public class SeriesPositionMismatchRuleTests : BaseTests
    {
        private static Audiobook Book(string? series, string? number, params AudiobookSeriesMembership[] memberships) => new()
        {
            Title = "Primal Hunter",
            Series = series,
            SeriesNumber = number,
            SeriesMemberships = memberships.ToList()
        };

        private static QualityScore Score(string title) => new() { SearchResult = new SearchResult { Title = title }, TotalScore = 90 };

        private static bool IsRejectedFor(Audiobook book, string title)
        {
            var score = Score(title);
            SeriesPositionMismatchRule.Apply(book, new[] { score });
            return score.IsRejected;
        }

        [Theory]
        [InlineData("Zogarth.-.Primal.Hunter.-.Tome.05.m4b")]
        [InlineData("Primal.Hunter.T05.Zogarth.2026.FRENCH.[M4B.128.kbps]-SGFX")]
        [InlineData("Zogarth - Primal Hunter T03 - 2025 [MP3 à 64 kb/s]")]
        [InlineData("Zogarth - Primal Hunter 1 [2024] [mp3 à 128 Kb/s]")]
        [InlineData("Primal Hunter, Book 4")]
        [InlineData("Primal Hunter Vol. 2")]
        [InlineData("Primal Hunter #7")]
        [InlineData("Primal Hunter Tomes 1 à 3")]
        [InlineData("Primal.Hunter.T01-T05.Zogarth")]
        public void Rejects_release_naming_another_book_of_the_series(string title)
        {
            Assert.True(IsRejectedFor(Book("Primal Hunter", "6"), title));
        }

        [Theory]
        [InlineData("Primal.Hunter.T06.Zogarth.2026.FRENCH.[M4B.64.kbps]-SGFX")]
        [InlineData("Zogarth.-.Primal.Hunter.-.Tome.06.m4b")]
        [InlineData("Primal Hunter 6 - Zogarth")]
        [InlineData("Primal Hunter Book 6 (Unabridged)")]
        [InlineData("Primal Hunter Tomes 4 à 6")]
        [InlineData("Primal.Hunter.T01-T08.Zogarth")]
        [InlineData("Primal Hunter Zogarth [INTEGRALE]")]
        [InlineData("Primal Hunter Zogarth 2026 FR M4B 64 kbps")]
        [InlineData("Zogarth - Primal Hunter AUDIOBOOK 128")]
        public void Keeps_release_matching_the_book_or_without_a_position(string title)
        {
            Assert.False(IsRejectedFor(Book("Primal Hunter", "6"), title));
        }

        [Fact]
        public void Keeps_everything_when_the_audiobook_has_no_series_position()
        {
            Assert.False(IsRejectedFor(Book("Primal Hunter", null), "Primal.Hunter.T05.Zogarth"));
            Assert.False(IsRejectedFor(Book(null, ""), "Primal.Hunter.T05.Zogarth"));
        }

        [Fact]
        public void Accepts_a_position_from_any_series_membership()
        {
            var book = Book("Cosmere", "5", new AudiobookSeriesMembership { SeriesName = "The Stormlight Archive", SeriesNumber = "1" });

            Assert.False(IsRejectedFor(book, "The Stormlight Archive 1 - The Way of Kings"));
            Assert.False(IsRejectedFor(book, "Cosmere Book 5"));
            Assert.True(IsRejectedFor(book, "The Stormlight Archive Book 2"));
        }

        [Fact]
        public void Compares_fractional_positions()
        {
            Assert.False(IsRejectedFor(Book("Primal Hunter", "1.5"), "Primal Hunter T1.5"));
            Assert.True(IsRejectedFor(Book("Primal Hunter", "1.5"), "Primal Hunter T1"));
        }

        [Fact]
        public void Does_not_read_years_bitrates_or_words_containing_a_keyword_as_positions()
        {
            var positions = SeriesPositionMismatchRule.ParsePositions(
                "Primal Hunter 2026 AUDIOBOOK M4B 128 kbps Part 2 Bookworm 3",
                new[] { "Primal Hunter" });

            Assert.Empty(positions);
        }

        [Fact]
        public void Gives_a_readable_rejection_reason()
        {
            var score = Score("Zogarth.-.Primal.Hunter.-.Tome.05.m4b");
            SeriesPositionMismatchRule.Apply(Book("Primal Hunter", "6"), new[] { score });

            Assert.Equal("Release is book 5 of the series, not book 6", Assert.Single(score.RejectionReasons));
        }
    }
}
