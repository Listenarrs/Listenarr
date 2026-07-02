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
using Listenarr.Application.Audiobooks.Verification;

namespace Listenarr.Tests.Features.Application.Audiobooks.Verification
{
    public class TranscriptMatcherTests
    {
        private const string HailMaryCredits =
            "Audible presents Project Hail Mary by Andy Weir narrated by Ray Porter";

        [Fact]
        public void Tokenize_LowercasesAndCanonicalizesNumbers()
        {
            var tokens = TranscriptMatcher.Tokenize("Book Two: The Wise Man's Fear, chapter 10");

            Assert.Contains("2", tokens);     // "two" → "2"
            Assert.Contains("man's", tokens); // inner apostrophe survives
            Assert.Contains("10", tokens);
            Assert.DoesNotContain("Two", tokens);
        }

        [Fact]
        public void MatchTitle_ExactSpokenTitle_ScoresHigh()
        {
            var tokens = TranscriptMatcher.Tokenize(HailMaryCredits);

            var match = TranscriptMatcher.MatchTitle(tokens, "Project Hail Mary");

            Assert.NotNull(match);
            Assert.True(match!.Score >= 0.95, $"expected ~1.0, got {match.Score}");
            Assert.Equal("project hail mary", match.MatchedText);
        }

        [Fact]
        public void MatchTitle_UnrelatedTranscript_ScoresLow()
        {
            var tokens = TranscriptMatcher.Tokenize(
                "this is a single by the electronic artist nobody you know featuring remixes");

            var match = TranscriptMatcher.MatchTitle(tokens, "Project Hail Mary");

            Assert.NotNull(match);
            Assert.True(match!.Score < 0.35, $"expected gross mismatch, got {match.Score}");
        }

        [Fact]
        public void MatchTitle_NoStoredTitle_ReturnsNull()
        {
            var tokens = TranscriptMatcher.Tokenize(HailMaryCredits);

            Assert.Null(TranscriptMatcher.MatchTitle(tokens, null));
            Assert.Null(TranscriptMatcher.MatchTitle(tokens, "   "));
        }

        [Fact]
        public void MatchNames_ExactAuthor_ScoresHigh()
        {
            var tokens = TranscriptMatcher.Tokenize(HailMaryCredits);

            var match = TranscriptMatcher.MatchNames(tokens, new[] { "Andy Weir" });

            Assert.NotNull(match);
            Assert.True(match!.Score >= 0.95, $"expected ~1.0, got {match.Score}");
        }

        [Theory]
        // STT-mangled spellings of proper nouns must still register via
        // phonetic + edit-distance matching — names are what STT garbles most.
        [InlineData("project hail mary by andy ware narrated by ray porter", "Andy Weir")]
        [InlineData("project hail mary by andy weir narrated by ray porder", "Ray Porter")]
        [InlineData("the martian by andy wier", "Andy Weir")]
        public void MatchNames_SttMangledName_StillScoresWell(string transcript, string storedName)
        {
            var tokens = TranscriptMatcher.Tokenize(transcript);

            var match = TranscriptMatcher.MatchNames(tokens, new[] { storedName });

            Assert.NotNull(match);
            Assert.True(match!.Score >= 0.6, $"mangled '{storedName}' scored {match.Score}");
        }

        [Fact]
        public void MatchNames_WrongAuthorEntirely_ScoresLow()
        {
            var tokens = TranscriptMatcher.Tokenize(
                "greatest hits volume two performed by the midnight orchestra");

            var match = TranscriptMatcher.MatchNames(tokens, new[] { "Brandon Sanderson" });

            Assert.NotNull(match);
            Assert.True(match!.Score < 0.35, $"expected gross mismatch, got {match.Score}");
        }

        [Theory]
        // Spoken middle initials must not break window alignment — observed live:
        // "Sarah J. Maas" heard as "sarah j mass" scored 0.5 because the aligned
        // window compared "maas" against "j".
        [InlineData("a court of thorns and roses by sarah j mass narrated by jennifer ikeda", "Sarah J. Maas")]
        [InlineData("written by george r r martin", "George R. R. Martin")]
        public void MatchNames_SpokenMiddleInitials_StillScoreHigh(string transcript, string storedName)
        {
            var tokens = TranscriptMatcher.Tokenize(transcript);

            var match = TranscriptMatcher.MatchNames(tokens, new[] { storedName });

            Assert.NotNull(match);
            Assert.True(match!.Score >= 0.85, $"'{storedName}' with initials scored {match.Score}");
        }

        [Fact]
        public void MatchNames_PicksBestOfMultipleAuthors()
        {
            var tokens = TranscriptMatcher.Tokenize("written by terry pratchett");

            var match = TranscriptMatcher.MatchNames(tokens, new[] { "Neil Gaiman", "Terry Pratchett" });

            Assert.NotNull(match);
            Assert.True(match!.Score >= 0.95, "co-author present in credits should win");
        }

        [Fact]
        public void MatchNames_EmptyOrNull_ReturnsNull()
        {
            var tokens = TranscriptMatcher.Tokenize(HailMaryCredits);

            Assert.Null(TranscriptMatcher.MatchNames(tokens, null));
            Assert.Null(TranscriptMatcher.MatchNames(tokens, Array.Empty<string>()));
            Assert.Null(TranscriptMatcher.MatchNames(tokens, new[] { "" }));
        }

        [Fact]
        public void MatchedText_OmittedBelowNoiseFloor()
        {
            var tokens = TranscriptMatcher.Tokenize("completely unrelated words everywhere here");

            var match = TranscriptMatcher.MatchTitle(tokens, "Project Hail Mary");

            Assert.NotNull(match);
            Assert.Null(match!.MatchedText);
        }
    }
}
