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
using Xunit;
using Moq;
using Microsoft.Extensions.Logging;
using Listenarr.Domain.Common;
using Listenarr.Domain.Models;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Common;
using Listenarr.Domain.Models.Configurations;

namespace Listenarr.Tests.Features.Api.Services
{
    /// <summary>
    /// Tests for FileNamingService automatic pattern selection (single vs multi-file)
    /// </summary>
    [Trait("Category", "FileNamingService")]
    public class FileNamingService_PatternSelectionTests
    {
        private readonly Mock<IConfigurationService> _mockConfigService;
        private readonly Mock<ILogger<FileNamingService>> _mockLogger;
        private readonly FileNamingService _service;

        public FileNamingService_PatternSelectionTests()
        {
            _mockConfigService = new Mock<IConfigurationService>();
            _mockLogger = new Mock<ILogger<FileNamingService>>();
            _service = new FileNamingService(_mockConfigService.Object, _mockLogger.Object);
        }

        [Fact]
        public async Task GenerateFilePathAsync_WithoutDiskNumber_UsesSingleFilePattern()
        {
            // Arrange
            var settings = new ApplicationSettings
            {
                OutputPath = "/audiobooks",
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            };
            _mockConfigService.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(settings);

            var metadata = new AudioMetadata
            {
                Title = "The Gunslinger",
                Artist = "Stephen King",
                Album = "The Dark Tower"
            };

            // Act - no diskNumber provided
            var result = await _service.GenerateFilePathAsync(metadata, ".m4b");

            // Assert - should use FileNamingPattern (simpler naming)
            Assert.Contains("The Gunslinger.m4b", result);
            Assert.DoesNotContain("-01", result); // Should not have disk number suffix
            Assert.DoesNotContain("-00", result);
        }

        [Fact]
        public async Task GenerateFilePathAsync_WithDiskNumber_UsesMultiFilePattern()
        {
            // Arrange
            var settings = new ApplicationSettings
            {
                OutputPath = "/audiobooks",
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            };
            _mockConfigService.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(settings);

            var metadata = new AudioMetadata
            {
                Title = "The Gunslinger",
                Artist = "Stephen King",
                Album = "The Dark Tower",
                DiscNumber = 3
            };

            // Act - with diskNumber provided
            var result = await _service.GenerateFilePathAsync(metadata, ".m4b");

            // Assert - should use MultiFileNamingPattern (with disk number)
            Assert.Contains("The Gunslinger-03.m4b", result);
        }

        [Fact]
        public async Task GenerateFilePathAsync_WithChapterNumber_UsesMultiFilePattern()
        {
            // Arrange
            var settings = new ApplicationSettings
            {
                OutputPath = "/audiobooks",
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-Chapter{ChapterNumber:00}"
            };
            _mockConfigService.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(settings);

            var metadata = new AudioMetadata
            {
                Title = "Foundation",
                Artist = "Isaac Asimov",
                TrackNumber = 12,
                Year = 1996,
                SeriesPosition = 3
            };

            // Act - with chapterNumber provided
            var result = await _service.GenerateFilePathAsync(metadata, ".mp3");

            // Assert - should use MultiFileNamingPattern (with chapter number)
            Assert.Contains("Foundation-Chapter12.mp3", result);
        }

        [Fact]
        public async Task GenerateFilePathAsync_WithBothDiskAndChapter_UsesMultiFilePattern()
        {
            // Arrange
            var settings = new ApplicationSettings
            {
                OutputPath = "/audiobooks",
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-D{DiskNumber:00}C{ChapterNumber:00}"
            };
            _mockConfigService.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(settings);

            var metadata = new AudioMetadata
            {
                Title = "Dune",
                Artist = "Frank Herbert",
                DiscNumber = 2,
                TrackNumber = 5
            };

            // Act - with both diskNumber and chapterNumber provided
            var result = await _service.GenerateFilePathAsync(metadata, ".m4b");

            // Assert - should use MultiFileNamingPattern and include both numbers
            Assert.Contains("Dune-D02C05.m4b", result);
        }

        [Fact]
        public async Task GenerateFilePathAsync_MultipleFilesFromSameAudiobook_ProduceUniqueNames()
        {
            // Arrange
            var settings = new ApplicationSettings
            {
                OutputPath = "/audiobooks",
                FolderNamingPattern = "{Author}",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            };
            _mockConfigService.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(settings);

            var metadata = new AudioMetadata
            {
                Title = "The Fellowship of the Ring",
                Artist = "J.R.R. Tolkien"
            };

            // Act - generate paths for multiple disks
            metadata.DiscNumber = 1;
            var file1 = await _service.GenerateFilePathAsync(metadata, ".m4b");
            metadata.DiscNumber = 2;
            var file2 = await _service.GenerateFilePathAsync(metadata, ".m4b");
            metadata.DiscNumber = 3;
            var file3 = await _service.GenerateFilePathAsync(metadata, ".m4b");

            // Assert - all file names should be unique
            Assert.Contains("The Fellowship of the Ring-01.m4b", file1);
            Assert.Contains("The Fellowship of the Ring-02.m4b", file2);
            Assert.Contains("The Fellowship of the Ring-03.m4b", file3);
            Assert.NotEqual(file1, file2);
            Assert.NotEqual(file2, file3);
            Assert.NotEqual(file1, file3);
        }

        [Fact]
        public async Task GenerateFilePathAsync_SingleFilePattern_SameForAllCalls()
        {
            // Arrange
            var settings = new ApplicationSettings
            {
                OutputPath = "/audiobooks",
                FolderNamingPattern = "{Author}",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            };
            _mockConfigService.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(settings);

            var metadata = new AudioMetadata
            {
                Title = "1984",
                Artist = "George Orwell"
            };

            // Act - generate path multiple times without disk/chapter numbers
            var file1 = await _service.GenerateFilePathAsync(metadata, ".m4b");
            var file2 = await _service.GenerateFilePathAsync(metadata, ".m4b");

            // Assert - should produce identical paths (appropriate for single-file audiobooks)
            Assert.Equal(file1, file2);
            Assert.Contains("1984.m4b", file1);
            Assert.DoesNotContain("-01", file1); // Should not have any numbering
        }

        [Fact]
        public async Task GenerateFilePathAsync_EmptyPatterns_FallsBackToDefaults()
        {
            // Arrange
            var settings = new ApplicationSettings
            {
                OutputPath = "/audiobooks",
                FolderNamingPattern = "",
                FileNamingPattern = "",
                MultiFileNamingPattern = ""
            };
            _mockConfigService.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(settings);

            var metadata = new AudioMetadata
            {
                Title = "Empty Pattern Book",
                Artist = "Author Name"
            };

            // Act - should handle empty patterns gracefully
            var result = await _service.GenerateFilePathAsync(metadata, ".m4b");

            // Assert - should produce a valid path
            Assert.NotNull(result);
            Assert.NotEmpty(result);
        }

        [Fact]
        public async Task GenerateFilePathAsync_NormalizesPortableInvalidCharacters()
        {
            var settings = new ApplicationSettings
            {
                OutputPath = FileUtils.GetAbsolutePath("audiobooks"),
                FolderNamingPattern = "{Author}/{Series}/{Title}",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            };
            _mockConfigService.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(settings);

            var metadata = new AudioMetadata
            {
                Title = "Murder by Other Means: The Dispatcher/Book 2?",
                Artist = "John Scalzi",
                Series = "The Dispatcher"
            };

            var result = await _service.GenerateFilePathAsync(metadata, ".m4b");

            Assert.DoesNotContain(":", result.Substring(2));
            Assert.DoesNotContain("?", result);
            Assert.DoesNotContain($"{Path.DirectorySeparatorChar}Murder by Other Means - The Dispatcher - Book 2 - ", result);
            Assert.Contains("Murder by Other Means - The Dispatcher - Book 2", result);
            Assert.EndsWith(".m4b", result);
        }

        [Fact]
        public async Task GenerateFilePathAsync_NormalizesReservedNamesAndTrailingDots()
        {
            var settings = new ApplicationSettings
            {
                OutputPath = "/audiobooks",
                FolderNamingPattern = "{Author}",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            };
            _mockConfigService.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(settings);

            var metadata = new AudioMetadata
            {
                Title = "NUL. ",
                Artist = "CON"
            };

            var result = await _service.GenerateFilePathAsync(metadata, ".m4b");
            var fileName = Path.GetFileName(result);

            Assert.Contains($"{Path.DirectorySeparatorChar}CON_{Path.DirectorySeparatorChar}", result);
            Assert.Equal("NUL_.m4b", fileName);
            Assert.DoesNotContain("NUL. ", result);
        }

        [Fact]
        public void ApplyNamingPattern_WithSlashInVariable_DoesNotCreateNestedFolders()
        {
            var variables = new System.Collections.Generic.Dictionary<string, object>
            {
                ["Author"] = "John Scalzi",
                ["Series"] = "The Dispatcher",
                ["Title"] = "Book 1/2"
            };

            var result = _service.ApplyNamingPattern("{Author}/{Series}/{Title}", variables, treatAsFilename: false);

            Assert.Equal($"John Scalzi{Path.DirectorySeparatorChar}The Dispatcher{Path.DirectorySeparatorChar}Book 1 - 2", result);
            Assert.DoesNotContain($"{Path.DirectorySeparatorChar}Book 1{Path.DirectorySeparatorChar}2", result);
        }

        [Fact]
        public async Task GenerateFilePathAsync_WithNarratorVariable_IncludesNarratorInGeneratedPath()
        {
            var settings = new ApplicationSettings
            {
                OutputPath = "/audiobooks",
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title} {{Narrator}}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            };
            _mockConfigService.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(settings);

            var metadata = new AudioMetadata
            {
                Title = "The Gunslinger",
                Artist = "Stephen King",
                Narrator = "George Guidall"
            };

            var result = await _service.GenerateFilePathAsync(metadata, ".m4b");

            Assert.Contains("The Gunslinger {George Guidall}.m4b", result);
        }

        [Fact]
        public async Task GenerateFilePathAsync_WithWrappedNarratorVariable_RemovesWrapperWhenNarratorMissing()
        {
            var settings = new ApplicationSettings
            {
                OutputPath = "/audiobooks",
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title} {{Narrator}}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            };
            _mockConfigService.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(settings);

            var metadata = new AudioMetadata
            {
                Title = "The Gunslinger",
                Artist = "Stephen King"
            };

            var result = await _service.GenerateFilePathAsync(metadata, ".m4b");

            Assert.Contains("The Gunslinger.m4b", result);
            Assert.DoesNotContain('{', result);
            Assert.DoesNotContain('}', result);
            Assert.DoesNotContain("  ", result);
        }

        [Fact]
        public async Task GenerateFilePathAsync_AuthorDoesNotFallbackToExplicitNarrator()
        {
            var settings = new ApplicationSettings
            {
                OutputPath = "/audiobooks",
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            };
            _mockConfigService.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(settings);

            var metadata = new AudioMetadata
            {
                Title = "The Gunslinger",
                AlbumArtist = "George Guidall",
                Narrator = "George Guidall"
            };

            var result = await _service.GenerateFilePathAsync(metadata, ".m4b");

            Assert.Contains($"Unknown Author{Path.DirectorySeparatorChar}The Gunslinger", result);
            Assert.DoesNotContain($"George Guidall{Path.DirectorySeparatorChar}The Gunslinger", result);
        }

        [Fact]
        public async Task GenerateFilePathAsync_WithExtendedMetadataVariables_IncludesThemInGeneratedPath()
        {
            var settings = new ApplicationSettings
            {
                OutputPath = "/audiobooks",
                FolderNamingPattern = "{Publisher}/{Language}/{Asin}",
                FileNamingPattern = "{Title} - {Edition} - {Subtitle}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            };
            _mockConfigService.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(settings);

            var metadata = new AudioMetadata
            {
                Title = "The Gunslinger",
                Subtitle = "The Dark Tower Begins",
                Edition = "Revised Edition",
                Publisher = "Penguin Audio",
                Language = "English",
                Asin = "B000FC1R84"
            };

            var result = await _service.GenerateFilePathAsync(metadata, ".m4b");

            Assert.Contains($"Penguin Audio{Path.DirectorySeparatorChar}English{Path.DirectorySeparatorChar}B000FC1R84", result);
            Assert.Contains("The Gunslinger - Revised Edition - The Dark Tower Begins.m4b", result);
        }

        [Fact]
        public async Task GenerateFilePathAsync_WithTrack_NotIncludedInGeneratedPath()
        {
            // Arrange
            var settings = new ApplicationSettings
            {
                OutputPath = "/audiobooks",
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            };
            _mockConfigService.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(settings);

            var metadata = new AudioMetadata
            {
                Artist = "Isaac Asimov",
                Series = "Le Cycle de Fondation",
                Title = "Seconde Fondation",
                Subtitle = "Le Cycle de Fondation 3",
                Edition = "",
                Narrator = "Stéphane Ronchewski",
                Publisher = "Audiolib",
                Language = "french",
                Asin = "2367628815",
                SeriesPosition = 3,
                Year = 2019,
                BitRate = 64238,
                DiscNumber = null,
                TrackNumber = 31
            };

            // Act - with chapterNumber provided
            var result = await _service.GenerateFilePathAsync(metadata, ".mp3");

            // Assert - should use MultiFileNamingPattern (with chapter number)
            Assert.Contains("Seconde Fondation.mp3", result);
        }

        [Fact]
        public async Task GenerateFilePathAsync_WithTrack_IncludeOnlyChapter()
        {
            // Arrange
            var settings = new ApplicationSettings
            {
                OutputPath = "/audiobooks",
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}-{ChapterNumber:00}"
            };
            _mockConfigService.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(settings);

            var metadata = new AudioMetadata
            {
                Artist = "Isaac Asimov",
                Series = "Le Cycle de Fondation",
                Title = "Seconde Fondation",
                Subtitle = "Le Cycle de Fondation 3",
                Edition = "",
                Narrator = "Stéphane Ronchewski",
                Publisher = "Audiolib",
                Language = "french",
                Asin = "2367628815",
                SeriesPosition = 3,
                Year = 2019,
                BitRate = 64238,
                DiscNumber = null,
                TrackNumber = 31
            };

            // Act - with chapterNumber provided
            var result = await _service.GenerateFilePathAsync(metadata, ".mp3");

            // Assert - should use MultiFileNamingPattern (with chapter number)
            Assert.Contains("Seconde Fondation-31.mp3", result);
        }
    }
}
