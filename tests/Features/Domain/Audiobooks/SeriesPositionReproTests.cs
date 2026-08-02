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
using System.Globalization;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Domain.Audiobooks
{
    /// <summary>
    /// Audible/Audnexus report a series position as a STRING, and it is not always a number.
    /// Real, live examples from the catalogue:
    ///
    ///   "The Father Brown Collection: Books 1-4"  -> position "1-4"  (one ASIN, four books)
    ///   "The Thirty-Nine Steps"                   -> position "1-2"  (bundles its sequel)
    ///   "She and Allan"                           -> position "0"    (prequel slot)
    ///   a novella between two books               -> position "1.5"
    ///
    /// Two defects followed from squeezing that string through a decimal:
    ///
    ///  1. The parse used the server's culture. Where '.' is the group separator (de-DE),
    ///     "1.5" parses as 15; under fr-FR it does not parse at all.
    ///  2. A position that does not parse became null, which is indistinguishable from a
    ///     book with NO series position -- so naming fell through to the track number and
    ///     wrote it into the filename as if it were the series number.
    /// </summary>
    [Trait("Name", "SeriesPositionReproTests")]
    [Trait("Category", "Domain")]
    public sealed class SeriesPositionReproTests : BaseTests
    {
        private static Audiobook Book(string? seriesNumber) => new()
        {
            Title = "Test",
            Series = "Test Series",
            SeriesNumber = seriesNumber,
        };

        private static FileNamingService Naming()
        {
            var config = new Mock<IConfigurationService>();
            var logger = new Mock<ILogger<FileNamingService>>();
            return new FileNamingService(config.Object, logger.Object);
        }

        // ---------------------------------------------------------------
        // 1. Culture: the source always uses '.', so parsing must be invariant.
        // ---------------------------------------------------------------

        [Theory]
        [InlineData("en-US")]
        [InlineData("de-DE")]  // '.' is the GROUP separator here -- "1.5" once parsed as 15
        [InlineData("fr-FR")]  // "1.5" once failed to parse at all
        public void DecimalPosition_SurvivesAnyServerCulture(string culture)
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo(culture);
                var metadata = Book("1.5").CreateBasicAudioMetadata();

                Assert.Equal(1.5m, metadata.SeriesPosition);
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [Fact]
        public void DecimalPosition_IsWrittenInvariantly_NotWithALocalDecimalComma()
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                var metadata = Book("1.5").CreateBasicAudioMetadata();

                var name = Naming().ApplyNamingPattern("{SeriesNumber}", metadata, treatAsFilename: true);

                // Not "1,5" -- a comma in a filename is a locale leaking onto disk.
                Assert.Equal("1.5", name);
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        // ---------------------------------------------------------------
        // 2. A real but non-numeric position must not be lost.
        // ---------------------------------------------------------------

        [Theory]
        [InlineData("1-4")]   // The Father Brown Collection: Books 1-4
        [InlineData("1-2")]   // The Thirty-Nine Steps (bundles Greenmantle)
        public void RangePosition_IsPreserved_EvenThoughItIsNotADecimal(string position)
        {
            var metadata = Book(position).CreateBasicAudioMetadata();

            // decimal? genuinely cannot hold "1-4", and should not try.
            Assert.Null(metadata.SeriesPosition);

            // But the value is real and must survive.
            Assert.Equal(position, metadata.SeriesPositionRaw);
        }

        [Theory]
        [InlineData("1-4")]
        [InlineData("1-2")]
        public void RangePosition_ReachesTheFilename_AndIsNotReplacedByTheTrackNumber(string position)
        {
            var metadata = Book(position).CreateBasicAudioMetadata();
            metadata.TrackNumber = 7;   // the value that used to be written instead

            var name = Naming().ApplyNamingPattern("{SeriesNumber}", metadata, treatAsFilename: true);

            Assert.Equal(position, name);
            Assert.NotEqual("7", name);
        }

        [Fact]
        public void AbsentPosition_StillFallsBackToTheTrackNumber()
        {
            // The fallback itself is deliberate and must be preserved: a book with no series
            // position at all should still get the track number. The bug was that a REAL
            // position was being treated as an absent one.
            var metadata = Book(null).CreateBasicAudioMetadata();
            metadata.TrackNumber = 7;

            var name = Naming().ApplyNamingPattern("{SeriesNumber}", metadata, treatAsFilename: true);

            Assert.Equal("7", name);
        }

        [Fact]
        public void ZeroPosition_Survives()
        {
            // She and Allan sits at position "0" of the Ayesha series.
            var metadata = Book("0").CreateBasicAudioMetadata();

            Assert.Equal(0m, metadata.SeriesPosition);
            Assert.Equal("0", metadata.SeriesPositionRaw);
        }
    }
}
