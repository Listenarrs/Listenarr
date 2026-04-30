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
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Listenarr.Api.Services.Metadata;
using Listenarr.Infrastructure.Models;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Services
{
    public class ImportServiceTests : BaseTests
    {
        [Fact]
        public async Task ImportFilesFromDirectory_CreatesDestinationDirectory_WhenMissing()
        {
            // Arrange
            var outputRoot = FileService.GetTempDirectory("import-out");
            var sourceDir = FileService.GetTempDirectory("import-src");
            var file1 = await FileService.GetFileAsync(sourceDir, "track1.m4b");
            var file2 = await FileService.GetFileAsync(sourceDir, "track2.m4b");

            var settings = new ApplicationSettings { OutputPath = outputRoot, CompletedFileAction = "Move", EnableMetadataProcessing = false };

            // Act
            var importService = _provider.GetRequiredService<IImportService>();
            var results = await importService.ImportFilesFromDirectoryAsync("dl-1", null, [file1, file2], settings);

            // Assert: destination directory created
            Assert.True(Directory.Exists(outputRoot));

            // At least one successful import result should be present
            Assert.Contains(results, r => r.Success);

            // All successful results should point to files under the output root
            foreach (var r in results.Where(r => r.Success))
            {
                Assert.StartsWith(outputRoot.TrimEnd(Path.DirectorySeparatorChar), r.FinalPath, StringComparison.OrdinalIgnoreCase);
                Assert.True(File.Exists(r.FinalPath));
            }
        }

        [Fact]
        public async Task ImportSingleFile_WithAudiobookBasePath_DoesNotDuplicateFolderPatternSegments()
        {
            var outputRoot = FileService.GetTempDirectory("import-out");
            var basePath = Path.Join(outputRoot, "Frank Herbert", "Dune");
            Directory.CreateDirectory(basePath);
            var sourceDir = FileService.GetTempDirectory("import-src");
            var sourceFile = await FileService.GetFileAsync(sourceDir, "dune-source.m4b");

            var settings = await _applicationSettingsRepository.SaveAsync(new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = "Move",
                EnableMetadataProcessing = false,
                FileNamingPattern = "{Author}/{Title}/{Title} ({Year})"
            });

            await _audiobookRepository.AddAsync(new Audiobook
            {
                Id = 123,
                Title = "Dune",
                Authors = ["Frank Herbert"],
                PublishYear = "2021",
                BasePath = basePath
            });

            var importService = _provider.GetRequiredService<IImportService>();

            var result = await importService.ImportSingleFileAsync("dl-1", 123, sourceFile, settings);

            Assert.True(result.Success);
            Assert.NotNull(result.FinalPath);
            Assert.StartsWith(basePath.TrimEnd(Path.DirectorySeparatorChar), result.FinalPath!, StringComparison.OrdinalIgnoreCase);

            var relative = Path.GetRelativePath(basePath, result.FinalPath!);
            Assert.Equal(Path.GetFileName(relative), relative);
            Assert.DoesNotContain($"Frank Herbert{Path.DirectorySeparatorChar}", relative, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain($"Dune{Path.DirectorySeparatorChar}", relative, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ImportFilesFromDirectory_WithLegacyFilePathAndNoBasePath_RegistersImportedFiles()
        {
            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata { Title = "Jack of Shadows", Format = "mp3", BitRate = 128000 });

            _services.AddSingleton(metadataMock.Object);
            Init();

            var outputRoot = FileService.GetTempDirectory("import-out");
            var sourceDir = FileService.GetTempDirectory("import-src");

            var part2 = await FileService.GetFileAsync(sourceDir, "Part 2.mp3");
            var part1 = await FileService.GetFileAsync(sourceDir, "Part 1.mp3");

            var legacyDir = FileService.GetTempDirectory("import-legacy");
            var legacyPath = Path.Join(legacyDir, "legacy.mp3");

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            await _audiobookRepository.AddAsync(new Audiobook
            {
                Id = 456,
                Title = "Jack of Shadows",
                Authors = ["Roger Zelazny"],
                FilePath = legacyPath,
                BasePath = null
            });

            var settings = await _applicationSettingsRepository.SaveAsync(new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = "Copy",
                EnableMetadataProcessing = false,
                FolderNamingPattern = "",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            });

            var importService = _provider.GetRequiredService<IImportService>();
            var results = await importService.ImportFilesFromDirectoryAsync(
                "legacy-basepath",
                456,
                [part2, part1],
                settings);

            Assert.Equal(2, results.Count(r => r.Success));

            var audiobook = await _audiobookRepository.GetByIdAsync(456);
            var files = await _audiobookFileRepository.GetByAudiobookIdAsync(456);

            Assert.NotNull(audiobook);
            Assert.Equal(Path.GetFullPath(outputRoot), audiobook!.BasePath);
            Assert.Equal(2, files.Count);
            Assert.Contains(files, f => f.Path == Path.Join(outputRoot, "Jack of Shadows-01.mp3"));
            Assert.Contains(files, f => f.Path == Path.Join(outputRoot, "Jack of Shadows-02.mp3"));
        }

        [Fact]
        public async Task ImportFilesFromDirectory_ForewordAndChapterOne_GetStableUniqueSequenceNames()
        {
            var outputRoot = FileService.GetTempDirectory("import-out");
            var sourceDir = FileService.GetTempDirectory("import-src");

            var foreword = await FileService.GetFileAsync(sourceDir, "(Foreword by Joe Haldeman).mp3");
            var chapter1 = await FileService.GetFileAsync(sourceDir, "Chapter 01.mp3");
            var chapter2 = await FileService.GetFileAsync(sourceDir, "Chapter 02.mp3");

            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.Is<string>(path => path.EndsWith("(Foreword by Joe Haldeman).mp3", StringComparison.OrdinalIgnoreCase))))
                .ReturnsAsync(new AudioMetadata { Title = "Jack of Shadows", Format = "mp3", TrackNumber = 1 });
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.Is<string>(path => path.EndsWith("Chapter 01.mp3", StringComparison.OrdinalIgnoreCase))))
                .ReturnsAsync(new AudioMetadata { Title = "Jack of Shadows", Format = "mp3", TrackNumber = 1 });
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.Is<string>(path => path.EndsWith("Chapter 02.mp3", StringComparison.OrdinalIgnoreCase))))
                .ReturnsAsync(new AudioMetadata { Title = "Jack of Shadows", Format = "mp3", TrackNumber = 2 });

            _services.AddSingleton(metadataMock.Object);
            Init();

            var settings = await _applicationSettingsRepository.SaveAsync(new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = "Copy",
                EnableMetadataProcessing = false,
                FolderNamingPattern = "",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            });

            var importService = _provider.GetRequiredService<IImportService>();
            var results = await importService.ImportFilesFromDirectoryAsync(
                "foreword-order",
                audiobookId: null,
                [foreword, chapter1, chapter2],
                settings);

            var mapped = results
                .Where(r => r.Success && !string.IsNullOrWhiteSpace(r.SourcePath) && !string.IsNullOrWhiteSpace(r.FinalPath))
                .ToDictionary(r => r.SourcePath!, r => r.FinalPath!, StringComparer.OrdinalIgnoreCase);

            Assert.Equal(Path.Join(outputRoot, "Jack of Shadows-01.mp3"), mapped[foreword]);
            Assert.Equal(Path.Join(outputRoot, "Jack of Shadows-02.mp3"), mapped[chapter1]);
            Assert.Equal(Path.Join(outputRoot, "Jack of Shadows-03.mp3"), mapped[chapter2]);
        }

        [Fact]
        public async Task ImportFilesFromDirectory_MoveImportsCompanionFilesAndDeletesSourceFolder()
        {
            var outputRoot = FileService.GetTempDirectory("import-out");
            var sourceDir = FileService.GetTempDirectory("import-src");

            var audioFile = await FileService.GetFileAsync(sourceDir, "Track 01.mp3");
            var coverFile = await FileService.GetFileAsync(sourceDir, "cover.jpg");
            var notesFile = await FileService.GetFileAsync(sourceDir, "notes.txt");

            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(audioFile))
                .ReturnsAsync(new AudioMetadata { Title = "Companion Book", Format = "mp3", BitRate = 128000 });

            _services.AddSingleton(metadataMock.Object);
            Init();

            var settings = await _applicationSettingsRepository.SaveAsync(new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = "Move",
                EnableMetadataProcessing = false,
                FolderNamingPattern = "",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}",
                ImportBlacklistExtensions = []
            });

            var importService = _provider.GetRequiredService<IImportService>();
            var results = await importService.ImportFilesFromDirectoryAsync(
                "companion-download",
                audiobookId: null,
                [audioFile, coverFile, notesFile],
                settings);

            Assert.Equal(3, results.Count(r => r.Success));
            Assert.Contains(results, r => string.Equals(Path.GetFileName(r.FinalPath), "Companion Book-01.mp3", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(results, r => string.Equals(Path.GetFileName(r.FinalPath), "cover.jpg", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(results, r => string.Equals(Path.GetFileName(r.FinalPath), "notes.txt", StringComparison.OrdinalIgnoreCase));
            Assert.False(Directory.Exists(sourceDir));
        }

        [Fact]
        public async Task ImportFilesFromDirectory_BlacklistedCompanionFilesAreSkipped()
        {
            var outputRoot = FileService.GetTempDirectory("import-out");
            var sourceDir = FileService.GetTempDirectory("import-src");

            var audioFile = await FileService.GetFileAsync(sourceDir, "Track 01.mp3");
            var coverFile = await FileService.GetFileAsync(sourceDir, "cover.jpg");
            var notesFile = await FileService.GetFileAsync(sourceDir, "notes.txt");

            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(audioFile))
                .ReturnsAsync(new AudioMetadata { Title = "Companion Book", Format = "mp3", BitRate = 128000 });

            _services.AddSingleton(metadataMock.Object);
            Init();

            var settings = await _applicationSettingsRepository.SaveAsync(new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = "Move",
                EnableMetadataProcessing = false,
                FolderNamingPattern = "",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}",
                ImportBlacklistExtensions = [".txt"]
            });

            var importService = _provider.GetRequiredService<IImportService>();
            var results = await importService.ImportFilesFromDirectoryAsync(
                "companion-download-blacklist",
                audiobookId: null,
                [audioFile, coverFile, notesFile],
                settings);

            Assert.Equal(2, results.Count(r => r.Success));
            Assert.Contains(results, r => string.Equals(Path.GetFileName(r.FinalPath), "Companion Book-01.mp3", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(results, r => string.Equals(Path.GetFileName(r.FinalPath), "cover.jpg", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(results, r => string.Equals(Path.GetFileName(r.FinalPath), "notes.txt", StringComparison.OrdinalIgnoreCase));
            Assert.True(Directory.Exists(sourceDir));
            Assert.True(File.Exists(notesFile));
        }

        [Fact]
        public async Task ImportFilesFromDirectory_NestedTorrentFolders_DoNotDuplicateSeriesAndTitleSegments()
        {
            var outputRoot = FileService.GetTempDirectory("import-out");
            var sourceRoot = FileService.GetTempDirectory("import-src");
            var title = "Murder by Other Means: The Dispatcher/Book 2";
            var author = "John Scalzi";
            var series = "The Dispatcher";
            var sanitizedTitle = SanitizePathComponentForCurrentPlatform(title);

            var nestedSourceDir = Path.Join(sourceRoot, series, sanitizedTitle);
            Directory.CreateDirectory(nestedSourceDir);

            var sourceFile = await FileService.GetFileAsync(nestedSourceDir, $"{sanitizedTitle}.m4b");

            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(sourceFile))
                .ReturnsAsync(new AudioMetadata
                {
                    Title = title,
                    Artist = author,
                    AlbumArtist = author,
                    Series = series,
                    Format = "m4b"
                });

            _services.AddSingleton(metadataMock.Object);
            Init();

            var settings = await _applicationSettingsRepository.SaveAsync(new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = "Copy",
                EnableMetadataProcessing = false,
                FolderNamingPattern = "{Author}/{Series}/{Title}",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            });

            var importService = _provider.GetRequiredService<IImportService>();
            var results = await importService.ImportFilesFromDirectoryAsync(
                "nested-torrent",
                audiobookId: null,
                [sourceFile],
                settings);

            var success = Assert.Single(results, r => r.Success);
            Assert.NotNull(success.FinalPath);

            var actualFullPath = Path.GetFullPath(success.FinalPath!);
            var actualRelativePath = Path.GetRelativePath(outputRoot, actualFullPath);
            var actualDirectory = Path.GetDirectoryName(actualRelativePath);
            var actualFileName = Path.GetFileName(actualRelativePath);
            var pathSeparator = Path.DirectorySeparatorChar.ToString();

            Assert.Equal(string.Join(pathSeparator, author, series, sanitizedTitle), actualDirectory);
            Assert.StartsWith(sanitizedTitle, actualFileName, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".m4b", actualFileName, StringComparison.OrdinalIgnoreCase);

            var duplicatedSegment = string.Join(pathSeparator, series, sanitizedTitle, series, sanitizedTitle);
            Assert.DoesNotContain(duplicatedSegment, actualFullPath, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        [Trait("OSPlatform", "Windows")]
        public async Task ImportSingleFile_WithWindowsShortBasePath_NormalizesFinalPath()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var outputRoot = FileService.GetTempDirectory("import-out");
            var longBasePath = Path.Join(outputRoot, "A Very Long Audiobook Folder Name");
            Directory.CreateDirectory(longBasePath);

            var sourceDir = FileService.GetTempDirectory("import-src");
            var sourceFile = await FileService.GetFileAsync(sourceDir, "source-track.m4b");

            var shortBasePath = TryGetShortPathName(longBasePath);
            if (string.IsNullOrWhiteSpace(shortBasePath)
                || string.Equals(shortBasePath, longBasePath, StringComparison.OrdinalIgnoreCase)
                || !shortBasePath.Contains('~'))
            {
                return;
            }

            var settings = await _applicationSettingsRepository.SaveAsync(new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = "Move",
                EnableMetadataProcessing = false,
                FileNamingPattern = "{Title}"
            });

            await _audiobookRepository.AddAsync(new Audiobook
            {
                Id = 321,
                Title = "A Great Book",
                Authors = new System.Collections.Generic.List<string> { "Test Author" },
                BasePath = shortBasePath
            });

            var importService = _provider.GetRequiredService<IImportService>();

            var result = await importService.ImportSingleFileAsync("dl-short-path", 321, sourceFile, settings);

            Assert.True(result.Success);
            Assert.NotNull(result.FinalPath);
            Assert.StartsWith(longBasePath.TrimEnd(Path.DirectorySeparatorChar), result.FinalPath!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("~", result.FinalPath!, StringComparison.Ordinal);

            var stored = await _audiobookRepository.GetByIdAsync(321);
            Assert.NotNull(stored);
            Assert.Equal(longBasePath, stored!.BasePath);
        }

        [Fact]
        public async Task ImportSingleFile_WithAudiobookNarrators_AllowsNarratorTokenInNamingPattern()
        {
            var outputRoot = FileService.GetTempDirectory("import-out");
            var sourceDir = FileService.GetTempDirectory("import-src");
            var sourceFile = await FileService.GetFileAsync(sourceDir, "gunslinger-source.m4b");

            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata { Title = "The Gunslinger", Format = "m4b" });

            _services.AddSingleton(metadataMock.Object);
            Init();

            var settings = await _applicationSettingsRepository.SaveAsync(new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = "Copy",
                EnableMetadataProcessing = false,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title} ({Narrator})",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            });

            await _audiobookRepository.AddAsync(new Audiobook
            {
                Id = 987,
                Title = "The Gunslinger",
                Authors = ["Stephen King"],
                Narrators = ["George Guidall", "Frank Muller"]
            });

            var importService = _provider.GetRequiredService<IImportService>();
            var result = await importService.ImportSingleFileAsync("dl-narrator", 987, sourceFile, settings);

            Assert.True(result.Success);
            Assert.NotNull(result.FinalPath);
            Assert.Contains("George Guidall, Frank Muller", result.FinalPath!, StringComparison.Ordinal);
            Assert.True(File.Exists(result.FinalPath));
        }

        [Fact]
        public async Task ImportSingleFile_WithoutAuthors_DoesNotUseNarratorAsAuthorFallback()
        {
            var outputRoot = FileService.GetTempDirectory("import-out");
            var sourceDir = FileService.GetTempDirectory("import-src");
            var sourceFile = await FileService.GetFileAsync(sourceDir, "gunslinger-source.m4b");

            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata
                {
                    Title = "The Gunslinger",
                    Format = "m4b",
                    AlbumArtist = "George Guidall",
                    Narrator = "George Guidall"
                });

            _services.AddSingleton(metadataMock.Object);
            Init();

            var settings = await _applicationSettingsRepository.SaveAsync(new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = "Copy",
                EnableMetadataProcessing = false,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            });

            await _audiobookRepository.AddAsync(new Audiobook
            {
                Id = 988,
                Title = "The Gunslinger",
                Narrators = new System.Collections.Generic.List<string> { "George Guidall" }
            });

            var importService = _provider.GetRequiredService<IImportService>();
            var result = await importService.ImportSingleFileAsync("dl-author-fallback", 988, sourceFile, settings);

            Assert.True(result.Success);
            Assert.NotNull(result.FinalPath);
            Assert.Contains($"Unknown Author{Path.DirectorySeparatorChar}The Gunslinger", result.FinalPath!, StringComparison.Ordinal);
            Assert.DoesNotContain($"George Guidall{Path.DirectorySeparatorChar}The Gunslinger", result.FinalPath!, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ImportSingleFile_WithAudiobookMetadata_SupportsSubtitlePublisherLanguageAndAsinTokens()
        {
            var outputRoot = FileService.GetTempDirectory("import-out");
            var sourceDir = FileService.GetTempDirectory("import-src");
            var sourceFile = await FileService.GetFileAsync(sourceDir, "gunslinger-source.m4b");

            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata { Title = "The Gunslinger", Format = "m4b" });

            _services.AddSingleton(metadataMock.Object);
            Init();

            var settings = await _applicationSettingsRepository.SaveAsync(new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = "Copy",
                EnableMetadataProcessing = false,
                FolderNamingPattern = "{Publisher}/{Language}/{Asin}",
                FileNamingPattern = "{Title} - {Edition} - {Subtitle}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            });

            await _audiobookRepository.AddAsync(new Audiobook
            {
                Id = 989,
                Title = "The Gunslinger",
                Subtitle = "The Dark Tower Begins",
                Authors = new System.Collections.Generic.List<string> { "Stephen King" },
                Publisher = "Penguin Audio",
                Language = "English",
                Asin = "B000FC1R84",
                Edition = "Revised Edition"
            });

            var importService = _provider.GetRequiredService<IImportService>();
            var result = await importService.ImportSingleFileAsync("dl-metadata-vars", 989, sourceFile, settings);

            Assert.True(result.Success);
            Assert.NotNull(result.FinalPath);
            Assert.Contains($"Penguin Audio{Path.DirectorySeparatorChar}English{Path.DirectorySeparatorChar}B000FC1R84", result.FinalPath!, StringComparison.Ordinal);
            Assert.Contains("The Gunslinger - Revised Edition - The Dark Tower Begins.m4b", result.FinalPath!, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ImportFilesFromDirectory_WithAudiobookMetadata_SupportsEditionSubtitlePublisherLanguageAndAsinTokens()
        {
            var outputRoot = FileService.GetTempDirectory("import-out");
            var sourceDir = FileService.GetTempDirectory("import-src");
            var firstSourceFile = await FileService.GetFileAsync(sourceDir, "gunslinger-source-1.m4b");
            var secondSourceFile = await FileService.GetFileAsync(sourceDir, "gunslinger-source-2.m4b");

            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(firstSourceFile))
                .ReturnsAsync(new AudioMetadata { Title = "The Gunslinger", Format = "m4b", DiscNumber = 1 });
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(secondSourceFile))
                .ReturnsAsync(new AudioMetadata { Title = "The Gunslinger", Format = "m4b", DiscNumber = 2 });

            _services.AddSingleton(metadataMock.Object);
            Init();

            var settings = await _applicationSettingsRepository.SaveAsync(new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = "Copy",
                EnableMetadataProcessing = false,
                FolderNamingPattern = "{Publisher}/{Language}/{Asin}",
                FileNamingPattern = "{Title} - {Edition} - {Subtitle}",
                MultiFileNamingPattern = "{Title} - {Edition} - {Subtitle} - {DiskNumber:00}"
            });

            await _audiobookRepository.AddAsync(new Audiobook
            {
                Id = 990,
                Title = "The Gunslinger",
                Subtitle = "The Dark Tower Begins",
                Authors = ["Stephen King"],
                Publisher = "Penguin Audio",
                Language = "English",
                Asin = "B000FC1R84",
                Edition = "Revised Edition"
            });

            var importService = _provider.GetRequiredService<IImportService>();
            var results = await importService.ImportFilesFromDirectoryAsync(
                "dl-dir-metadata-vars",
                990,
                [firstSourceFile, secondSourceFile],
                settings);

            var successfulResults = results.Where(item => item.Success).ToList();
            Assert.Equal(2, successfulResults.Count);
            Assert.All(successfulResults, result =>
            {
                Assert.NotNull(result.FinalPath);
                Assert.Contains($"Penguin Audio{Path.DirectorySeparatorChar}English{Path.DirectorySeparatorChar}B000FC1R84", result.FinalPath!, StringComparison.Ordinal);
                Assert.Contains("The Gunslinger - Revised Edition - The Dark Tower Begins", result.FinalPath!, StringComparison.Ordinal);
            });
        }

        private static string SanitizePathComponentForCurrentPlatform(string value)
        {
            var sanitized = new StringBuilder();

            foreach (var c in value)
            {
                if (char.IsControl(c))
                {
                    continue;
                }

                if (c == ':' || c == '/' || c == '\\')
                {
                    sanitized.Append(" - ");
                }
                else if (Path.GetInvalidFileNameChars().Contains(c) || "<>:\"/\\|?*".Contains(c))
                {
                    sanitized.Append('_');
                }
                else
                {
                    sanitized.Append(c);
                }
            }

            var result = sanitized.ToString();
            result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+", " ");
            result = System.Text.RegularExpressions.Regex.Replace(result, @"(?:\s*-\s*){2,}", " - ");
            result = System.Text.RegularExpressions.Regex.Replace(result, @"_+", "_");
            result = result.Trim().TrimEnd('.', ' ');
            result = System.Text.RegularExpressions.Regex.Replace(result, @"^\s*[-_]+\s*", string.Empty);
            result = System.Text.RegularExpressions.Regex.Replace(result, @"\s*[-_]+\s*$", string.Empty);

            if (string.Equals(result, "CON", StringComparison.OrdinalIgnoreCase)
                || string.Equals(result, "PRN", StringComparison.OrdinalIgnoreCase)
                || string.Equals(result, "AUX", StringComparison.OrdinalIgnoreCase)
                || string.Equals(result, "NUL", StringComparison.OrdinalIgnoreCase)
                || System.Text.RegularExpressions.Regex.IsMatch(result, @"^(COM|LPT)[1-9]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                result += "_";
            }

            return string.IsNullOrWhiteSpace(result) ? "Unknown" : result;
        }

        private static string? TryGetShortPathName(string longPath)
        {
            if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(longPath))
            {
                return null;
            }

            var buffer = new StringBuilder(260);
            var result = GetShortPathName(longPath, buffer, buffer.Capacity);
            if (result == 0)
            {
                return null;
            }

            if (result > buffer.Capacity)
            {
                buffer = new StringBuilder((int)result);
                result = GetShortPathName(longPath, buffer, buffer.Capacity);
                if (result == 0)
                {
                    return null;
                }
            }

            return buffer.ToString();
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetShortPathName(string longPath, StringBuilder shortPathBuffer, int bufferLength);
    }
}
