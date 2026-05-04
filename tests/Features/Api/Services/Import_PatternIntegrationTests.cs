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
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging;
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models.Configurations;
using Listenarr.Application.Common;

namespace Listenarr.Tests.Features.Api.Services
{
    /// <summary>
    /// Integration tests verifying FileNamingService generates correct paths for realistic import workflows
    /// Tests that the service integrates properly with configuration and produces expected file structures
    /// </summary>
    public class Import_PatternIntegrationTests
    {
        [Fact]
        public async Task FileNamingService_SingleFile_GeneratesCompletePathWithoutDiskNumbers()
        {
            // Arrange
            var mockConfig = new Mock<IConfigurationService>();
            mockConfig.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings
                {
                    OutputPath = "/audiobooks",
                    FolderNamingPattern = "{Author}/{Title}",
                    FileNamingPattern = "{Title}",
                    MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
                });

            var service = new FileNamingService(mockConfig.Object, Mock.Of<ILogger<FileNamingService>>());

            var metadata = new AudioMetadata
            {
                Title = "The Martian",
                Artist = "Andy Weir",
                Album = "The Martian"
            };

            // Act - single file (no disk number)
            var result = await service.GenerateFilePathAsync(metadata, ".m4b");

            // Assert
            Assert.NotNull(result);
            Assert.Contains("The Martian", result);
            Assert.Contains(".m4b", result);
            Assert.DoesNotContain("-01", result);
            Assert.DoesNotContain("-00", result);
            // Should include folder structure from FolderNamingPattern
            Assert.Contains(Path.DirectorySeparatorChar.ToString(), result);
        }

        [Fact]
        public async Task FileNamingService_MultiFile_GeneratesUniquePathsForEachDisk()
        {
            // Arrange
            var mockConfig = new Mock<IConfigurationService>();
            mockConfig.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings
                {
                    OutputPath = "/audiobooks",
                    FolderNamingPattern = "{Author}",
                    FileNamingPattern = "{Title}",
                    MultiFileNamingPattern = "{Title}-Part{DiskNumber:00}"
                });

            var service = new FileNamingService(mockConfig.Object, Mock.Of<ILogger<FileNamingService>>());

            var metadata = new AudioMetadata
            {
                Title = "The Stand",
                Artist = "Stephen King"
            };

            // Act - generate paths for multiple disks
            metadata.DiscNumber = 1;
            var path1 = await service.GenerateFilePathAsync(metadata, ".m4b");
            metadata.DiscNumber = 2;
            var path2 = await service.GenerateFilePathAsync(metadata, ".m4b");
            metadata.DiscNumber = 3;
            var path3 = await service.GenerateFilePathAsync(metadata, ".m4b");

            // Assert - all paths should be different
            Assert.NotEqual(path1, path2);
            Assert.NotEqual(path2, path3);
            Assert.NotEqual(path1, path3);

            // All should contain proper disk numbering
            Assert.Contains("Part01", path1);
            Assert.Contains("Part02", path2);
            Assert.Contains("Part03", path3);
        }

        [Fact]
        public async Task FileNamingService_RealWorldScenario_GeneratesValidFilesystem()
        {
            // Arrange - simulate a real import workflow
            var tempBase = Path.Join(Path.GetTempPath(), "listenarr-test", Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempBase);

            try
            {
                var mockConfig = new Mock<IConfigurationService>();
                mockConfig.Setup(c => c.GetApplicationSettingsAsync())
                    .ReturnsAsync(new ApplicationSettings
                    {
                        OutputPath = tempBase,
                        FolderNamingPattern = "{Author}/{Series}/{Title}",
                        FileNamingPattern = "{Title}",
                        MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
                    });

                var service = new FileNamingService(mockConfig.Object, Mock.Of<ILogger<FileNamingService>>());

                var metadata = new AudioMetadata
                {
                    Title = "The Gunslinger",
                    Artist = "Stephen King",
                    Series = "The Dark Tower",
                    SeriesPosition = 1
                };

                // Act - generate paths for a three-disc audiobook
                var paths = new List<string>();
                for (int i = 1; i <= 3; i++)
                {
                    metadata.DiscNumber = i;
                    var path = await service.GenerateFilePathAsync(metadata, ".m4b");
                    paths.Add(path);

                    // Create the actual directory structure to verify it's valid
                    var directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    // Write a test file
                    await File.WriteAllTextAsync(path, $"Test content for disk {i}");
                }

                // Assert - verify filesystem was created correctly
                Assert.Equal(3, paths.Count);

                // All files should exist
                foreach (var path in paths)
                {
                    Assert.True(File.Exists(path), $"File should exist: {path}");
                }

                // Verify folder structure
                var folder = Path.GetDirectoryName(paths[0]);
                Assert.NotNull(folder);
                Assert.Contains("Stephen King", folder);
                Assert.Contains("The Dark Tower", folder);
                Assert.Contains("The Gunslinger", folder);

                // Verify file names are unique
                var fileNames = paths.ConvertAll(p => Path.GetFileName(p));
                Assert.Equal(3, fileNames.Distinct().Count());
                Assert.Contains(fileNames, fn => fn != null && fn.Contains("01"));
                Assert.Contains(fileNames, fn => fn != null && fn.Contains("02"));
                Assert.Contains(fileNames, fn => fn != null && fn.Contains("03"));
            }
            finally
            {
                // Cleanup
                try { Directory.Delete(tempBase, true); } catch (IOException ex) { _ = ex; } catch (UnauthorizedAccessException ex) { _ = ex; }
            }
        }

        [Fact]
        public async Task FileNamingService_SeriesWithSingleFile_UsesSimplePattern()
        {
            // Arrange
            var mockConfig = new Mock<IConfigurationService>();
            mockConfig.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings
                {
                    OutputPath = "/audiobooks",
                    FolderNamingPattern = "{Author}/{Series}/{Title}",
                    FileNamingPattern = "{Series} - {SeriesNumber} - {Title}",
                    MultiFileNamingPattern = "{Series} - {SeriesNumber} - {Title}-{DiskNumber:00}"
                });

            var service = new FileNamingService(mockConfig.Object, Mock.Of<ILogger<FileNamingService>>());

            var metadata = new AudioMetadata
            {
                Title = "Foundation",
                Artist = "Isaac Asimov",
                Series = "Foundation",
                SeriesPosition = 1
            };

            // Act - single file audiobook
            var result = await service.GenerateFilePathAsync(metadata, ".m4b");

            // Assert - should use the simpler FileNamingPattern (no disk number)
            Assert.Contains("Foundation - 1 - Foundation", result);
            Assert.DoesNotContain("-01", result);
            Assert.DoesNotContain("-00", result);
        }

        [Fact]
        public async Task FileNamingService_SeriesWithMultiFile_IncludesDiskNumbers()
        {
            // Arrange
            var mockConfig = new Mock<IConfigurationService>();
            mockConfig.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings
                {
                    OutputPath = "/audiobooks",
                    FolderNamingPattern = "{Author}/{Series}",
                    FileNamingPattern = "{Series} {SeriesNumber} - {Title}",
                    MultiFileNamingPattern = "{Series} {SeriesNumber} - {Title}-D{DiskNumber:00}"
                });

            var service = new FileNamingService(mockConfig.Object, Mock.Of<ILogger<FileNamingService>>());

            var metadata = new AudioMetadata
            {
                Title = "The Way of Kings",
                Artist = "Brandon Sanderson",
                Series = "The Stormlight Archive",
                SeriesPosition = 1
            };

            // Act - multi-disc audiobook
            metadata.DiscNumber = 1;
            var disk1 = await service.GenerateFilePathAsync(metadata, ".m4b");
            metadata.DiscNumber = 2;
            var disk2 = await service.GenerateFilePathAsync(metadata, ".m4b");

            // Assert - should use MultiFileNamingPattern with disk numbers
            Assert.Contains("-D01", disk1);
            Assert.Contains("-D02", disk2);
            Assert.NotEqual(disk1, disk2);
        }

        [Fact]
        public async Task FileNamingService_ChapterNumberInsteadOfDisk_UsesMultiFilePattern()
        {
            // Arrange
            var mockConfig = new Mock<IConfigurationService>();
            mockConfig.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings
                {
                    OutputPath = "/audiobooks",
                    FolderNamingPattern = "{Author}",
                    FileNamingPattern = "{Title}",
                    MultiFileNamingPattern = "{Title}-Chapter{ChapterNumber:00}"
                });

            var service = new FileNamingService(mockConfig.Object, Mock.Of<ILogger<FileNamingService>>());

            var metadata = new AudioMetadata
            {
                Title = "Dune",
                Artist = "Frank Herbert"
            };

            // Act - using chapter numbers instead of disk numbers
            metadata.TrackNumber = 1;
            var chapter1 = await service.GenerateFilePathAsync(metadata, ".mp3");
            metadata.TrackNumber = 2;
            var chapter2 = await service.GenerateFilePathAsync(metadata, ".mp3");

            // Assert - should use MultiFileNamingPattern with chapter numbers
            Assert.Contains("Chapter01", chapter1);
            Assert.Contains("Chapter02", chapter2);
            Assert.NotEqual(chapter1, chapter2);
        }

        [Fact]
        public async Task FileNamingService_BothDiskAndChapter_UsesMultiFilePattern()
        {
            // Arrange
            var mockConfig = new Mock<IConfigurationService>();
            mockConfig.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings
                {
                    OutputPath = "/audiobooks",
                    FolderNamingPattern = "{Author}",
                    FileNamingPattern = "{Title}",
                    MultiFileNamingPattern = "{Title}-D{DiskNumber:00}C{ChapterNumber:00}"
                });

            var service = new FileNamingService(mockConfig.Object, Mock.Of<ILogger<FileNamingService>>());

            var metadata = new AudioMetadata
            {
                Title = "The Lord of the Rings",
                Artist = "J.R.R. Tolkien"
            };

            // Act - both disk and chapter numbers provided
            metadata.DiscNumber = 2;
            metadata.TrackNumber = 5;
            var result = await service.GenerateFilePathAsync(metadata, ".m4b");

            // Assert - should use MultiFileNamingPattern with both numbers
            Assert.Contains("D02", result);
            Assert.Contains("C05", result);
        }

        [Fact]
        public async Task FileNamingService_SeriesNumberWithZeroPadding_PadsCorrectly()
        {
            // Arrange
            var mockConfig = new Mock<IConfigurationService>();
            mockConfig.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings
                {
                    OutputPath = "/audiobooks",
                    FolderNamingPattern = "{Author}/{Series}",
                    FileNamingPattern = "{Series} - {SeriesNumber:00} - {Title}",
                    MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
                });

            var service = new FileNamingService(mockConfig.Object, Mock.Of<ILogger<FileNamingService>>());

            var metadata = new AudioMetadata
            {
                Title = "The Gunslinger",
                Artist = "Stephen King",
                Series = "The Dark Tower",
                SeriesPosition = 1
            };

            // Act - single file audiobook with zero-padded series number
            var result = await service.GenerateFilePathAsync(metadata, ".m4b");

            // Assert - SeriesNumber should be zero-padded to "01"
            Assert.Contains("The Dark Tower - 01 - The Gunslinger", result);
        }

        [Fact]
        public async Task FileNamingService_SeriesNumberWithZeroPadding_TwoDigitNumberUnchanged()
        {
            // Arrange
            var mockConfig = new Mock<IConfigurationService>();
            mockConfig.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings
                {
                    OutputPath = "/audiobooks",
                    FolderNamingPattern = "{Author}/{Series}",
                    FileNamingPattern = "{Series} - {SeriesNumber:00} - {Title}",
                    MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
                });

            var service = new FileNamingService(mockConfig.Object, Mock.Of<ILogger<FileNamingService>>());

            var metadata = new AudioMetadata
            {
                Title = "Wizard and Glass",
                Artist = "Stephen King",
                Series = "The Dark Tower",
                SeriesPosition = 14
            };

            // Act
            var result = await service.GenerateFilePathAsync(metadata, ".m4b");

            // Assert - two-digit number should remain "14"
            Assert.Contains("The Dark Tower - 14 - Wizard and Glass", result);
        }

        [Fact]
        public async Task FileNamingService_SeriesNumberWithZeroPadding_DecimalPositionNotTruncated()
        {
            // Arrange
            var mockConfig = new Mock<IConfigurationService>();
            mockConfig.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings
                {
                    OutputPath = "/audiobooks",
                    FolderNamingPattern = "{Author}/{Series}",
                    FileNamingPattern = "{Series} - {SeriesNumber:00} - {Title}",
                    MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
                });

            var service = new FileNamingService(mockConfig.Object, Mock.Of<ILogger<FileNamingService>>());

            var metadata = new AudioMetadata
            {
                Title = "The Wind Through the Keyhole",
                Artist = "Stephen King",
                Series = "The Dark Tower",
                SeriesPosition = 4.5m
            };

            // Act
            var result = await service.GenerateFilePathAsync(metadata, ".m4b");

            // Assert - decimal position can't be int-parsed, so format is ignored and raw value used
            Assert.Contains("4.5", result);
            Assert.Contains("The Wind Through the Keyhole", result);
        }

        [Fact]
        public async Task FileNamingService_SeriesNumberWithZeroPadding_NullPositionOmitted()
        {
            // Arrange
            var mockConfig = new Mock<IConfigurationService>();
            mockConfig.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings
                {
                    OutputPath = "/audiobooks",
                    FolderNamingPattern = "{Author}",
                    FileNamingPattern = "{Title} - {SeriesNumber:00}",
                    MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
                });

            var service = new FileNamingService(mockConfig.Object, Mock.Of<ILogger<FileNamingService>>());

            var metadata = new AudioMetadata
            {
                Title = "Standalone Book",
                Artist = "Some Author",
                Series = null,
                SeriesPosition = null
            };

            // Act
            var result = await service.GenerateFilePathAsync(metadata, ".m4b");

            // Assert - no series number, so the variable and surrounding separator should be cleaned up
            Assert.Contains("Standalone Book", result);
            Assert.DoesNotContain("00", result);
            Assert.DoesNotContain(" - .", result);
        }

        [Fact]
        public async Task FileNamingService_SeriesNumberWithoutFormat_NoZeroPadding()
        {
            // Arrange
            var mockConfig = new Mock<IConfigurationService>();
            mockConfig.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings
                {
                    OutputPath = "/audiobooks",
                    FolderNamingPattern = "{Author}/{Series}",
                    FileNamingPattern = "{Series} - {SeriesNumber} - {Title}",
                    MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
                });

            var service = new FileNamingService(mockConfig.Object, Mock.Of<ILogger<FileNamingService>>());

            var metadata = new AudioMetadata
            {
                Title = "The Gunslinger",
                Artist = "Stephen King",
                Series = "The Dark Tower",
                SeriesPosition = 1
            };

            // Act
            var result = await service.GenerateFilePathAsync(metadata, ".m4b");

            // Assert - without :00 format, should use raw value "1" not "01"
            Assert.Contains("The Dark Tower - 1 - The Gunslinger", result);
            Assert.DoesNotContain(" 01 ", result);
        }
    }
}
