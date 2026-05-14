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
using Listenarr.Api.Controllers;
using Listenarr.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Listenarr.Api.Services.Metadata;
using Listenarr.Application.Repositories;
using Listenarr.Domain.Models;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Controllers
{
    public class ManualImport_MultiFileCollisionTests : BaseTests
    {
        private string CreateTempDirectory(string name) => FileService.GetTempDirectory(name);

        private static Audiobook CreateAudiobook(int id, string title, string basePath, string? subtitle = null, string? author = null)
        {
            var builder = new AudiobookBuilder()
                .WithId(id)
                .WithTitle(title)
                .WithBasePath(basePath);

            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                builder.WithSubtitle(subtitle);
            }

            if (!string.IsNullOrWhiteSpace(author))
            {
                builder.WithAuthor(author);
            }

            return builder.Build();
        }

        private static ApplicationSettings BuildSettings(
            string outputPath,
            string folderNamingPattern = "",
            string fileNamingPattern = "{Title}",
            string multiFileNamingPattern = "{Title}-{DiskNumber:00}",
            IEnumerable<string>? importBlacklistExtensions = null)
        {
            var builder = new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .WithFolderNamingPattern(folderNamingPattern)
                .WithFileNamingPattern(fileNamingPattern)
                .WithMultiFileNamingPattern(multiFileNamingPattern);

            if (importBlacklistExtensions != null)
            {
                builder.WithoutImportBlacklistExtensions();
                foreach (var extension in importBlacklistExtensions)
                {
                    builder.WithImportBlacklistExtension(extension);
                }
            }

            return builder.Build();
        }

        public static Mock<IAudiobookRepository> GetRepoMock(Audiobook book)
        {
            var repoMock = new Mock<IAudiobookRepository>();
            repoMock.Setup(r => r.GetByIdAsync(It.IsAny<int>())).ReturnsAsync((int id) => id == book.Id ? book : null);
            repoMock.Setup(r => r.UpdateAsync(It.IsAny<Audiobook>())).ReturnsAsync(true);

            return repoMock;
        }

        public static Mock<IScanQueueService> GetScanMock()
        {
            var scanMock = new Mock<IScanQueueService>();
            scanMock.Setup(s => s.EnqueueScanAsync(It.IsAny<Audiobook>(), It.IsAny<string>())).ReturnsAsync(Guid.NewGuid());

            return scanMock;
        }

        public static ManualImportController GetController(Audiobook book, ApplicationSettings settings, Mock<IAudiobookRepository> repoMock = null, Mock<IScanQueueService> scanMock = null)
        {
            repoMock ??= GetRepoMock(book);
            scanMock ??= GetScanMock();

            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata { Title = "Ordered Book", Format = "mp3", BitRate = 128000 });
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.Is<string>(path => path.EndsWith("(Foreword by Joe Haldeman).mp3", StringComparison.OrdinalIgnoreCase))))
                .ReturnsAsync(new AudioMetadata { Title = "Jack of Shadows", Format = "mp3", TrackNumber = 1 });
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.Is<string>(path => path.EndsWith("Chapter 01.mp3", StringComparison.OrdinalIgnoreCase))))
                .ReturnsAsync(new AudioMetadata { Title = "Jack of Shadows", Format = "mp3", TrackNumber = 1 });
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.Is<string>(path => path.EndsWith("Chapter 02.mp3", StringComparison.OrdinalIgnoreCase))))
                .ReturnsAsync(new AudioMetadata { Title = "Jack of Shadows", Format = "mp3", TrackNumber = 2 });
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.Is<string>(path => path.EndsWith("Disc 1.mp3", StringComparison.OrdinalIgnoreCase))))
                .ReturnsAsync(new AudioMetadata { Title = "Jack of Shadows", Format = "mp3", DiscNumber = 1, TrackNumber = 1 });
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.Is<string>(path => path.EndsWith("Disc 2.mp3", StringComparison.OrdinalIgnoreCase))))
                .ReturnsAsync(new AudioMetadata { Title = "Jack of Shadows", Format = "mp3", DiscNumber = 2, TrackNumber = 2 });
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.Is<string>(path => path.EndsWith("Companion Book.mp3", StringComparison.OrdinalIgnoreCase))))
                .ReturnsAsync(new AudioMetadata { Title = "Companion Book", Album = "Companion Book", Artist = "Author A", Format = "mp3" });
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.Is<string>(path => path.EndsWith("Different Book.mp3", StringComparison.OrdinalIgnoreCase))))
                .ReturnsAsync(new AudioMetadata { Title = "Different Book", Album = "Different Book", Artist = "Author A", Format = "mp3" });
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.Is<string>(path => path.EndsWith("Track 01.mp3", StringComparison.OrdinalIgnoreCase))))
                .ReturnsAsync(new AudioMetadata { Title = "Companion Book", Format = "mp3", BitRate = 128000 });
            metadataMock.Setup(m => m.WriteAsinTagAsync(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(Task.CompletedTask);

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(settings);

            var rootFolderMock = new Mock<IRootFolderService>();
            rootFolderMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new System.Collections.Generic.List<Listenarr.Domain.Models.RootFolder>());

            return new ManualImportController(
                Mock.Of<Microsoft.Extensions.Logging.ILogger<ManualImportController>>(),
                repoMock.Object,
                metadataMock.Object,
                new FileNamingService(configMock.Object, NullLogger<FileNamingService>.Instance),
                configMock.Object,
                scanMock.Object,
                rootFolderMock.Object,
                new FileMover(Mock.Of<Microsoft.Extensions.Logging.ILogger<FileMover>>())
            );
        }

        [Fact]
        public async Task InteractiveManualImport_MultipleFiles_ResolvesCollisionsWithinBatch()
        {
            var basePath = CreateTempDirectory("listenarr-manual-batch");
            var srcDir = CreateTempDirectory("listenarr-manual-src");

            var book = CreateAudiobook(42, "Batch Book", basePath);

            // Create two source files
            var src1 = Path.Join(srcDir, "one.mp3");
            var src2 = Path.Join(srcDir, "two.mp3");
            await File.WriteAllTextAsync(src1, "one");
            await File.WriteAllTextAsync(src2, "two");

            var request = new ManualImportRequestDto
            {
                Path = srcDir,
                Mode = "interactive",
                Action = FileMover.FileAction.Copy,
                Items = new System.Collections.Generic.List<ManualImportItemDto>
                {
                    new ManualImportItemDto { FullPath = src1, MatchedAudiobookId = book.Id },
                    new ManualImportItemDto { FullPath = src2, MatchedAudiobookId = book.Id }
                }
            };

            var controller = GetController(book, BuildSettings(basePath));

            await controller.Start(request);

            // Assert: both files should exist in the audiobook base path, second should have a suffix if name collided
            var diskFiles = Directory.GetFiles(basePath, "*", SearchOption.AllDirectories).Select(p => Path.GetFileName(p)).ToList();

            Assert.Contains(diskFiles, f => f.Equals("Batch Book.mp3", StringComparison.OrdinalIgnoreCase) || f.StartsWith("Batch Book"));
            // Expect at least two files (the second should be suffixed)
            Assert.True(diskFiles.Count >= 2, "Expected at least two files in destination (one suffixed for the collision)");
        }

        [Fact]
        public async Task InteractiveManualImport_MultipartFiles_UsesStableNaturalOrderAndNumbering()
        {
            var basePath = CreateTempDirectory("listenarr-manual-ordered");

            var book = CreateAudiobook(84, "Ordered Book", basePath);

            var srcDir = CreateTempDirectory("listenarr-manual-ordered-src");
            var part10 = Path.Join(srcDir, "Part 10.mp3");
            var part2 = Path.Join(srcDir, "Part 2.mp3");
            var part1 = Path.Join(srcDir, "Part 1.mp3");
            await File.WriteAllTextAsync(part10, "ten");
            await File.WriteAllTextAsync(part2, "two");
            await File.WriteAllTextAsync(part1, "one");

            var request = new ManualImportRequestDto
            {
                Path = srcDir,
                Mode = "interactive",
                Action = FileMover.FileAction.Copy,
                Items = new System.Collections.Generic.List<ManualImportItemDto>
                {
                    new ManualImportItemDto { FullPath = part10, MatchedAudiobookId = book.Id },
                    new ManualImportItemDto { FullPath = part2, MatchedAudiobookId = book.Id },
                    new ManualImportItemDto { FullPath = part1, MatchedAudiobookId = book.Id }
                }
            };

            var controller = GetController(
                book,
                BuildSettings(basePath, folderNamingPattern: "{Author}"));

            var action = await controller.Start(request);
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(action.Result);

            var diskFiles = Directory.GetFiles(basePath, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .ToList();

            Assert.Contains("Ordered Book-01.mp3", diskFiles);
            Assert.Contains("Ordered Book-02.mp3", diskFiles);
            Assert.Contains("Ordered Book-10.mp3", diskFiles);
            Assert.Equal("one", await File.ReadAllTextAsync(Path.Join(basePath, "Ordered Book-01.mp3")));
            Assert.Equal("two", await File.ReadAllTextAsync(Path.Join(basePath, "Ordered Book-02.mp3")));
            Assert.Equal("ten", await File.ReadAllTextAsync(Path.Join(basePath, "Ordered Book-10.mp3")));
        }

        [Fact]
        public async Task InteractiveManualImport_TitleOnlyPattern_DoesNotAppendSubtitle()
        {
            var basePath = CreateTempDirectory("listenarr-manual-title-only");
            var srcDir = CreateTempDirectory("listenarr-manual-title-only-src");

            var book = CreateAudiobook(85, "Example", basePath, subtitle: "Yup");

            var source = Path.Join(srcDir, "source.mp3");
            await File.WriteAllTextAsync(source, "audio");

            var request = new ManualImportRequestDto
            {
                Path = srcDir,
                Mode = "interactive",
                Action = FileMover.FileAction.Copy,
                Items = new System.Collections.Generic.List<ManualImportItemDto>
                {
                    new ManualImportItemDto { FullPath = source, MatchedAudiobookId = book.Id }
                }
            };

            var controller = GetController(book, BuildSettings(basePath));

            var action = await controller.Start(request);
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(action.Result);

            var diskFiles = Directory.GetFiles(basePath, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .ToList();

            Assert.Contains("Example.mp3", diskFiles);
            Assert.DoesNotContain("Example - Yup.mp3", diskFiles, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(diskFiles, file => file?.Contains("Yup", StringComparison.OrdinalIgnoreCase) == true);
        }

        [Fact]
        public async Task InteractiveManualImport_MultiFileTitleOnlyPattern_DoesNotAppendSubtitle()
        {
            var basePath = CreateTempDirectory("listenarr-manual-multi-title-only");
            var srcDir = CreateTempDirectory("listenarr-manual-multi-title-only-src");

            var book = CreateAudiobook(86, "Example", basePath, subtitle: "Yup");

            var part1 = Path.Join(srcDir, "Part 1.mp3");
            var part2 = Path.Join(srcDir, "Part 2.mp3");
            await File.WriteAllTextAsync(part1, "one");
            await File.WriteAllTextAsync(part2, "two");

            var request = new ManualImportRequestDto
            {
                Path = srcDir,
                Mode = "interactive",
                Action = FileMover.FileAction.Copy,
                Items = new System.Collections.Generic.List<ManualImportItemDto>
                {
                    new ManualImportItemDto { FullPath = part1, MatchedAudiobookId = book.Id },
                    new ManualImportItemDto { FullPath = part2, MatchedAudiobookId = book.Id }
                }
            };

            var controller = GetController(
                book,
                BuildSettings(basePath, multiFileNamingPattern: "{Title}"));

            var action = await controller.Start(request);
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(action.Result);

            var diskFiles = Directory.GetFiles(basePath, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .ToList();

            Assert.Contains("Example-01.mp3", diskFiles);
            Assert.Contains("Example-02.mp3", diskFiles);
            Assert.DoesNotContain(diskFiles, file => file?.Contains("Yup", StringComparison.OrdinalIgnoreCase) == true);
        }

        [Fact]
        public async Task InteractiveManualImport_ForewordAndChapterOne_AvoidsDuplicateNumberedNames()
        {
            var basePath = CreateTempDirectory("listenarr-manual-foreword");
            var srcDir = CreateTempDirectory("listenarr-manual-foreword-sr");

            var book = CreateAudiobook(126, "Jack of Shadows", basePath);

            var foreword = Path.Join(srcDir, "(Foreword by Joe Haldeman).mp3");
            var chapter1 = Path.Join(srcDir, "Chapter 01.mp3");
            var chapter2 = Path.Join(srcDir, "Chapter 02.mp3");
            await File.WriteAllTextAsync(foreword, "foreword");
            await File.WriteAllTextAsync(chapter1, "chapter1");
            await File.WriteAllTextAsync(chapter2, "chapter2");

            var request = new ManualImportRequestDto
            {
                Path = srcDir,
                Mode = "interactive",
                Action = FileMover.FileAction.Copy,
                Items = new System.Collections.Generic.List<ManualImportItemDto>
                {
                    new ManualImportItemDto { FullPath = foreword, MatchedAudiobookId = book.Id },
                    new ManualImportItemDto { FullPath = chapter1, MatchedAudiobookId = book.Id },
                    new ManualImportItemDto { FullPath = chapter2, MatchedAudiobookId = book.Id }
                }
            };

            var controller = GetController(book, BuildSettings(basePath));

            var action = await controller.Start(request);
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(action.Result);

            var diskFiles = Directory.GetFiles(basePath, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .ToList();

            Assert.Contains("Jack of Shadows-01.mp3", diskFiles);
            Assert.Contains("Jack of Shadows-02.mp3", diskFiles);
            Assert.Contains("Jack of Shadows-03.mp3", diskFiles);
            Assert.DoesNotContain("Jack of Shadows-01 (1).mp3", diskFiles, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task InteractiveManualImport_MultiFileBatch_EnqueuesSingleCommonDirectoryScan()
        {
            var outputRoot = CreateTempDirectory("listenarr-manual-scan-root");
            var srcDir = CreateTempDirectory("listenarr-manual-scan-src");

            var book = CreateAudiobook(222, "Jack of Shadows", outputRoot, author: "Roger Zelazny");

            var disc1 = Path.Join(srcDir, "Disc 1.mp3");
            var disc2 = Path.Join(srcDir, "Disc 2.mp3");
            await File.WriteAllTextAsync(disc1, "disc1");
            await File.WriteAllTextAsync(disc2, "disc2");

            var repoMock = GetRepoMock(book);

            var expectedScanPath = Path.Join(outputRoot, "Roger Zelazny", "Jack of Shadows");
            var scanMock = new Mock<IScanQueueService>();
            scanMock.Setup(s => s.EnqueueScanAsync(book, expectedScanPath)).ReturnsAsync(Guid.NewGuid());

            var controller = GetController(
                book,
                BuildSettings(
                    outputRoot,
                    folderNamingPattern: "{Author}/{Title}",
                    multiFileNamingPattern: "Disc {DiskNumber:00}/{Title}-{DiskNumber:00}"),
                repoMock,
                scanMock);

            var request = new ManualImportRequestDto
            {
                Path = srcDir,
                Mode = "interactive",
                Action = FileMover.FileAction.Copy,
                Items = new System.Collections.Generic.List<ManualImportItemDto>
                {
                    new ManualImportItemDto { FullPath = disc1, MatchedAudiobookId = book.Id },
                    new ManualImportItemDto { FullPath = disc2, MatchedAudiobookId = book.Id }
                }
            };

            var action = await controller.Start(request);
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(action.Result);

            Assert.Equal(expectedScanPath, book.BasePath);
            scanMock.Verify(s => s.EnqueueScanAsync(book, expectedScanPath), Times.Once);
            scanMock.Verify(s => s.EnqueueScanAsync(book, It.IsAny<string>()), Times.Once);
            repoMock.Verify(r => r.UpdateAsync(It.Is<Audiobook>(a => a.Id == book.Id && a.BasePath == expectedScanPath)), Times.AtLeastOnce);
        }

        [Fact]
        public async Task InteractiveManualImport_MoveWithCompanionFiles_ImportsSidecarsAndDeletesSourceFolder()
        {
            var destinationRoot = CreateTempDirectory("listenarr-manual-companion-dest");
            var sourceDir = CreateTempDirectory("listenarr-manual-companion-src");

            var book = CreateAudiobook(333, "Companion Book", destinationRoot);

            var audioFile = Path.Join(sourceDir, "Track 01.mp3");
            var coverFile = Path.Join(sourceDir, "cover.jpg");
            var notesFile = Path.Join(sourceDir, "notes.txt");
            await File.WriteAllTextAsync(audioFile, "audio");
            await File.WriteAllTextAsync(coverFile, "cover");
            await File.WriteAllTextAsync(notesFile, "notes");

            var controller = GetController(
                book,
                BuildSettings(destinationRoot, importBlacklistExtensions: []));

            var request = new ManualImportRequestDto
            {
                Path = sourceDir,
                Mode = "interactive",
                Action = FileMover.FileAction.Move,
                IncludeCompanionFiles = true,
                CleanupEmptySourceFolders = true,
                Items = new System.Collections.Generic.List<ManualImportItemDto>
                {
                    new ManualImportItemDto { FullPath = audioFile, MatchedAudiobookId = book.Id }
                }
            };

            var action = await controller.Start(request);
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(action.Result);

            Assert.True(File.Exists(Path.Join(destinationRoot, "Companion Book.mp3")));
            Assert.True(File.Exists(Path.Join(destinationRoot, "cover.jpg")));
            Assert.True(File.Exists(Path.Join(destinationRoot, "notes.txt")));
            Assert.False(Directory.Exists(sourceDir));
        }

        [Fact]
        public async Task InteractiveManualImport_CompanionPass_SkipsDifferentAudiobookAudioInSameFolder()
        {
            var destinationRoot = CreateTempDirectory("listenarr-manual-mixed-dest");
            var sourceDir = CreateTempDirectory("listenarr-manual-mixed-src");

            var book = CreateAudiobook(334, "Companion Book", destinationRoot);

            var selectedAudio = Path.Join(sourceDir, "Companion Book.mp3");
            var foreignAudio = Path.Join(sourceDir, "Different Book.mp3");
            var coverFile = Path.Join(sourceDir, "cover.jpg");
            await File.WriteAllTextAsync(selectedAudio, "selected");
            await File.WriteAllTextAsync(foreignAudio, "foreign");
            await File.WriteAllTextAsync(coverFile, "cover");

            var controller = GetController(
                book,
                BuildSettings(destinationRoot, importBlacklistExtensions: []));

            var request = new ManualImportRequestDto
            {
                Path = sourceDir,
                Mode = "interactive",
                Action = FileMover.FileAction.Copy,
                IncludeCompanionFiles = true,
                Items = new System.Collections.Generic.List<ManualImportItemDto>
                {
                    new ManualImportItemDto { FullPath = selectedAudio, MatchedAudiobookId = book.Id }
                }
            };

            var action = await controller.Start(request);
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(action.Result);

            Assert.True(File.Exists(Path.Join(destinationRoot, "Companion Book.mp3")));
            Assert.True(File.Exists(Path.Join(destinationRoot, "cover.jpg")));
            Assert.False(File.Exists(Path.Join(destinationRoot, "Different Book.mp3")));
        }


        [Fact]
        public async Task InteractiveManualImport_DontMoveAnything_DontRenameAnything()
        {
            var basePath = CreateTempDirectory("listenarr-manual-neutral-dst");
            var srcDir = CreateTempDirectory("listenarr-manual-neutral-src");

            var book = CreateAudiobook(126, "Jack of Shadows", basePath);

            var foreword = Path.Join(srcDir, "(Foreword by Joe Haldeman).mp3");
            var chapter1 = Path.Join(srcDir, "Chapter 01.mp3");
            var chapter2 = Path.Join(srcDir, "Chapter 02.mp3");
            await File.WriteAllTextAsync(foreword, "foreword");
            await File.WriteAllTextAsync(chapter1, "chapter1");
            await File.WriteAllTextAsync(chapter2, "chapter2");

            var request = new ManualImportRequestDto
            {
                Path = srcDir,
                Mode = "interactive",
                Action = FileMover.FileAction.None,
                Items = new System.Collections.Generic.List<ManualImportItemDto>
                {
                    new ManualImportItemDto { FullPath = foreword, MatchedAudiobookId = book.Id },
                    new ManualImportItemDto { FullPath = chapter1, MatchedAudiobookId = book.Id },
                    new ManualImportItemDto { FullPath = chapter2, MatchedAudiobookId = book.Id }
                }
            };

            var controller = GetController(book, BuildSettings(basePath));

            var action = await controller.Start(request);
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(action.Result);

            var dstFiles = Directory.GetFiles(basePath, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .ToList();

            var srcFiles = Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .ToList();

            Assert.Empty(dstFiles);
            Assert.Contains("(Foreword by Joe Haldeman).mp3", srcFiles);
            Assert.Contains("Chapter 01.mp3", srcFiles);
            Assert.Contains("Chapter 02.mp3", srcFiles);
            Assert.DoesNotContain("Jack of Shadows-01.mp3", srcFiles);
            Assert.DoesNotContain("Jack of Shadows-02.mp3", srcFiles);
            Assert.DoesNotContain("Jack of Shadows-03.mp3", srcFiles);
            Assert.DoesNotContain("Jack of Shadows-01 (1).mp3", srcFiles, StringComparer.OrdinalIgnoreCase);
        }
    }
}
