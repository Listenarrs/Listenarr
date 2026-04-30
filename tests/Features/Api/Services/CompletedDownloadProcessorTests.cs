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
using Listenarr.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using System.IO.Compression;
using System.Reflection;
using Listenarr.Tests.Common;
using Listenarr.Domain.Utils;
using Listenarr.Tests.Builders;
using Listenarr.Application.Services;
using Listenarr.Domain.Models;

namespace Listenarr.Tests.Features.Api.Services
{
    [Trait("Name", "CompletedDownloadProcessorTests")]
    [Trait("Area", "CompletedDownloadProcessing")]
    [Trait("Category", "CompletedDownloadProcessor")]
    public class CompletedDownloadProcessorTests : BaseTests
    {
        private readonly string CLIENT_CONFIG_ID = "dl-client-1";
        private readonly string DOWNLOAD_COMPLETE_ID = "dl-complete-1";
        private readonly int AUDIOBOOK_ID = 1;

        private Mock<IToastService> _toastMock = new Mock<IToastService>();
        private Mock<IDownloadHistoryService> _downloadHistoryMock = new Mock<IDownloadHistoryService>();

        public override async Task InitializeAsync()
        {
            _toastMock = new Mock<IToastService>();
            _toastMock
                .Setup(t => t.PublishToastAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<int?>()))
                .Returns(Task.CompletedTask);

            _downloadHistoryMock = new Mock<IDownloadHistoryService>();
            _downloadHistoryMock
                .Setup(h => h.RecordImportFailedAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>()))
                .Returns(Task.CompletedTask);

            _services.AddSingleton(_toastMock.Object);
            _services.AddSingleton(_downloadHistoryMock.Object);
            Init();

            await InitDataAsync();
        }

        private async Task InitDataAsync()
        {
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithCopyFileOnCompleted()
                .WithoutMetadataProcessing()
                .WithMultiFileNamingPattern("{Title}-{DiskNumber:00}-{ChapterNumber:00}")
                .WithoutExtractArchive()
                .WithImportBlacklistExtension(".nfo")
                .Build());

            await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfiguration
            {
                Id = CLIENT_CONFIG_ID,
                Name = "Slskd",
                Type = "slskd",
                Host = "localhost",
                Port = 5030
            });

            await _downloadRepository.AddAsync(new Download
            {
                Id = DOWNLOAD_COMPLETE_ID,
                DownloadClientId = CLIENT_CONFIG_ID,
                AudiobookId = AUDIOBOOK_ID,
                Metadata = new Dictionary<string, object>
                {
                    ["Uploader"] = "USER1",
                    ["Protocol"] = DownloadProtocol.Torrent
                }
            });
        }

        [Fact]
        [Trait("Method", "MarkImportFailureAsync")]
        [Trait("Scenario", "TransientFailureStaysImportPending")]
        public async Task MarkImportFailureAsync_FirstAttempt_KeepsImportPendingForRetry()
        {
            var downloadId = Guid.NewGuid().ToString();
            await _downloadRepository.AddAsync(new Download
            {
                Id = downloadId,
                Status = DownloadStatus.Downloading,
                DownloadClientId = "client-retry",
                Title = "Retry Candidate"
            });

            var processor = MockUtils.CreateCompletedDownloadProcessor(_provider);

            var markImportFailureMethod = typeof(CompletedDownloadProcessor)
                .GetMethod("MarkImportFailureAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(markImportFailureMethod);

            var task = (Task?)markImportFailureMethod!.Invoke(processor, new object?[]
            {
                downloadId,
                "TransientFailure",
                "boom",
                null,
                false
            });

            Assert.NotNull(task);
            await task!;

            var tracked = await _downloadRepository.FindAsync(downloadId);
            Assert.NotNull(tracked);
            Assert.Equal(DownloadStatus.ImportPending, tracked!.Status);
            Assert.Equal(1, tracked.ImportAttempts);
            Assert.Null(tracked.ImportBlockReason);
            Assert.True(tracked.ImportBlockMessages == null || tracked.ImportBlockMessages.Count == 0);
            Assert.Contains("boom", tracked.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            _downloadHistoryMock.Verify(h => h.RecordImportFailedAsync(
                downloadId,
                "client-retry",
                "Retry Candidate",
                It.Is<string>(msg => msg.Contains("boom", StringComparison.OrdinalIgnoreCase))), Times.Once);

            _toastMock.Verify(t => t.PublishToastAsync(
                "warning",
                "Manual Interaction Required",
                It.IsAny<string>(),
                It.IsAny<int?>()), Times.Never);
        }

        [Fact]
        [Trait("Method", "MarkImportFailureAsync")]
        [Trait("Scenario", "ThresholdFailureBlocksAndSignalsManualInteraction")]
        public async Task MarkImportFailureAsync_ThirdAttempt_BlocksAndSignalsManualInteraction()
        {
            var downloadId = Guid.NewGuid().ToString();
            await _downloadRepository.AddAsync(new Download
            {
                Id = downloadId,
                Status = DownloadStatus.ImportPending,
                DownloadClientId = "client-threshold",
                Title = "Threshold Candidate",
                ImportAttempts = 2
            });

            var processor = MockUtils.CreateCompletedDownloadProcessor(_provider);

            var markImportFailureMethod = typeof(CompletedDownloadProcessor)
                .GetMethod("MarkImportFailureAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(markImportFailureMethod);

            var task = (Task?)markImportFailureMethod!.Invoke(processor, new object?[]
            {
                downloadId,
                "RepeatedFailure",
                "still failing",
                null,
                false
            });

            Assert.NotNull(task);
            await task!;

            var tracked = await _downloadRepository.FindAsync(downloadId);
            Assert.NotNull(tracked);
            Assert.Equal(DownloadStatus.ImportBlocked, tracked!.Status);
            Assert.Equal(3, tracked.ImportAttempts);
            Assert.Equal("RepeatedFailure", tracked.ImportBlockReason);
            Assert.NotNull(tracked.ImportBlockMessages);
            Assert.Contains(tracked.ImportBlockMessages!, m => m.Contains("still failing", StringComparison.OrdinalIgnoreCase));

            _downloadHistoryMock.Verify(h => h.RecordImportFailedAsync(
                downloadId,
                "client-threshold",
                "Threshold Candidate",
                It.Is<string>(msg => msg.Contains("still failing", StringComparison.OrdinalIgnoreCase))), Times.Once);

            _toastMock.Verify(t => t.PublishToastAsync(
                "warning",
                "Manual Interaction Required",
                It.Is<string>(msg => msg.Contains("could not be imported automatically", StringComparison.OrdinalIgnoreCase)),
                8000), Times.Once);

            var records = await _historyRepository.GetRecentAsync(1);
            var capturedHistory = Assert.Single(records);
            Assert.NotNull(capturedHistory);
            Assert.Equal("ImportBlocked", capturedHistory!.EventType);
            Assert.Contains("still failing", capturedHistory.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        [Trait("Method", "MarkImportFailureAsync")]
        [Trait("Scenario", "AttemptCounterPersistsAcrossRestart")]
        public async Task MarkImportFailureAsync_AttemptCounterPersistsAcrossProcessorRestartSimulation()
        {
            var downloadId = Guid.NewGuid().ToString();

            await _downloadRepository.AddAsync(new Download
            {
                Id = downloadId,
                Status = DownloadStatus.ImportPending,
                DownloadClientId = "client-persist",
                Title = "Persistent Attempts",
                ImportAttempts = 1
            });

            var method = typeof(CompletedDownloadProcessor)
                .GetMethod("MarkImportFailureAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var firstProcessor = MockUtils.CreateCompletedDownloadProcessor(_provider);
            var firstTask = (Task?)method!.Invoke(firstProcessor, new object?[]
            {
                downloadId,
                "TransientFailure",
                "attempt two",
                null,
                false
            });
            Assert.NotNull(firstTask);
            await firstTask!;

            var afterFirst = await _downloadRepository.FindAsync(downloadId);
            Assert.NotNull(afterFirst);
            Assert.Equal(DownloadStatus.ImportPending, afterFirst!.Status);
            Assert.Equal(2, afterFirst.ImportAttempts);

            var restartedProcessor = MockUtils.CreateCompletedDownloadProcessor(_provider);
            var secondTask = (Task?)method!.Invoke(restartedProcessor, new object?[]
            {
                downloadId,
                "TransientFailure",
                "attempt three",
                null,
                false
            });
            Assert.NotNull(secondTask);
            await secondTask!;

            var afterSecond = await _downloadRepository.FindAsync(downloadId);
            Assert.NotNull(afterSecond);
            Assert.Equal(DownloadStatus.ImportBlocked, afterSecond!.Status);
            Assert.Equal(3, afterSecond.ImportAttempts);
            Assert.Equal("TransientFailure", afterSecond.ImportBlockReason);
        }

        [Fact]
        [Trait("Method", "ProcessCompletedDownloadAsync")]
        [Trait("Scenario", "NoImportableFilesBlocksAndSignalsManualInteraction")]
        public async Task ProcessCompletedDownloadAsync_NoImportableFiles_BlocksDownload_AndSignalsManualInteraction()
        {
            var downloadId = Guid.NewGuid().ToString();
            await _downloadRepository.AddAsync(new Download
            {
                Id = downloadId,
                Status = DownloadStatus.Downloading,
                DownloadClientId = "client-1",
                Title = "Broken Import"
            });

            var processor = MockUtils.CreateCompletedDownloadProcessor(_provider);
            await processor.ProcessCompletedDownloadAsync(downloadId, string.Empty);

            var tracked = await _downloadRepository.FindAsync(downloadId);
            Assert.NotNull(tracked);
            Assert.Equal(DownloadStatus.ImportBlocked, tracked!.Status);
            Assert.Equal("NoImportableFiles", tracked.ImportBlockReason);
            Assert.Equal(1, tracked.ImportAttempts);
            Assert.NotNull(tracked.ImportBlockMessages);
            Assert.Contains(tracked.ImportBlockMessages!, m => m.Contains("Manual interaction is required.", StringComparison.OrdinalIgnoreCase));

            _downloadHistoryMock.Verify(h => h.RecordImportFailedAsync(
                downloadId,
                "client-1",
                "Broken Import",
                It.Is<string>(msg => msg.Contains("Manual interaction is required.", StringComparison.OrdinalIgnoreCase))), Times.Once);

            _toastMock.Verify(t => t.PublishToastAsync(
                "warning",
                "Manual Interaction Required",
                It.Is<string>(msg => msg.Contains("could not be imported automatically", StringComparison.OrdinalIgnoreCase)),
                8000), Times.Once);

            var records = await _historyRepository.GetRecentAsync(1);
            var capturedHistory = Assert.Single(records);
            Assert.NotNull(capturedHistory);
            Assert.Equal("ImportBlocked", capturedHistory!.EventType);
            Assert.Contains("Manual interaction is required.", capturedHistory.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        [Trait("Method", "ProcessCompletedDownloadAsync")]
        [Trait("Scenario", "SingleFileImportSetsMovedAndFinalPath")]
        public async Task ProcessCompletedDownloadAsync_SingleFile_UpdatesFinalPathAndStatus()
        {
            // Arrange
            var finalPath = FileUtils.GetAbsolutePath("temp", "audiobook.mp3");

            var downloadId = Guid.NewGuid().ToString();
            await _downloadRepository.AddAsync(new Download
            {
                Id = downloadId,
                Status = DownloadStatus.Downloading,
                // FIXME: Is it relevant to have Download without Audiobook ID ? Download should be aborted/removed if audiobook is removed ?
                AudiobookId = null
            });

            var tracked = await _downloadRepository.FindAsync(downloadId);
            Assert.Empty(tracked.FinalPath);

            // Act
            var processor = MockUtils.CreateCompletedDownloadProcessor(_provider);
            await processor.ProcessCompletedDownloadAsync(downloadId, finalPath);

            // Assert
            tracked = await _downloadRepository.FindAsync(downloadId);
            Assert.NotNull(tracked);
            Assert.True(tracked!.Status == DownloadStatus.Completed || tracked.Status == DownloadStatus.Moved, $"Expected Completed or Moved, got {tracked.Status}");
            Assert.NotEmpty(tracked.FinalPath);
        }

        [Fact]
        [Trait("Method", "ProcessCompletedDownloadAsync")]
        [Trait("Scenario", "DirectoryImportInvokesDirectoryPathFlow")]
        public async Task ProcessCompletedDownloadAsync_Directory_InvokesDirectoryImport()
        {
            // Arrange
            _ = await FileService.GetFileAsync(FileService.GetTempPath(), "file1.mp3");

            var downloadId = Guid.NewGuid().ToString();
            await _downloadRepository.AddAsync(new Download
            {
                Id = downloadId,
                // FIXME: This is not a completed download, thus ProcessCompletedDownloadAsync should do nothing on it
                Status = DownloadStatus.Downloading
            });

            // Act
            var processor = MockUtils.CreateCompletedDownloadProcessor(_provider);
            await processor.ProcessCompletedDownloadAsync(downloadId, FileService.GetTempPath());

            // Assert
            var tracked = await _downloadRepository.FindAsync(downloadId);
            Assert.NotNull(tracked);
            Assert.True(tracked!.Status == DownloadStatus.Completed || tracked.Status == DownloadStatus.Moved, $"Expected Completed or Moved, got {tracked.Status}");
        }

        [Fact]
        [Trait("Method", "ProcessCompletedDownloadAsync")]
        [Trait("Scenario", "DirectoryImportIncludesCompanionFilesAndRespectsBlacklist")]
        public async Task ProcessCompletedDownloadAsync_Directory_PassesCompanionFilesExceptBlacklisted()
        {
            var audioPath = await FileService.GetFileAsync(FileService.GetTempPath(), "file1.mp3");
            var coverPath = await FileService.GetFileAsync(FileService.GetTempPath(), "cover.jpg");
            var nfoPath = await FileService.GetFileAsync(FileService.GetTempPath(), "release.nfo");
            var archivePath = await FileService.GetFileAsync(FileService.GetTempPath(), "release.zip");

            var downloadId = Guid.NewGuid().ToString();

            string[]? capturedFiles = null;
            var fileFinalizerMock = new Mock<IFileFinalizer>();
            fileFinalizerMock
                .Setup(f => f.ImportFilesFromDirectoryAsync(downloadId, null, It.IsAny<IEnumerable<string>>(), It.IsAny<ApplicationSettings>()))
                .Callback<string, int?, IEnumerable<string>, ApplicationSettings>((_, _, files, _) => capturedFiles = files.ToArray())
                .ReturnsAsync((string _, int? _, IEnumerable<string> files, ApplicationSettings _) =>
                    [.. files.Select(f => new ImportResult
                    {
                        Success = true,
                        SourcePath = f,
                        FinalPath = f
                    })]);

            _services.AddSingleton(fileFinalizerMock.Object);
            Init();

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithoutExtractArchive()
                .WithImportBlacklistExtension(".nfo")
                .Build());

            await _downloadRepository.AddAsync(new Download
            {
                Id = downloadId,
                // FIXME: This is not a completed download, thus ProcessCompletedDownloadAsync should do nothing on it
                Status = DownloadStatus.Downloading
            });

            var processor = MockUtils.CreateCompletedDownloadProcessor(_provider);
            await processor.ProcessCompletedDownloadAsync(downloadId, FileService.GetTempPath());

            Assert.NotNull(capturedFiles);
            Assert.Contains(audioPath, capturedFiles!);
            Assert.Contains(coverPath, capturedFiles!);
            Assert.DoesNotContain(nfoPath, capturedFiles!);
            Assert.DoesNotContain(archivePath, capturedFiles!);
        }

        [Fact]
        [Trait("Method", "ProcessCompletedDownloadAsync")]
        [Trait("Scenario", "DirectoryImportSeparatesMixedAudiobooksByTitle")]
        public async Task ProcessCompletedDownloadAsync_Directory_FiltersMixedAudioFilesToMatchingTitle()
        {
            var targetAudioPath = await FileService.GetTempFileAsync("Target Book.m4b");
            var foreignAudioPath = await FileService.GetTempFileAsync("Different Book.m4b");
            var coverPath = await FileService.GetTempFileAsync("cover.jpg");

            var downloadId = Guid.NewGuid().ToString();

            string[]? capturedFiles = null;
            var fileFinalizerMock = new Mock<IFileFinalizer>();
            fileFinalizerMock
                .Setup(f => f.ImportFilesFromDirectoryAsync(downloadId, null, It.IsAny<IEnumerable<string>>(), It.IsAny<ApplicationSettings>()))
                .Callback<string, int?, IEnumerable<string>, ApplicationSettings>((_, _, files, _) => capturedFiles = files.ToArray())
                .ReturnsAsync((string _, int? _, IEnumerable<string> files, ApplicationSettings _) =>
                    [.. files.Select(f => new ImportResult
                    {
                        Success = true,
                        SourcePath = f,
                        FinalPath = f
                    })]);

            _services.AddSingleton(fileFinalizerMock.Object);
            Init();

            await _downloadRepository.AddAsync(new Download
            {
                Id = downloadId,
                Status = DownloadStatus.Downloading,
                Title = "Target Book"
            });

            var processor = MockUtils.CreateCompletedDownloadProcessor(_provider);
            await processor.ProcessCompletedDownloadAsync(downloadId, FileService.GetTempPath());

            Assert.NotNull(capturedFiles);
            Assert.Contains(targetAudioPath, capturedFiles!);
            Assert.Contains(coverPath, capturedFiles!);
            Assert.DoesNotContain(foreignAudioPath, capturedFiles!);
        }

        [Fact]
        [Trait("Method", "ProcessCompletedDownloadAsync")]
        [Trait("Scenario", "DirectoryImportUsesClientReportedFilesAsSourceOfTruth")]
        public async Task ProcessCompletedDownloadAsync_Directory_UsesClientReportedFilesToIncludeOnlyTrackedFiles()
        {
            var firstAudioPath = await FileService.GetTempFileAsync("Alpha Book.m4b");
            var secondAudioPath = await FileService.GetTempFileAsync("Omega Companion.m4b");
            var txtPath = await FileService.GetTempFileAsync("book.txt");
            var unrelatedPath = await FileService.GetTempFileAsync("unrelated.jpg");

            var downloadId = Guid.NewGuid().ToString();

            string[]? capturedFiles = null;
            var fileFinalizerMock = new Mock<IFileFinalizer>();
            fileFinalizerMock
                .Setup(f => f.ImportFilesFromDirectoryAsync(downloadId, null, It.IsAny<IEnumerable<string>>(), It.IsAny<ApplicationSettings>()))
                .Callback<string, int?, IEnumerable<string>, ApplicationSettings>((_, _, files, _) => capturedFiles = files.ToArray())
                .ReturnsAsync((string _, int? _, IEnumerable<string> files, ApplicationSettings _) =>
                    [.. files.Select(f => new ImportResult
                    {
                        Success = true,
                        SourcePath = f,
                        FinalPath = f
                    })]);

            var importResolverMock = new Mock<IImportItemResolutionService>();
            importResolverMock
                .Setup(r => r.ResolveImportItemAsync(
                    It.Is<Download>(d => d.Id == downloadId),
                    It.IsAny<QueueItem>(),
                    It.IsAny<QueueItem?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((Download _, QueueItem queueItem, QueueItem? _, CancellationToken _) =>
                {
                    queueItem.SourceFiles = new List<string> { firstAudioPath, secondAudioPath, txtPath };
                    return queueItem;
                });

            _services.AddSingleton(fileFinalizerMock.Object);
            _services.AddSingleton(importResolverMock.Object);
            Init();

            await _downloadRepository.AddAsync(new Download
            {
                Id = downloadId,
                Status = DownloadStatus.Downloading,
                DownloadClientId = "client-source-truth",
                Title = "Alpha Book",
                Metadata = new Dictionary<string, object>
                {
                    ["TorrentHash"] = "ABC123"
                }
            });

            var processor = MockUtils.CreateCompletedDownloadProcessor(_provider);
            await processor.ProcessCompletedDownloadAsync(downloadId, FileService.GetTempPath());

            Assert.NotNull(capturedFiles);
            Assert.Contains(firstAudioPath, capturedFiles!);
            Assert.Contains(secondAudioPath, capturedFiles!);
            Assert.Contains(txtPath, capturedFiles!);
            Assert.DoesNotContain(unrelatedPath, capturedFiles!);
        }

        [Fact]
        [Trait("Method", "ProcessCompletedDownloadAsync")]
        [Trait("Scenario", "NonAudioSingleFileFallsBackToDirectoryImport")]
        public async Task ProcessCompletedDownloadAsync_NonAudioSingleFile_UsesParentDirectoryWhenAudioExists()
        {
            var audioPath = await FileService.GetTempFileAsync("book.m4b");
            var coverPath = await FileService.GetTempFileAsync("book.jpg");

            var downloadId = Guid.NewGuid().ToString();

            string[]? capturedFiles = null;
            var fileFinalizerMock = new Mock<IFileFinalizer>();
            fileFinalizerMock
                .Setup(f => f.ImportFilesFromDirectoryAsync(downloadId, null, It.IsAny<IEnumerable<string>>(), It.IsAny<ApplicationSettings>()))
                .Callback<string, int?, IEnumerable<string>, ApplicationSettings>((_, _, files, _) => capturedFiles = files.ToArray())
                .ReturnsAsync((string _, int? _, IEnumerable<string> files, ApplicationSettings _) =>
                    [.. files.Select(f => new ImportResult
                    {
                        Success = true,
                        SourcePath = f,
                        FinalPath = f
                    })]);

            fileFinalizerMock
                .Setup(f => f.ImportSingleFileAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<ApplicationSettings>()))
                .Throws(new Xunit.Sdk.XunitException("single-file import should not run when a non-audio completion path has sibling audio"));

            _services.AddSingleton(fileFinalizerMock.Object);
            Init();

            await _downloadRepository.AddAsync(new Download
            {
                Id = downloadId,
                Status = DownloadStatus.Downloading,
                DownloadClientId = "client-cover-path",
                Title = "Book"
            });

            var processor = MockUtils.CreateCompletedDownloadProcessor(_provider);
            await processor.ProcessCompletedDownloadAsync(downloadId, coverPath);

            Assert.NotNull(capturedFiles);
            Assert.Contains(audioPath, capturedFiles!);
            Assert.Contains(coverPath, capturedFiles!);

            var tracked = await _downloadRepository.FindAsync(downloadId);
            Assert.NotNull(tracked);
            Assert.Equal(DownloadStatus.Moved, tracked!.Status);
            Assert.Equal(audioPath, tracked.FinalPath);
        }

        [Fact]
        [Trait("Method", "ProcessCompletedDownloadAsync")]
        [Trait("Scenario", "RecursiveDirectoryImportsNestedFile")]
        public async Task ProcessCompletedDownloadAsync_RecursiveDirectory_ImportsNestedFile()
        {
            var nested = FileService.GetTempDirectory("nested");
            var filePath = await FileService.GetFileAsync(nested, "file2.mp3");

            var downloadId = Guid.NewGuid().ToString();
            await _downloadRepository.AddAsync(new Download
            {
                Id = downloadId,
                Status = DownloadStatus.Downloading
            });

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(FileService.GetTempPath())
                .Build());

            var processor = MockUtils.CreateCompletedDownloadProcessor(_provider);
            await processor.ProcessCompletedDownloadAsync(downloadId, FileService.GetTempPath());

            var tracked = await _downloadRepository.FindAsync(downloadId);
            Assert.NotNull(tracked);
            Assert.True(tracked!.Status == DownloadStatus.Completed || tracked.Status == DownloadStatus.Moved, $"Expected Completed or Moved, got {tracked.Status}");
            Assert.NotEmpty(tracked.FinalPath);
        }

        [Fact]
        [Trait("Scenario", "ArchiveExtractionImportsContainedFile")]
        public async Task ProcessCompletedDownloadAsync_ArchiveExtraction_ImportsContainedFile()
        {
            var destinationDirectory = FileService.GetTempDirectory("destination");
            var inner = FileService.GetTempDirectory("inner");
            _ = await FileService.GetFileAsync(inner, "audio.mp3");
            var zipPath = Path.Join(FileService.GetTempPath(), "release.zip");
            ZipFile.CreateFromDirectory(inner, zipPath);
            Assert.True(File.Exists(zipPath));

            var downloadId = Guid.NewGuid().ToString();
            await _downloadRepository.AddAsync(new Download
            {
                Id = downloadId,
                Status = DownloadStatus.Downloading
            });

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithExtractArchive()
                .WithMultiFileNamingPattern("{Title}")
                .WithOutputPath(destinationDirectory)
                .WithoutMetadataProcessing()
                .Build());

            var processor = MockUtils.CreateCompletedDownloadProcessor(_provider);
            await processor.ProcessCompletedDownloadAsync(downloadId, zipPath);

            var tracked = await _downloadRepository.FindAsync(downloadId);
            Assert.NotNull(tracked);
            Assert.True(tracked!.Status == DownloadStatus.Completed || tracked.Status == DownloadStatus.Moved, $"Expected Completed or Moved, got {tracked.Status}");
            // FIXME: -01 gets added even though the MultiFileNamingPattern explicitely ask not to
            Assert.Contains("audio-01.mp3", tracked.FinalPath ?? string.Empty);
        }

        [Fact]
        [Trait("Method", "ProcessCompletedDownloadAsync")]
        [Trait("Scenario", "InvalidTransitionIsRejected")]
        public async Task InvalidTransition_IsRejectedAndLogged()
        {
            var queueMock = new Mock<IDownloadQueueService>();

            _services.AddSingleton(queueMock.Object);
            Init();

            var downloadId = Guid.NewGuid().ToString();
            await _downloadRepository.AddAsync(new Download
            {
                Id = downloadId,
                Status = DownloadStatus.Moved,
                DownloadClientId = "client-terminal",
                Title = "Already Imported",
                FinalPath = "C:\\library\\already-imported.m4b"
            });

            var processor = MockUtils.CreateCompletedDownloadProcessor(_provider);
            await processor.ProcessCompletedDownloadAsync(downloadId, FileUtils.GetAbsolutePath("temp", "should-not-run.m4b"));

            var tracked = await _downloadRepository.FindAsync(downloadId);
            Assert.NotNull(tracked);
            Assert.Equal(DownloadStatus.Moved, tracked!.Status);

            queueMock.Verify(q => q.GetQueueAsync(), Times.Never);
        }

        [Fact]
        [Trait("Method", "ProcessCompletedDownloadAsync")]
        public async Task ProcessCOmpleteDownloadAsync_MultipleFiles()
        {
            var remoteSource = FileService.GetTempDirectory("dl-remote-source");
            var localSource = FileService.GetTempDirectory("dl-local-source");
            var localDestination = FileService.GetTempDirectory("dl-destination");

            var remoteChapter1 = Path.Join(remoteSource, "01 - Seconde Fondation Isaac Asimov.mp3");
            var remoteChapter2 = Path.Join(remoteSource, "02 - Seconde Fondation Isaac Asimov.mp3");
            var remoteChapter3 = Path.Join(remoteSource, "03 - Seconde Fondation Isaac Asimov.mp3");
            var remoteChapter4 = Path.Join(remoteSource, "04 - Seconde Fondation Isaac Asimov.mp3");
            var remoteCompanion = Path.Join(remoteSource, "Seconde Fondation Isaac Asimov.nfo");

            var localChapter1 = await FileService.GetFileAsync(localSource, "01 - Seconde Fondation Isaac Asimov.mp3");
            var localChapter2 = await FileService.GetFileAsync(localSource, "02 - Seconde Fondation Isaac Asimov.mp3");
            var localChapter3 = await FileService.GetFileAsync(localSource, "03 - Seconde Fondation Isaac Asimov.mp3");
            var localChapter4 = await FileService.GetFileAsync(localSource, "04 - Seconde Fondation Isaac Asimov.mp3");
            var localCompanion = await FileService.GetFileAsync(localSource, "Seconde Fondation Isaac Asimov.nfo");

            var importItemResolutionServiceMock = new Mock<IImportItemResolutionService>();
            importItemResolutionServiceMock
                .Setup(r => r.ResolveImportItemAsync(
                    It.Is<Download>(d => d.Id == DOWNLOAD_COMPLETE_ID),
                    It.IsAny<QueueItem>(),
                    It.IsAny<QueueItem?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((Download download, QueueItem queueItem, QueueItem? previousAttempt, CancellationToken ct) =>
                {
                    queueItem.SourceFiles = new List<string> {
                        remoteChapter1,
                        remoteChapter2,
                        remoteChapter3,
                        remoteChapter4,
                        remoteCompanion
                    };
                    return queueItem;
                });

            _services.AddSingleton(importItemResolutionServiceMock.Object);
            Init();
            await InitDataAsync();

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithMoveFileOnCompleted()
                .WithMultiFileNamingPattern("{Title}-{DiskNumber:00}-{ChapterNumber:00}")
                .WithImportBlacklistExtension(".nfo")
                .Build());

            var client = await _downloadClientConfigurationRepository.GetByIdAsync(CLIENT_CONFIG_ID);
            var download = (await _downloadRepository.GetByIdsAsync([DOWNLOAD_COMPLETE_ID])).First();

            await _remotePathMappingRepository.SaveAsync(new RemotePathMapping
            {
                Id = 1,
                DownloadClientId = CLIENT_CONFIG_ID,
                Name = "TEST_REMOTE_MAPPING",
                RemotePath = remoteSource,
                LocalPath = localSource,
            });

            var basePath = Path.Join(localDestination, "Isaac Asimov", "Le Cycle de Fondation", "Seconde Fondation");

            await _audiobookRepository.AddAsync(new Audiobook
            {
                Id = AUDIOBOOK_ID,
                Title = "Seconde Fondation",
                Authors = [
                    "Isaac Asimov"
                ],
                PublishYear = "1996",
                Series = "Le Cycle de Fondation",
                BasePath = basePath
            });

            var completeDownloadProcessor = MockUtils.CreateCompletedDownloadProcessor(_provider);
            await completeDownloadProcessor.ProcessCompletedDownloadAsync(download.Id, localSource);

            var audiobook = await _audiobookRepository.GetByIdAsync(AUDIOBOOK_ID);
            var files = await _audiobookFileRepository.GetAllAsync();

            // FIXME: disc and track number are the same because ffprobe metadata are not used (see ImportService.ImportFilesFromDirectoryAsync)
            Assert.True(File.Exists(Path.Join(basePath, "Seconde Fondation-01-01.mp3")));
            Assert.True(File.Exists(Path.Join(basePath, "Seconde Fondation-02-02.mp3")));
            Assert.True(File.Exists(Path.Join(basePath, "Seconde Fondation-03-03.mp3")));
            Assert.True(File.Exists(Path.Join(basePath, "Seconde Fondation-04-04.mp3")));
            Assert.False(File.Exists(Path.Join(basePath, "Seconde Fondation Isaac Asimov.nfo")));
        }
    }
}
