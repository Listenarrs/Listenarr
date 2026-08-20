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
namespace Listenarr.Tests.Features.Api.Services
{
    /// <summary>
    /// A bracket group may hold more than one optional token (for example "[{Series} {SeriesNumber}]").
    /// These cover the group disappearing when every token inside it is empty, and surviving with the
    /// empty tokens stripped when some content remains.
    /// </summary>
    public class FileNamingService_OptionalTokenElisionTests
    {
        private const string FolderPattern =
            "{Author}/[{Series} {SeriesNumber}] {Title} {{Narrator}} ({Year})";

        private const string FilePattern =
            "{Author} - [{Series} {SeriesNumber}] {Title} {{Narrator}} ({Year})";

        private static FileNamingService CreateService()
        {
            var loggerMock = new Mock<ILogger<FileNamingService>>();
            var configMock = new Mock<IConfigurationService>();
            return new FileNamingService(configMock.Object, loggerMock.Object);
        }

        private static Dictionary<string, object> Variables(
            string author, string series, string seriesNumber, string title, string narrator, string year)
        {
            return new Dictionary<string, object>
            {
                { "Author", author },
                { "Series", series },
                { "SeriesNumber", seriesNumber },
                { "Title", title },
                { "Narrator", narrator },
                { "Year", year }
            };
        }

        [Fact]
        public void AllTokensPresent_RendersEveryGroup()
        {
            var result = CreateService().ApplyNamingPattern(
                FolderPattern,
                Variables("J.K. Rowling", "Harry Potter", "1", "Harry Potter and the Sorcerer's Stone", "Jim Dale", "1997"));

            Assert.Equal(
                Path.Join("J.K. Rowling", "[Harry Potter 1] Harry Potter and the Sorcerer's Stone {Jim Dale} (1997)"),
                result);
        }

        [Fact]
        public void EmptySeriesTokens_DropTheWholeBracketGroup()
        {
            var result = CreateService().ApplyNamingPattern(
                FolderPattern,
                Variables("J.K. Rowling", null, null, "The Ickabog", "Stephen Fry", "2020"));

            Assert.Equal(
                Path.Join("J.K. Rowling", "The Ickabog {Stephen Fry} (2020)"),
                result);
        }

        [Fact]
        public void EmptySeriesAndNarrator_LeaveOnlyTitleAndYear()
        {
            var result = CreateService().ApplyNamingPattern(
                FolderPattern,
                Variables("Adrian Tchaikovsky", null, null, "Cage of Souls", null, "2019"));

            Assert.Equal(
                Path.Join("Adrian Tchaikovsky", "Cage of Souls (2019)"),
                result);
        }

        [Fact]
        public void SeriesWithoutNumber_KeepsBracketGroupWithoutStrayCharacters()
        {
            var result = CreateService().ApplyNamingPattern(
                FolderPattern,
                Variables("Cory Doctorow", "Radicalized", null, "Radicalized", null, "2019"));

            Assert.Equal(
                Path.Join("Cory Doctorow", "[Radicalized] Radicalized (2019)"),
                result);
        }

        [Fact]
        public void TrailingOptionalTokens_LeaveNoTrailingSeparators()
        {
            var result = CreateService().ApplyNamingPattern(
                FolderPattern,
                Variables("Alastair Reynolds", "Revelation Space", "11", "Chasm City", null, null));

            Assert.Equal(
                Path.Join("Alastair Reynolds", "[Revelation Space 11] Chasm City"),
                result);
        }

        [Fact]
        public void FilenamePattern_ElidesEmptyGroupsToo()
        {
            var result = CreateService().ApplyNamingPattern(
                FilePattern,
                Variables("J.K. Rowling", null, null, "The Ickabog", "Stephen Fry", "2020"),
                treatAsFilename: true);

            Assert.Equal("J.K. Rowling - The Ickabog {Stephen Fry} (2020)", result);
        }

        [Fact]
        public void SentinelNeverLeaksIntoOutput()
        {
            var result = CreateService().ApplyNamingPattern(
                FolderPattern,
                Variables("Some Author", null, null, "Some Title", null, null));

            Assert.DoesNotContain("EMPTY", result, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain('\uE000', result);
            Assert.Equal(Path.Join("Some Author", "Some Title"), result);
        }
    }
}
