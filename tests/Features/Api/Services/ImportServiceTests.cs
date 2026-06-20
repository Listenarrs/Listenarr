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
using Listenarr.Tests.Common;
using Listenarr.Tests.Builders;

namespace Listenarr.Tests.Features.Api.Services
{
    public class downloadImportServiceTests : BaseTests
    {
        private string _outputRoot = "";

        private ApplicationSettings _settings = new ApplicationSettingsBuilder()
            .WithMoveFileOnCompleted()
            .WithoutMetadataProcessing()
            .WithFolderNamingPattern("{Author}/{Title}")
            .WithMultiFileNamingPattern("{Title} ({Year})")
            .WithFileNamingPattern("{Title} ({Year})")
            .Build();

        private DownloadClientConfiguration _client = new DownloadClientConfigurationBuilder()
            .Build();

        private Audiobook _audiobook = new AudiobookBuilder()
            .WithTitle("Dune")
            .WithAuthor("Frank Herbert")
            .WithId(123)
            .WithYear("2021")
            .Build();

        private Download _download = new DownloadBuilder()
            .Build();

        public override async Task InitializeAsync()
        {
            _outputRoot = FileService.GetTempDirectory("import-out");

            await InitDataAsync();
        }

        private async Task InitDataAsync()
        {
            _settings.OutputPath = _outputRoot;
            await _applicationSettingsRepository.SaveAsync(_settings);

            await _downloadClientConfigurationRepository.SaveAsync(_client);

            _audiobook.BasePath = _outputRoot;
            await _audiobookRepository.AddAsync(_audiobook);

            _download.DownloadClientId = _client.Id;
            _download.AudiobookId = _audiobook.Id;
            await _downloadRepository.AddAsync(_download);
        }

        [Fact]
        public async Task ImportFilesFromDirectory_CreatesDestinationDirectory_WhenMissing()
        {
            // Arrange
            var sourceDir = FileService.GetTempDirectory("import-src");
            var file1 = await FileService.GetFileAsync(sourceDir, "track1.m4b");
            var file2 = await FileService.GetFileAsync(sourceDir, "track2.m4b");

            // Act
            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();
            var results = await downloadImportService.ImportDownloadFilesAsync(_audiobook, [file1, file2]);

            // Assert: destination directory created
            Assert.True(Directory.Exists(_outputRoot));

            // At least one successful import result should be present
            Assert.Contains(results, r => r.Success);

            // All successful results should point to files under the output root
            foreach (var r in results.Where(r => r.Success))
            {
                Assert.StartsWith(_outputRoot.TrimEnd(Path.DirectorySeparatorChar), r.FinalPath, StringComparison.OrdinalIgnoreCase);
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

            _audiobook.BasePath = basePath;
            await _audiobookRepository.UpdateAsync(_audiobook);

            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();

            var results = await downloadImportService.ImportDownloadFilesAsync(_audiobook, [sourceFile]);
            Assert.Single(results);
            var result = results.First();

            Assert.True(result.Success);
            Assert.NotNull(result.FinalPath);
            Assert.StartsWith(basePath.TrimEnd(Path.DirectorySeparatorChar), result.FinalPath!, StringComparison.OrdinalIgnoreCase);

            var relative = Path.GetRelativePath(basePath, result.FinalPath!);
            Assert.Equal(Path.GetFileName(relative), relative);
            Assert.DoesNotContain($"Frank Herbert{Path.DirectorySeparatorChar}", relative, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain($"Dune{Path.DirectorySeparatorChar}", relative, StringComparison.OrdinalIgnoreCase);
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

            _services.AddSingleton<IMetadataService>(metadataMock.Object);
            Init();
            await InitDataAsync();

            var settings = await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputRoot)
                .WithCopyFileOnCompleted()
                .WithMetadataProcessing()
                .WithFolderNamingPattern("")
                .WithFileNamingPattern("{Title}")
                .WithMultiFileNamingPattern("{Title}-{DiskNumber:00}")
                .Build());

            var audiobook = await CreateAudiobook();
            audiobook.BasePath = outputRoot;
            await _audiobookRepository.UpdateAsync(audiobook);

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(audiobook)
                .WithDownloadClientConfiguration(_client)
                .Build());

            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();
            var results = await downloadImportService.ImportDownloadFilesAsync(audiobook, [foreword, chapter1, chapter2]);

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
                CompletedFileAction = FileAction.Move,
                EnableMetadataProcessing = true,
                FolderNamingPattern = "",
                FileNamingPattern = "{Title}-{DiskNumber:00}",
                ImportBlacklistExtensions = []
            });

            var audiobook = await CreateAudiobook();
            audiobook.BasePath = outputRoot;
            await _audiobookRepository.UpdateAsync(audiobook);

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(audiobook)
                .WithDownloadClientConfiguration(_client)
                .Build());

            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();
            var results = await downloadImportService.ImportDownloadFilesAsync(audiobook, [audioFile, coverFile, notesFile]);

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
                CompletedFileAction = FileAction.Move,
                EnableMetadataProcessing = true,
                FolderNamingPattern = "",
                FileNamingPattern = "{Title}-{DiskNumber:00}",
                ImportBlacklistExtensions = [".txt"]
            });

            var audiobook = await CreateAudiobook();
            audiobook.BasePath = outputRoot;
            await _audiobookRepository.UpdateAsync(audiobook);

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(audiobook)
                .WithDownloadClientConfiguration(_client)
                .Build());

            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();
            var results = await downloadImportService.ImportDownloadFilesAsync(audiobook, [audioFile, coverFile, notesFile]);

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
            var pathSeparator = Path.DirectorySeparatorChar.ToString();
            var basePath = string.Join(pathSeparator, outputRoot, author, series, sanitizedTitle);

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
                CompletedFileAction = FileAction.Copy,
                EnableMetadataProcessing = true,
                FolderNamingPattern = "{Author}/{Series}/{Title}",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            });

            var audiobook = await CreateAudiobook();
            audiobook.BasePath = basePath;
            await _audiobookRepository.UpdateAsync(audiobook);

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(audiobook)
                .WithDownloadClientConfiguration(_client)
                .Build());

            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();
            var results = await downloadImportService.ImportDownloadFilesAsync(audiobook, [sourceFile]);

            var success = Assert.Single(results, r => r.Success);
            Assert.NotNull(success.FinalPath);

            var actualFullPath = Path.GetFullPath(success.FinalPath!);
            var actualFileName = Path.GetFileName(actualFullPath);

            Assert.Equal(basePath, Path.GetDirectoryName(actualFullPath));
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
                CompletedFileAction = FileAction.Move,
                EnableMetadataProcessing = false,
                FileNamingPattern = "{Title}"
            });

            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Id = 321,
                Title = "A Great Book",
                Authors = new System.Collections.Generic.List<string> { "Test Author" },
                BasePath = shortBasePath
            });

            _download.AudiobookId = audiobook.Id;
            await _downloadRepository.UpdateAsync(_download);

            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();

            var results = await downloadImportService.ImportDownloadFilesAsync(audiobook, [sourceFile]);
            Assert.Single(results);
            var result = results.First();

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
            await InitDataAsync();

            var settings = await _applicationSettingsRepository.SaveAsync(new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = FileAction.Copy,
                EnableMetadataProcessing = false,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title} ({Narrator})",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            });

            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Id = 987,
                Title = "The Gunslinger",
                Authors = ["Stephen King"],
                Narrators = ["George Guidall", "Frank Muller"],
                BasePath = outputRoot
            });

            _download.AudiobookId = audiobook.Id;
            await _downloadRepository.UpdateAsync(_download);

            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();
            var results = await downloadImportService.ImportDownloadFilesAsync(audiobook, [sourceFile]);
            Assert.Single(results);
            var result = results.First();

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
            await InitDataAsync();

            var settings = await _applicationSettingsRepository.SaveAsync(new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = FileAction.Copy,
                EnableMetadataProcessing = true,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            });

            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Id = 988,
                Title = "The Gunslinger",
                Narrators = ["George Guidall"],
                BasePath = Path.Join(outputRoot, "Unknown Author", "The Gunslinger")
            });

            _download.AudiobookId = audiobook.Id;
            await _downloadRepository.UpdateAsync(_download);

            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();
            var results = await downloadImportService.ImportDownloadFilesAsync(audiobook, [sourceFile]);
            Assert.Single(results);
            var result = results.First();

            Assert.True(result.Success);
            Assert.NotNull(result.FinalPath);
            Assert.Contains(Path.Join("Unknown Author", "The Gunslinger"), result.FinalPath!, StringComparison.Ordinal);
            Assert.DoesNotContain(Path.Join("George Guidall", "The Gunslinger"), result.FinalPath!, StringComparison.Ordinal);
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
            await InitDataAsync();

            var settings = await _applicationSettingsRepository.SaveAsync(new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = FileAction.Copy,
                EnableMetadataProcessing = true,
                FolderNamingPattern = "{Publisher}/{Language}/{Asin}",
                FileNamingPattern = "{Title} - {Edition} - {Subtitle}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            });

            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Id = 989,
                Title = "The Gunslinger",
                Subtitle = "The Dark Tower Begins",
                Authors = ["Stephen King"],
                Publisher = "Penguin Audio",
                Language = "English",
                Asin = "B000FC1R84",
                Edition = "Revised Edition",
                BasePath = Path.Join(outputRoot, "Penguin Audio", "English", "B000FC1R84")
            });

            _download.AudiobookId = audiobook.Id;
            await _downloadRepository.UpdateAsync(_download);

            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();
            var results = await downloadImportService.ImportDownloadFilesAsync(audiobook, [sourceFile]);
            Assert.Single(results);
            var result = results.First();

            Assert.True(result.Success);
            Assert.NotNull(result.FinalPath);
            Assert.Contains(Path.Join("Penguin Audio", "English", "B000FC1R84"), result.FinalPath!, StringComparison.Ordinal);
            Assert.Contains("The Gunslinger - Revised Edition - The Dark Tower Begins.m4b", result.FinalPath!, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ImportSingleFile_WhenDestinationHasSameContent_ReusesDestinationAndRegistersMissingFile()
        {
            var outputRoot = FileService.GetTempDirectory("import-out");
            var sourceDir = FileService.GetTempDirectory("import-src");
            var firstSourceFile = await FileService.GetFileAsync(sourceDir, "first.mp3", "same audio");
            var secondSourceFile = await FileService.GetFileAsync(sourceDir, "second.mp3", "same audio");

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = FileAction.Copy,
                EnableMetadataProcessing = false,
                FolderNamingPattern = "",
                FileNamingPattern = "{Title}"
            });

            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Id = 991,
                Title = "Replay Book",
                Authors = ["Replay Author"],
                BasePath = outputRoot
            });

            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();
            var firstResults = await downloadImportService.ImportDownloadFilesAsync(audiobook, [firstSourceFile]);
            var first = Assert.Single(firstResults);
            Assert.True(first.Success);
            Assert.NotNull(first.FinalPath);

            await _audiobookFileRepository.DeleteByAudiobookIdAsync(audiobook.Id);

            var secondResults = await downloadImportService.ImportDownloadFilesAsync(audiobook, [secondSourceFile]);
            var second = Assert.Single(secondResults);

            Assert.True(second.Success);
            Assert.Equal(first.FinalPath, second.FinalPath);
            Assert.True(File.Exists(secondSourceFile));
            var registeredFiles = await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id);
            Assert.Contains(registeredFiles, file => string.Equals(file.Path, first.FinalPath, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task ImportSingleFile_WhenDestinationContentDiffers_UsesUniqueDestination()
        {
            var outputRoot = FileService.GetTempDirectory("import-out");
            var sourceDir = FileService.GetTempDirectory("import-src");
            var firstSourceFile = await FileService.GetFileAsync(sourceDir, "first.mp3", "old audio");
            var secondSourceFile = await FileService.GetFileAsync(sourceDir, "second.mp3", "new audio");

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = FileAction.Copy,
                EnableMetadataProcessing = false,
                FolderNamingPattern = "",
                FileNamingPattern = "{Title}"
            });

            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Id = 992,
                Title = "Collision Book",
                Authors = ["Collision Author"],
                BasePath = outputRoot
            });

            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();
            var first = Assert.Single(await downloadImportService.ImportDownloadFilesAsync(audiobook, [firstSourceFile]));
            var second = Assert.Single(await downloadImportService.ImportDownloadFilesAsync(audiobook, [secondSourceFile]));

            Assert.True(first.Success);
            Assert.True(second.Success);
            Assert.NotEqual(first.FinalPath, second.FinalPath);
            Assert.Equal("old audio", await File.ReadAllTextAsync(first.FinalPath!));
            Assert.Equal("new audio", await File.ReadAllTextAsync(second.FinalPath!));
            Assert.Contains(" (1)", second.FinalPath);
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
            await InitDataAsync();

            var settings = await _applicationSettingsRepository.SaveAsync(new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = FileAction.Copy,
                EnableMetadataProcessing = true,
                FolderNamingPattern = "{Publisher}/{Language}/{Asin}",
                FileNamingPattern = "{Title} - {Edition} - {Subtitle}",
                MultiFileNamingPattern = "{Title} - {Edition} - {Subtitle} - {DiskNumber:00}"
            });

            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Id = 990,
                Title = "The Gunslinger",
                Subtitle = "The Dark Tower Begins",
                Authors = ["Stephen King"],
                Publisher = "Penguin Audio",
                Language = "English",
                Asin = "B000FC1R84",
                Edition = "Revised Edition",
                BasePath = Path.Join(outputRoot, "Penguin Audio", "English", "B000FC1R84")
            });

            _download.AudiobookId = audiobook.Id;
            await _downloadRepository.UpdateAsync(_download);

            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();
            var results = await downloadImportService.ImportDownloadFilesAsync(audiobook, [firstSourceFile, secondSourceFile]);

            var successfulResults = results.Where(item => item.Success).ToList();
            Assert.Equal(2, successfulResults.Count);
            Assert.All(successfulResults, result =>
            {
                Assert.NotNull(result.FinalPath);
                Assert.Contains(Path.Join("Penguin Audio", "English", "B000FC1R84"), result.FinalPath!, StringComparison.Ordinal);
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
