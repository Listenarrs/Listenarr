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
    /// <summary>
    /// Fixtures are verbatim whisper transcripts from the live library (with the
    /// STT name-manglings they actually produce) — the extractor must work on
    /// real decoder output, not idealized credits.
    /// </summary>
    public class SpokenCreditsExtractorTests
    {
        [Fact]
        public void Extract_RecordedBooksIdent_FullCredits()
        {
            // Warbreaker dramatized-adaptation opening (small.en, live).
            var credits = SpokenCreditsExtractor.Extract(
                "This is Audible. Recorded Books presents a sci-fi audio production, Warbreaker, " +
                "by Brandon Sanderson. This unabridged recording is narrated by James Yagashi and " +
                "directed by David Millen. This book is copyrighted 2009 by Dragon Steel Entertainment LLC. " +
                "This recording is copyrighted 2009 by Recorded Books.");

            Assert.NotNull(credits);
            Assert.Equal("Warbreaker", credits!.Title);
            Assert.Equal("Brandon Sanderson", credits.Author);
            Assert.Equal("James Yagashi", credits.Narrator);
            Assert.Equal("Recorded Books", credits.Publisher);
        }

        [Fact]
        public void Extract_SimplePresentsIdent()
        {
            // 1984 opening (live).
            var credits = SpokenCreditsExtractor.Extract(
                "\"Blackstone Audio Presents 1984\" by George Orwell. Part 1 Chapter 1 It was a " +
                "bright, cold day in April, and the clocks were striking thirteen.");

            Assert.NotNull(credits);
            Assert.Equal("1984", credits!.Title);
            Assert.Equal("George Orwell", credits.Author);
            Assert.Null(credits.Narrator);
            Assert.Equal("Blackstone Audio", credits.Publisher);
        }

        [Fact]
        public void Extract_TitleByAuthorNarratedBy()
        {
            // ACOTAR opening (live; "present" is whisper's rendering, names mangled).
            var credits = SpokenCreditsExtractor.Extract(
                "\"This is Audible,\" recorded books, and one-click digital present, A Court of " +
                "Thorns and Roses, by Sarah J. Mass, narrated by Jennifer Eketa. Chapter 1 The " +
                "forest had become a labyrinth of snow and ice.");

            Assert.NotNull(credits);
            Assert.Equal("A Court of Thorns and Roses", credits!.Title);
            Assert.Equal("Sarah J. Mass", credits.Author);
            Assert.Equal("Jennifer Eketa", credits.Narrator);
        }

        [Fact]
        public void Extract_WrittenByReadByStyle()
        {
            // 3 Days to Live anthology opening (live).
            var credits = SpokenCreditsExtractor.Extract(
                "(upbeat music) The Housekeepers. Written by James Patterson and Julie Margaret " +
                "Hoggbin. Read by Ellen Archer. Adding to the dreamlike effect, my watch had " +
                "decided to stop working somewhere over the Atlantic Ocean.");

            Assert.NotNull(credits);
            Assert.Equal("The Housekeepers", credits!.Title);
            Assert.Equal("James Patterson and Julie Margaret Hoggbin", credits.Author);
            Assert.Equal("Ellen Archer", credits.Narrator);
        }

        [Fact]
        public void Extract_ColdOpen_NoCredits_ReturnsNull()
        {
            // Straight into narration: nothing should be fabricated. "by" inside
            // prose must not be promoted to an author claim.
            var credits = SpokenCreditsExtractor.Extract(
                "It was a bright, cold day in April, and the clocks were striking thirteen. " +
                "Winston Smith slipped quickly through the glass doors of victory mansions, " +
                "pursued by a swirl of gritty dust, one by one the posters watched him.");

            Assert.Null(credits);
        }

        [Fact]
        public void Extract_NarratorOnlyClosingCredits()
        {
            var credits = SpokenCreditsExtractor.Extract(
                "This has been a production of Audible Studios, narrated by Simon Prebble. " +
                "Audible hopes you have enjoyed this program.");

            Assert.NotNull(credits);
            Assert.Equal("Simon Prebble", credits!.Narrator);
            Assert.Null(credits.Author);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Extract_EmptyTranscript_ReturnsNull(string? transcript)
        {
            Assert.Null(SpokenCreditsExtractor.Extract(transcript));
        }

        [Fact]
        public void Extract_CreditsBeyondWindow_AreIgnored()
        {
            // "by" clauses deep into chapter text (past the credits window) must
            // not produce claims.
            var longProse = string.Join(" ", Enumerable.Repeat("The wind swept across the empty moor and the night grew colder still.", 12));
            var credits = SpokenCreditsExtractor.Extract(
                longProse + " The letter was signed by Captain Reynolds.");

            Assert.Null(credits);
        }
    }
}
