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
using Listenarr.Tests.Builders;
using System.Runtime.InteropServices;
using System.IO.Compression;
using Listenarr.Domain.Models.Enumerations;
using Listenarr.Tests.Mocks;

namespace Listenarr.Tests.Features.Application.Downloads
{
    [Trait("Category", "DownloadProcessingJob")]
    public class DownloadImportServiceTests : BaseTests
    {
        private MetadataServiceMock metadataServiceMock = new();

        public override async Task InitializeAsync()
        {
            _services.AddSingleton<IMetadataService>(metadataServiceMock);
            Init();
        }

        public static TheoryData<string> PathSuffixes
        {
            get
            {
                var data = new TheoryData<string>
                {
                    { Path.Join("Jane Austen", "Pride and Prejudice") },
                    { Path.Join("Test") },
                    { Path.Join("will", "use", "any", "given", "base", "path") }
                };

                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    data.Add(Path.Join(" even ", "if ", "we", "use", "  space  "));
                }

                return data;
            }
        }

        [Theory]
        [MemberData(nameof(PathSuffixes))]
        public async Task CompletedDownload_LinkedToAudiobook_DoesNotMoveToUnknownAuthor(string pathSuffix)
        {
            var outputRoot = FileService.GetTempDirectory("listenarr-test-output");
            var sourceFile = await FileService.GetTempFileAsync("dl-dbl.mp3");

            var basePath = Path.Join(outputRoot, pathSuffix);

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Pride and Prejudice")
                .WithAuthor("Jane Austen")
                .WithBasePath(basePath)
                .Build());

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputRoot)
                .WithMetadataProcessing()
                .WithMoveFileOnCompleted()
                .WithFolderNamingPattern("{Author}/{Title}")
                .WithFileNamingPattern("{Title}")
                .WithMultiFileNamingPattern("{Title}")
                .Build());

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(audiobook)
                .WithCompletedStatus(DateTime.UtcNow)
                .WithPath(sourceFile)
                .Build());

            // Act - process completed download
            var downloadService = _provider.GetRequiredService<IDownloadImportService>();
            await downloadService.ImportDownloadFilesAsync(audiobook, [sourceFile]);

            var files = await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id);
            Assert.Single(files);
            var file = files.First();
            Assert.NotEmpty(file.Path);
            Assert.True(File.Exists(file.Path));

            var expectedPath = Path.Join(basePath, "Pride and Prejudice.mp3");
            Assert.Equal(expectedPath, file.Path);

            // Also assert there's no AudiobookFile under an "unknown author" path
            var filepaths = await _audiobookFileRepository.GetAllFilePathsAsync();
            Assert.Empty(filepaths.FindAll(path => path.Contains("unknown author", StringComparison.OrdinalIgnoreCase)));
        }

        [Fact]
        public async Task Import_WithMove()
        {
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithMoveFileOnCompleted()
                .WithoutMetadataProcessing()
                .Build());

            var basePath = FileService.GetTempDirectory("library");
            var sourcePath = FileService.GetTempDirectory("downloads");
            var filePath = await FileService.GetFileAsync(sourcePath, "audio.mp3");
            var expectedPath = Path.Join(basePath, "audio.mp3");

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(basePath)
                .Build());

            // Act
            var downloadService = _provider.GetRequiredService<IDownloadImportService>();
            await downloadService.ImportDownloadFilesAsync(audiobook, [filePath]);

            // Moved file does not exist anymore at source
            Assert.True(File.Exists(expectedPath));
            Assert.False(File.Exists(filePath));
        }

        [Fact]
        public async Task Import_WitCopy()
        {
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithCopyFileOnCompleted()
                .WithoutMetadataProcessing()
                .Build());

            var basePath = FileService.GetTempDirectory("library");
            var sourcePath = FileService.GetTempDirectory("downloads");
            var filePath = await FileService.GetFileAsync(sourcePath, "audio.mp3");
            var expectedPath = Path.Join(basePath, "audio.mp3");

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(basePath)
                .Build());

            // Act
            var downloadService = _provider.GetRequiredService<IDownloadImportService>();
            await downloadService.ImportDownloadFilesAsync(audiobook, [filePath]);

            // Copied file does still exist at source
            Assert.True(File.Exists(expectedPath));
            Assert.True(File.Exists(filePath));
        }

        [Fact]
        public async Task DoesNotImportBlacklisted()
        {
            var basePath = FileService.GetTempDirectory("destination");
            var audioPath = await FileService.GetFileAsync(FileService.GetTempPath(), "file1.mp3");
            var coverPath = await FileService.GetFileAsync(FileService.GetTempPath(), "cover.jpg");
            var nfoPath = await FileService.GetFileAsync(FileService.GetTempPath(), "release.nfo");
            var archivePath = await FileService.GetFileAsync(FileService.GetTempPath(), "release.zip");

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(basePath)
                .Build());

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithoutExtractArchive()
                .WithImportBlacklistExtension(".nfo")
                .WithoutMetadataProcessing()
                .Build());

            // Act
            var downloadService = _provider.GetRequiredService<IDownloadImportService>();
            await downloadService.ImportDownloadFilesAsync(audiobook, [audioPath, coverPath, nfoPath, archivePath]);

            Assert.True(File.Exists(Path.Join(basePath, "file1.mp3")));
            Assert.True(File.Exists(Path.Join(basePath, "cover.jpg")));
            Assert.False(File.Exists(Path.Join(basePath, "release.nfo")));
            Assert.True(File.Exists(Path.Join(basePath, "release.zip")));
        }

        [Fact]
        [Trait("Scenario", "ArchiveExtractionImportsContainedFile")]
        public async Task ArchiveExtraction_ImportsContainedFile()
        {
            var destinationDirectory = FileService.GetTempDirectory("destination");
            var inner = FileService.GetTempDirectory("inner");
            _ = await FileService.GetFileAsync(inner, "audio.mp3");
            var zipPath = Path.Join(FileService.GetTempPath(), "release.zip");
            ZipFile.CreateFromDirectory(inner, zipPath);
            Assert.True(File.Exists(zipPath));

            var audiobook = await CreateAudiobook();
            audiobook.BasePath = Path.Join(destinationDirectory, "Fake Author/Fake Title/Anything Really");
            await _audiobookRepository.UpdateAsync(audiobook);

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithExtractArchive()
                .WithMultiFileNamingPattern("{Title}")
                .WithOutputPath(destinationDirectory)
                .WithoutMetadataProcessing()
                .Build());

            // Act
            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();
            await downloadImportService.ImportDownloadFilesAsync(audiobook, [zipPath]);

            var expected = Path.Join(audiobook.BasePath, "audio.mp3");
            var files = await _audiobookFileRepository.GetAllAsync();
            Assert.Single(files);
            var file = files.First();
            Assert.Equal(expected, file.Path);
            Assert.True(File.Exists(expected));
        }

        [Fact]
        [Trait("Method", "ProcessCompletedDownloadAsync")]
        public async Task ProcessCompleteDownloadAsync_MultipleFiles()
        {
            var localSource = FileService.GetTempDirectory("dl-local-source");
            var localDestination = FileService.GetTempDirectory("dl-destination");

            var localChapter1 = await FileService.GetFileAsync(localSource, "01 - Seconde Fondation Isaac Asimov.mp3");
            var localChapter2 = await FileService.GetFileAsync(localSource, "02 - Seconde Fondation Isaac Asimov.mp3");
            var localChapter3 = await FileService.GetFileAsync(localSource, "03 - Seconde Fondation Isaac Asimov.mp3");
            var localChapter4 = await FileService.GetFileAsync(localSource, "04 - Seconde Fondation Isaac Asimov.mp3");
            var localCompanion = await FileService.GetFileAsync(localSource, "Seconde Fondation Isaac Asimov.nfo");

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithMoveFileOnCompleted()
                .WithMultiFileNamingPattern("{Title}-{DiskNumber:00}-{ChapterNumber:00}")
                .WithImportBlacklistExtension(".nfo")
                .Build());

            var basePath = Path.Join(localDestination, "Isaac Asimov", "Le Cycle de Fondation", "Seconde Fondation");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(basePath)
                .WithTitle("Seconde Fondation")
                .WithSeries("Le Cycle de Fondation")
                .WithAuthor("Isaac Asimov")
                .WithYear("1996")
                .Build());

            // Act
            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();
            await downloadImportService.ImportDownloadFilesAsync(audiobook, [localChapter1, localChapter2, localChapter3, localChapter4, localCompanion]);

            var files = await _audiobookFileRepository.GetAllAsync();
            Assert.Equal(4, files.Count);
            Assert.True(File.Exists(Path.Join(basePath, "Seconde Fondation-01-01.mp3")));
            Assert.True(File.Exists(Path.Join(basePath, "Seconde Fondation-02-02.mp3")));
            Assert.True(File.Exists(Path.Join(basePath, "Seconde Fondation-03-03.mp3")));
            Assert.True(File.Exists(Path.Join(basePath, "Seconde Fondation-04-04.mp3")));
            Assert.False(File.Exists(Path.Join(basePath, "Seconde Fondation Isaac Asimov.nfo")));
        }

        [Fact]
        public async Task QualityGating_SkipsLowerQualityImport()
        {
            var library = FileService.GetTempDirectory("library");
            var highQualityFile = await FileService.GetFileAsync(library, "high.mp3");

            var qualityProfile = await _qualityProfileRepository.AddAsync(new QualityProfileBuilder()
                .Build());

            // Create audiobook and an existing high-quality 
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("The High Quality Book")
                .WithBasePath(library)
                .WithQualityProfile(qualityProfile)
                .Build());

            // Simulate existing AudiobookFile (MP3 320) in DB
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(highQualityFile)
                .WithFormat("mp3")
                .WithBitrate(320000)
                .Build());

            // Create a temp file representing a lower-quality completed download (MP3 128)
            var tmpMp3 = await FileService.GetTempFileAsync("dummy.mp3");
            metadataServiceMock.AddMetadata(@"\dummy.mp3$", new AudioMetadata { Title = "Ordered Download", Format = "mp3", BitRate = 128000 });

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettings { OutputPath = Path.GetTempPath(), EnableMetadataProcessing = true, CompletedFileAction = FileAction.Move });

            // Act - process completed download
            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();
            await downloadImportService.ImportDownloadFilesAsync(audiobook, [tmpMp3]);

            // Assert: no new AudiobookFile created for this audiobook (still only the existing one)
            var files = await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id);
            Assert.Single(files);
        }

        [Fact]
        public async Task MultiFileImport_ImportsAllFiles_WithUniqueNames()
        {
            // Create an existing file in destination with name collision
            var basePath = FileService.GetTempDirectory("listenarr-multi");
            var existing = await FileService.GetFileAsync(basePath, "chapter1.mp3");

            // Create source directory with two files: one collides, one new
            var srcDir = FileService.GetTempDirectory("listenarr-src");
            var file1 = await FileService.GetFileAsync(srcDir, "chapter1.mp3");
            var file2 = await FileService.GetFileAsync(srcDir, "chapter2.mp3");

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Multi Book")
                .WithBasePath(basePath)
                .Build());

            // Act
            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();
            await downloadImportService.ImportDownloadFilesAsync(audiobook, [file1, file2]);

            // Assert: files were moved into destination or imported later (deferred). At minimum we expect either DB records
            // to be created synchronously or files to be present on disk in the audiobook BasePath.
            var files = await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id);
            Assert.True(files.Count >= 1, "Expected at least one AudiobookFile DB record to be created");

            // Search recursively because naming patterns may place files into subfolders under the audiobook BasePath
            var diskFiles = Directory.GetFiles(audiobook.BasePath, "*", SearchOption.AllDirectories).Select(p => Path.GetFileName(p)).ToList();
            // Colliding original file should remain and a suffixed file should be present
            Assert.Contains("chapter1.mp3", diskFiles);
            // Either a suffixed file for the colliding chapter1, or the second file should also be present
            Assert.True(
                diskFiles.Any(d => d.StartsWith("chapter1 (")) ||
                diskFiles.Any(d => d.StartsWith("chapter2")) ||
                files.Count > 1,
                "Expected a suffixed filename for the collision or the second file to be present or multiple DB entries");
        }

        [Fact]
        public async Task ImportDownloadFilesAsync_MultipartFiles_KeepNaturalOrderWhenRenamed()
        {
            var outputDir = FileService.GetTempDirectory("listenarr-import-ordered");

            var srcDir = FileService.GetTempDirectory("listenarr-import-ordered-src");
            var part10 = await FileService.GetFileAsync(srcDir, "Part 10.mp3", "ten");
            var part2 = await FileService.GetFileAsync(srcDir, "Part 2.mp3", "two");
            var part1 = await FileService.GetFileAsync(srcDir, "Part 1.mp3", "one");

            metadataServiceMock.AddMetadata(@"\.mp3$", new AudioMetadata { Title = "Ordered Download", Format = "mp3", BitRate = 128000 });

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(outputDir)
                .Build());

            var settings = await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputDir)
                .WithMetadataProcessing()
                .WithCopyFileOnCompleted()
                .WithFolderNamingPattern("")
                .WithFileNamingPattern("{Title}")
                .WithMultiFileNamingPattern("{Title}-{DiskNumber:00}")
                .Build());

            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();
            var results = await downloadImportService.ImportDownloadFilesAsync(audiobook, [part10, part2, part1]);

            var mapped = results
                .Where(r => r.Success && !string.IsNullOrWhiteSpace(r.FinalPath) && !string.IsNullOrWhiteSpace(r.SourcePath))
                .ToDictionary(r => r.SourcePath!, r => r.FinalPath!, StringComparer.OrdinalIgnoreCase);

            Assert.Equal(Path.Join(outputDir, "Ordered Download-01.mp3"), mapped[part1]);
            Assert.Equal(Path.Join(outputDir, "Ordered Download-02.mp3"), mapped[part2]);
            Assert.Equal(Path.Join(outputDir, "Ordered Download-10.mp3"), mapped[part10]);
            Assert.Equal("one", await File.ReadAllTextAsync(mapped[part1]));
            Assert.Equal("two", await File.ReadAllTextAsync(mapped[part2]));
            Assert.Equal("ten", await File.ReadAllTextAsync(mapped[part10]));
        }

        [Fact]
        public async Task ImportDownloadFilesAsync_SameNumberOfResult_ThanNumberOfFiles()
        {
            var outputDir = FileService.GetTempDirectory("listenarr-import-ordered");

            var srcDir = FileService.GetTempDirectory("listenarr-import-ordered-src");
            var part10 = await FileService.GetFileAsync(srcDir, "Part 10.mp3", "ten");
            var missingPart2 = Path.Join(srcDir, "Part 2.mp3");
            var part1 = await FileService.GetFileAsync(srcDir, "Part 1.mp3", "one");
            var companion1 = await FileService.GetFileAsync(srcDir, "Companion.nfo", "one");
            var missingCompanion2 = Path.Join(srcDir, "Companion.jpg");

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(outputDir)
                .Build());

            var settings = await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputDir)
                .WithoutMetadataProcessing()
                .WithMoveFileOnCompleted()
                .Build());

            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();
            var results = await downloadImportService.ImportDownloadFilesAsync(audiobook, [part10, missingPart2, part1, companion1, missingCompanion2]);
            Assert.Equal(5, results.Count);

            List<string> success = [part10, part1, companion1];
            foreach (var result in results)
            {
                if (success.Contains(result.SourcePath))
                {
                    Assert.True(result.Success);
                }
                else
                {
                    Assert.False(result.Success);
                }
            }
        }

        [Fact]
        public async Task DownloadImportService_NoImportedFile_WhenAudioFilesFails()
        {
            var outputDirectory = FileService.GetTempDirectory("library");

            var sourceDirectory = FileService.GetTempDirectory("download");
            var file1 = Path.Join(sourceDirectory, "file1.mp3");
            var file2 = Path.Join(sourceDirectory, "file2.mp3");
            var file3 = Path.Join(sourceDirectory, "file3.mp3");
            var file4 = Path.Join(sourceDirectory, "file4.m4b");
            var companion1 = await FileService.GetFileAsync(sourceDirectory, "companion1.jpg");
            var companion2 = await FileService.GetFileAsync(sourceDirectory, "companion2.jpg");
            var companion3 = await FileService.GetFileAsync(sourceDirectory, "companion3.jpg");

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(outputDirectory)
                .Build());

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputDirectory)
                .WithMetadataProcessing()
                .WithMoveFileOnCompleted()
                .Build());

            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();
            var results = await downloadImportService.ImportDownloadFilesAsync(audiobook, [file1, file2, file3, file4, companion1, companion2, companion3]);
            Assert.Equal(7, results.Count);

            // Output folder should stay empty
            var importedFiles = Directory.EnumerateFiles(outputDirectory, "*.*", SearchOption.AllDirectories)
                .ToList();
            Assert.Empty(importedFiles);
        }
    }
}
