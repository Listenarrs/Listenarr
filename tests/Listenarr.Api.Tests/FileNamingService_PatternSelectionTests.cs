using System;
using System.Threading.Tasks;
using Xunit;
using Moq;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Api.Tests
{
    /// <summary>
    /// Tests for FileNamingService automatic pattern selection (single vs multi-file)
    /// </summary>
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
            var result = await _service.GenerateFilePathAsync(metadata, diskNumber: null, chapterNumber: null, ".m4b");

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
                Album = "The Dark Tower"
            };

            // Act - with diskNumber provided
            var result = await _service.GenerateFilePathAsync(metadata, diskNumber: 3, chapterNumber: null, ".m4b");

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
                Artist = "Isaac Asimov"
            };

            // Act - with chapterNumber provided
            var result = await _service.GenerateFilePathAsync(metadata, diskNumber: null, chapterNumber: 12, ".mp3");

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
                Artist = "Frank Herbert"
            };

            // Act - with both diskNumber and chapterNumber provided
            var result = await _service.GenerateFilePathAsync(metadata, diskNumber: 2, chapterNumber: 5, ".m4b");

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
            var file1 = await _service.GenerateFilePathAsync(metadata, diskNumber: 1, chapterNumber: null, ".m4b");
            var file2 = await _service.GenerateFilePathAsync(metadata, diskNumber: 2, chapterNumber: null, ".m4b");
            var file3 = await _service.GenerateFilePathAsync(metadata, diskNumber: 3, chapterNumber: null, ".m4b");

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
            var file1 = await _service.GenerateFilePathAsync(metadata, diskNumber: null, chapterNumber: null, ".m4b");
            var file2 = await _service.GenerateFilePathAsync(metadata, diskNumber: null, chapterNumber: null, ".m4b");

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
            var result = await _service.GenerateFilePathAsync(metadata, diskNumber: null, chapterNumber: null, ".m4b");

            // Assert - should produce a valid path
            Assert.NotNull(result);
            Assert.NotEmpty(result);
        }
    }
}
