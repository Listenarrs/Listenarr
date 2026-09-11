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

        [Theory]
        [InlineData("en-US")]
        [InlineData("de-DE")]
        [InlineData("fr-FR")]
        public void CommaFormattedPosition_FailsTheParseInsteadOfChangingMagnitude(string culture)
        {
            // NumberStyles.Number carries AllowThousands and the invariant group separator is
            // ',', so "1,5" parses as 15 under Number: the wrong magnitude the invariant pin
            // exists to prevent, arriving by a different route. Float rejects it. The raw
            // string is kept either way, so the filename is unaffected.
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo(culture);
                var metadata = Book("1,5").CreateBasicAudioMetadata();

                Assert.Null(metadata.SeriesPosition);
                Assert.Equal("1,5", metadata.SeriesPositionRaw);
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        // ---------------------------------------------------------------
        // 3. On the import route, both fields come from ONE source.
        // ---------------------------------------------------------------

        private static AudioMetadata FileTags(decimal? position, string? raw) => new()
        {
            Title = "From the file",
            SeriesPosition = position,
            SeriesPositionRaw = raw,
        };

        [Fact]
        public void ImportPosition_TakesBothFieldsFromTheCatalogue_WhenItHasOne()
        {
            // The catalogue says "1-4" (an omnibus) and the file's own tags say 3. Deciding
            // the two fields independently used to give SeriesPositionRaw "1-4" from the
            // catalogue and SeriesPosition 3 from the file, so the sort key and the label
            // described different books. The catalogue wins both, or neither.
            var metadata = DownloadImportService.BuildNamingMetadata(
                Book("1-4"),
                FileTags(3m, "3"),
                "fallback");

            Assert.Equal("1-4", metadata.SeriesPositionRaw);
            Assert.Null(metadata.SeriesPosition);
        }

        [Fact]
        public void ImportPosition_TakesBothFieldsFromTheFile_WhenTheCatalogueHasNone()
        {
            var metadata = DownloadImportService.BuildNamingMetadata(
                Book(null),
                FileTags(3m, "3"),
                "fallback");

            Assert.Equal("3", metadata.SeriesPositionRaw);
            Assert.Equal(3m, metadata.SeriesPosition);
        }

        [Fact]
        public void ImportPosition_SpellsAbsenceAsNull_AndTrimsOnBothRoutes()
        {
            var absent = DownloadImportService.BuildNamingMetadata(
                Book(null),
                FileTags(null, null),
                "fallback");

            // Not string.Empty: absence has one spelling, matching the domain route.
            Assert.Null(absent.SeriesPositionRaw);
            Assert.Null(absent.SeriesPosition);

            var fromCatalogue = DownloadImportService.BuildNamingMetadata(
                Book("  2  "),
                FileTags(null, null),
                "fallback");

            Assert.Equal("2", fromCatalogue.SeriesPositionRaw);
            Assert.Equal(2m, fromCatalogue.SeriesPosition);

            var fromFile = DownloadImportService.BuildNamingMetadata(
                Book(null),
                FileTags(null, "  1-4  "),
                "fallback");

            Assert.Equal("1-4", fromFile.SeriesPositionRaw);

            var blankCatalogue = DownloadImportService.BuildNamingMetadata(
                Book("   "),
                FileTags(null, "1-4"),
                "fallback");

            // Whitespace is not a position, so the file still wins.
            Assert.Equal("1-4", blankCatalogue.SeriesPositionRaw);
        }

        // ---------------------------------------------------------------
        // 4. The raw position reaches a path, so it must not be able to BE a path.
        // ---------------------------------------------------------------

        [Theory]
        [InlineData("1/2")]
        [InlineData("1\\2")]
        [InlineData("../../etc")]
        [InlineData("..")]
        [InlineData("C:\\Windows")]
        public void RawPosition_CannotAddAPathComponent(string position)
        {
            // This PR widens the type of a value that reaches a path from decimal to an
            // arbitrary source string, so the guard that stops it becoming a path is now
            // load-bearing and is pinned here rather than left to the sanitiser's own tests.
            var metadata = Book(position).CreateBasicAudioMetadata();
            var naming = Naming();

            var fileName = naming.ApplyNamingPattern("{SeriesNumber}", metadata, treatAsFilename: true);

            Assert.DoesNotContain("/", fileName);
            Assert.DoesNotContain("\\", fileName);
            Assert.DoesNotContain(":", fileName);
            Assert.NotEqual("..", fileName);
            Assert.NotEqual(".", fileName);
            Assert.Equal(fileName, Path.GetFileName(fileName));

            // The folder route is the one that can actually make directories: the pattern has
            // three components, so the rendered path must have three, whatever the position
            // contained.
            var folder = naming.ApplyNamingPattern(
                "{Author}/{Series}/{SeriesNumber}",
                metadata,
                treatAsFilename: false);

            var segments = folder.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\'],
                StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(3, segments.Length);
            Assert.DoesNotContain("..", segments);
            Assert.DoesNotContain(".", segments);
        }

        [Fact]
        public void RawPositionWithASeparator_CreatesNoDirectoryOutsideTheRoot()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "listenarr-series-position-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                var metadata = Book("../../1/2").CreateBasicAudioMetadata();

                var relative = Naming().ApplyNamingPattern(
                    "{Author}/{Series}/{SeriesNumber}",
                    metadata,
                    treatAsFilename: false);

                var relativeSegments = relative.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\'],
                    StringSplitOptions.RemoveEmptyEntries);

                // Three tokens in the pattern, three components out. Asserting only that the
                // destination stays under the root is not enough: ".." components that cancel
                // each other still land inside the root while building a path nobody asked for.
                Assert.Equal(3, relativeSegments.Length);
                Assert.DoesNotContain("..", relativeSegments);
                Assert.DoesNotContain(".", relativeSegments);

                var destination = Path.GetFullPath(Path.Combine(root, relative));

                // The rendered path stays inside the root: no ".." escapes, no absolute path
                // takes over the Combine.
                Assert.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    destination,
                    StringComparison.Ordinal);

                Directory.CreateDirectory(destination);

                // Author, Series, SeriesNumber and nothing else, as one chain.
                var created = Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                    .Select(directory => Path.GetRelativePath(root, directory))
                    .OrderBy(directory => directory.Length)
                    .ToArray();

                Assert.Equal(3, created.Length);
                Assert.Equal(relativeSegments[0], created[0]);
                Assert.Equal(Path.GetRelativePath(root, destination), created[2]);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
