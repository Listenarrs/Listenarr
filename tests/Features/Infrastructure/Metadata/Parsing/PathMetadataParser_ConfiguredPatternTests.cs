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

namespace Listenarr.Tests.Features.Infrastructure.Metadata.Parsing
{
    /// <summary>
    /// Scanning should read back the layout the renamer writes, so folder parsing is driven by the
    /// configured folder naming pattern rather than a fixed convention.
    /// </summary>
    [Trait("Area", "Metadata")]
    [Trait("Name", "PathMetadataParser_ConfiguredPatternTests")]
    [Trait("Category", "PathMetadataParser")]
    public class PathMetadataParser_ConfiguredPatternTests : BaseTests
    {
        private const string BracketPattern =
            "{Author}/[{Series} {SeriesNumber}] {Title} {{Narrator}} ({Year})";

        private static PathParsedMetadata Parse(string author, string bookFolder, string? pattern = BracketPattern)
        {
            var root = Path.Join(Path.GetTempPath(), $"MetadataRoot-{Guid.NewGuid():N}");
            var file = Path.Join(root, author, bookFolder, "book.m4b");
            var syntax = FileSystemPathSemantics.CurrentHostDefault.Syntax;
            return PathMetadataParser.ParsePathOnly(
                file,
                root,
                new FileSystemPathSemantics(syntax, FileSystemCaseSensitivity.Insensitive),
                pattern);
        }

        [Theory]
        [InlineData("[Revelation Space 10] Dilation Sleep (1990)", "Revelation Space", "10", "Dilation Sleep", "1990")]
        [InlineData("[Chronicles of Narnia 0] Chronicles of Narnia Intro (2019)", "Chronicles of Narnia", "0", "Chronicles of Narnia Intro", "2019")]
        [InlineData("[Known Space 00.0] Beclaimed in Hell (2017)", "Known Space", "00.0", "Beclaimed in Hell", "2017")]
        [InlineData("[The Expanse 2.7] Drive (2012)", "The Expanse", "2.7", "Drive", "2012")]
        [InlineData("[Rama 03] The Garden of Rama (1991)", "Rama", "03", "The Garden of Rama", "1991")]
        public void ParsesEverySeriesNumberForm(
            string folder, string series, string part, string title, string year)
        {
            var parsed = Parse("Some Author", folder);

            Assert.Equal(series, parsed.Series);
            Assert.Equal(part, parsed.SeriesNumber);
            Assert.Equal(title, parsed.Title);
            Assert.Equal(year, parsed.Year);
        }

        [Fact]
        public void SeriesGroupRenderedWithoutItsNumber_StillParses()
        {
            // "[{Series} {SeriesNumber}]" renders as "[Radicalized]" when the number is empty,
            // so each token inside an elidable group must be independently optional when reading.
            var parsed = Parse("Cory Doctorow", "[Radicalized] Radicalized (2019)");

            Assert.Equal("Radicalized", parsed.Series);
            Assert.Null(parsed.SeriesNumber);
            Assert.Equal("Radicalized", parsed.Title);
            Assert.Equal("2019", parsed.Year);
        }

        [Fact]
        public void UnmentionedTrailingTag_DoesNotSwallowTheYear()
        {
            var parsed = Parse("Arthur C. Clarke", "[Rama 03] The Garden of Rama (1991) [abridged]");

            Assert.Equal("The Garden of Rama", parsed.Title);
            Assert.Equal("1991", parsed.Year);
        }

        [Fact]
        public void SecondSeriesBracket_IsToleratedAndFirstWins()
        {
            var parsed = Parse(
                "Orson Scott Card",
                "[Enderverse 07.5][Ender's Saga 1.1] A War Of Gifts {Scott Brick} (2007)");

            Assert.Equal("Enderverse", parsed.Series);
            Assert.Equal("07.5", parsed.SeriesNumber);
            Assert.Equal("A War Of Gifts", parsed.Title);
            Assert.Equal("Scott Brick", parsed.Narrator);
            Assert.Equal("2007", parsed.Year);
        }

        [Fact]
        public void StandaloneBook_ParsesNarratorAndYearWithoutSeries()
        {
            var parsed = Parse("Robert A. Heinlein", "Revolt in 2100 {Eric Michael Summerer} (2009)");

            Assert.Null(parsed.Series);
            Assert.Equal("Revolt in 2100", parsed.Title);
            Assert.Equal("Eric Michael Summerer", parsed.Narrator);
            Assert.Equal("2009", parsed.Year);
        }

        [Fact]
        public void FolderWithoutYear_StillParses()
        {
            var parsed = Parse("Victoria Schwab", "[Villians 0] 5 Warm Up - A Prequel to Vicious");

            Assert.Equal("Villians", parsed.Series);
            Assert.Equal("0", parsed.SeriesNumber);
            Assert.Equal("5 Warm Up - A Prequel to Vicious", parsed.Title);
        }

        [Fact]
        public void AuthorDirectory_IsNotClaimedAsABookFolder()
        {
            // Every token in the pattern is optional, so a bare name satisfies it structurally.
            // Requiring a non-title marker stops "Author/Series/book.m4b" resolving to the author.
            var root = Path.Join(Path.GetTempPath(), $"MetadataRoot-{Guid.NewGuid():N}");
            var file = Path.Join(root, "Alastair Reynolds", "Revelation Space", "book.m4b");
            var syntax = FileSystemPathSemantics.CurrentHostDefault.Syntax;

            var parsed = PathMetadataParser.ParsePathOnly(
                file,
                root,
                new FileSystemPathSemantics(syntax, FileSystemCaseSensitivity.Insensitive),
                BracketPattern);

            Assert.Null(parsed.Title);
            Assert.Null(parsed.Series);
        }

        [Fact]
        public void NoConfiguredPattern_FallsBackToBuiltInConvention()
        {
            var parsed = Parse("Brandon Sanderson", "2010 - The Way of Kings [The Stormlight Archive 1]", pattern: null);

            Assert.Equal("The Stormlight Archive", parsed.Series);
            Assert.Equal("1", parsed.SeriesNumber);
            Assert.Equal("The Way of Kings", parsed.Title);
            Assert.Equal("2010", parsed.Year);
        }

        [Fact]
        public void ConfiguredPatternThatDoesNotMatch_FallsBackToBuiltInConvention()
        {
            var parsed = Parse("Brandon Sanderson", "2010 - The Way of Kings [The Stormlight Archive 1]");

            Assert.Equal("The Way of Kings", parsed.Title);
            Assert.Equal("2010", parsed.Year);
        }

        [Fact]
        public void MalformedPattern_DoesNotBreakScanning()
        {
            var parsed = Parse("Some Author", "2010 - A Title", pattern: "{Author}/[{Unclosed");

            Assert.Equal("A Title", parsed.Title);
        }
    }
}
