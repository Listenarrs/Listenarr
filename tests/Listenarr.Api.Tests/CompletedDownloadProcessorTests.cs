using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Listenarr.Api.Services;
using Listenarr.Infrastructure.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using System.IO.Compression;
using System.Reflection;
using Listenarr.Infrastructure.Repositories;
using Listenarr.Application.Services;

namespace Listenarr.Api.Tests
{
    [Trait("Area", "CompletedDownloadProcessing")]
    public class CompletedDownloadProcessorTests
    {
        [Fact]
        [Trait("Scenario", "TransientFailureStaysImportPending")]
        public async Task MarkImportFailureAsync_FirstAttempt_KeepsImportPendingForRetry()
        {
            var downloadId = Guid.NewGuid().ToString();

            var repo = new TestDownloadRepository();
            await repo.AddAsync(new Download
            {
                Id = downloadId,
                Status = DownloadStatus.Downloading,
                DownloadClientId = "client-retry",
                Title = "Retry Candidate"
            });

            var fileFinalizer = new TestFileFinalizer(null);

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings());

            var toastMock = new Mock<IToastService>();
            toastMock
                .Setup(t => t.PublishToastAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<int?>()))
                .Returns(Task.CompletedTask);

            var scopeFactoryMock = new Mock<IServiceScopeFactory>();
            var scopeMock = new Mock<IServiceScope>();
            var spMock = new Mock<IServiceProvider>();
            spMock.Setup(sp => sp.GetService(typeof(ListenArrDbContext))).Returns(null);
            spMock.Setup(sp => sp.GetService(typeof(IToastService))).Returns(toastMock.Object);
            scopeMock.Setup(s => s.ServiceProvider).Returns(spMock.Object);
            scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

            var importMock = new Mock<IImportService>();
            var queueMock = new Mock<IDownloadQueueService>();
            queueMock.Setup(q => q.GetQueueAsync()).ReturnsAsync(new List<Listenarr.Domain.Models.QueueItem>());
            var hubContextMock = new Mock<IHubContext<Listenarr.Api.Hubs.DownloadHub>>();
            var loggerMock = new Mock<ILogger<CompletedDownloadProcessor>>();
            var archiveExtractor = new ArchiveExtractor(new Mock<ILogger<ArchiveExtractor>>().Object);

            var downloadHistoryMock = new Mock<IDownloadHistoryService>();
            downloadHistoryMock
                .Setup(h => h.RecordImportFailedAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>()))
                .Returns(Task.CompletedTask);

            var processor = new CompletedDownloadProcessor(
                repo,
                fileFinalizer,
                configMock.Object,
                scopeFactoryMock.Object,
                importMock.Object,
                archiveExtractor,
                queueMock.Object,
                hubContextMock.Object,
                loggerMock.Object,
                hubBroadcaster: null,
                metrics: null,
                downloadHistoryService: downloadHistoryMock.Object);

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

            var tracked = await repo.FindAsync(downloadId);
            Assert.NotNull(tracked);
            Assert.Equal(DownloadStatus.ImportPending, tracked!.Status);
            Assert.Equal(1, tracked.ImportAttempts);
            Assert.Null(tracked.ImportBlockReason);
            Assert.True(tracked.ImportBlockMessages == null || tracked.ImportBlockMessages.Count == 0);
            Assert.Contains("boom", tracked.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            downloadHistoryMock.Verify(h => h.RecordImportFailedAsync(
                downloadId,
                "client-retry",
                "Retry Candidate",
                It.Is<string>(msg => msg.Contains("boom", StringComparison.OrdinalIgnoreCase))), Times.Once);

            toastMock.Verify(t => t.PublishToastAsync(
                "warning",
                "Manual Interaction Required",
                It.IsAny<string>(),
                It.IsAny<int?>()), Times.Never);
        }

        [Fact]
        [Trait("Scenario", "ThresholdFailureBlocksAndSignalsManualInteraction")]
        public async Task MarkImportFailureAsync_ThirdAttempt_BlocksAndSignalsManualInteraction()
        {
            var downloadId = Guid.NewGuid().ToString();

            var repo = new TestDownloadRepository();
            await repo.AddAsync(new Download
            {
                Id = downloadId,
                Status = DownloadStatus.ImportPending,
                DownloadClientId = "client-threshold",
                Title = "Threshold Candidate",
                ImportAttempts = 2
            });

            var fileFinalizer = new TestFileFinalizer(null);

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings());

            var toastMock = new Mock<IToastService>();
            toastMock
                .Setup(t => t.PublishToastAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<int?>()))
                .Returns(Task.CompletedTask);

            Listenarr.Domain.Models.History? capturedHistory = null;
            var historyRepoMock = new Mock<IHistoryRepository>();
            historyRepoMock
                .Setup(h => h.AddAsync(It.IsAny<Listenarr.Domain.Models.History>()))
                .Callback<Listenarr.Domain.Models.History>(h => capturedHistory = h)
                .ReturnsAsync((Listenarr.Domain.Models.History h) => h);

            var scopeFactoryMock = new Mock<IServiceScopeFactory>();
            var scopeMock = new Mock<IServiceScope>();
            var spMock = new Mock<IServiceProvider>();
            spMock.Setup(sp => sp.GetService(typeof(ListenArrDbContext))).Returns(null);
            spMock.Setup(sp => sp.GetService(typeof(IToastService))).Returns(toastMock.Object);
            spMock.Setup(sp => sp.GetService(typeof(IHistoryRepository))).Returns(historyRepoMock.Object);
            scopeMock.Setup(s => s.ServiceProvider).Returns(spMock.Object);
            scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

            var importMock = new Mock<IImportService>();
            var queueMock = new Mock<IDownloadQueueService>();
            queueMock.Setup(q => q.GetQueueAsync()).ReturnsAsync(new List<Listenarr.Domain.Models.QueueItem>());
            var hubContextMock = new Mock<IHubContext<Listenarr.Api.Hubs.DownloadHub>>();
            var loggerMock = new Mock<ILogger<CompletedDownloadProcessor>>();
            var archiveExtractor = new ArchiveExtractor(new Mock<ILogger<ArchiveExtractor>>().Object);

            var downloadHistoryMock = new Mock<IDownloadHistoryService>();
            downloadHistoryMock
                .Setup(h => h.RecordImportFailedAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>()))
                .Returns(Task.CompletedTask);

            var processor = new CompletedDownloadProcessor(
                repo,
                fileFinalizer,
                configMock.Object,
                scopeFactoryMock.Object,
                importMock.Object,
                archiveExtractor,
                queueMock.Object,
                hubContextMock.Object,
                loggerMock.Object,
                hubBroadcaster: null,
                metrics: null,
                downloadHistoryService: downloadHistoryMock.Object);

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

            var tracked = await repo.FindAsync(downloadId);
            Assert.NotNull(tracked);
            Assert.Equal(DownloadStatus.ImportBlocked, tracked!.Status);
            Assert.Equal(3, tracked.ImportAttempts);
            Assert.Equal("RepeatedFailure", tracked.ImportBlockReason);
            Assert.NotNull(tracked.ImportBlockMessages);
            Assert.Contains(tracked.ImportBlockMessages!, m => m.Contains("still failing", StringComparison.OrdinalIgnoreCase));

            downloadHistoryMock.Verify(h => h.RecordImportFailedAsync(
                downloadId,
                "client-threshold",
                "Threshold Candidate",
                It.Is<string>(msg => msg.Contains("still failing", StringComparison.OrdinalIgnoreCase))), Times.Once);

            toastMock.Verify(t => t.PublishToastAsync(
                "warning",
                "Manual Interaction Required",
                It.Is<string>(msg => msg.Contains("could not be imported automatically", StringComparison.OrdinalIgnoreCase)),
                8000), Times.Once);

            historyRepoMock.Verify(h => h.AddAsync(It.IsAny<Listenarr.Domain.Models.History>()), Times.Once);
            Assert.NotNull(capturedHistory);
            Assert.Equal("ImportBlocked", capturedHistory!.EventType);
            Assert.Contains("still failing", capturedHistory.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        [Trait("Scenario", "AttemptCounterPersistsAcrossRestart")]
        public async Task MarkImportFailureAsync_AttemptCounterPersistsAcrossProcessorRestartSimulation()
        {
            var downloadId = Guid.NewGuid().ToString();

            var repo = new TestDownloadRepository();
            await repo.AddAsync(new Download
            {
                Id = downloadId,
                Status = DownloadStatus.ImportPending,
                DownloadClientId = "client-persist",
                Title = "Persistent Attempts",
                ImportAttempts = 1
            });

            CompletedDownloadProcessor BuildProcessor()
            {
                var fileFinalizer = new TestFileFinalizer(null);
                var configMock = new Mock<IConfigurationService>();
                configMock.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings());

                var toastMock = new Mock<IToastService>();
                toastMock
                    .Setup(t => t.PublishToastAsync(
                        It.IsAny<string>(),
                        It.IsAny<string>(),
                        It.IsAny<string>(),
                        It.IsAny<int?>()))
                    .Returns(Task.CompletedTask);

                var scopeFactoryMock = new Mock<IServiceScopeFactory>();
                var scopeMock = new Mock<IServiceScope>();
                var spMock = new Mock<IServiceProvider>();
                spMock.Setup(sp => sp.GetService(typeof(ListenArrDbContext))).Returns(null);
                spMock.Setup(sp => sp.GetService(typeof(IToastService))).Returns(toastMock.Object);
                scopeMock.Setup(s => s.ServiceProvider).Returns(spMock.Object);
                scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

                var importMock = new Mock<IImportService>();
                var queueMock = new Mock<IDownloadQueueService>();
                queueMock.Setup(q => q.GetQueueAsync()).ReturnsAsync(new List<Listenarr.Domain.Models.QueueItem>());
                var hubContextMock = new Mock<IHubContext<Listenarr.Api.Hubs.DownloadHub>>();
                var loggerMock = new Mock<ILogger<CompletedDownloadProcessor>>();
                var archiveExtractor = new ArchiveExtractor(new Mock<ILogger<ArchiveExtractor>>().Object);

                var downloadHistoryMock = new Mock<IDownloadHistoryService>();
                downloadHistoryMock
                    .Setup(h => h.RecordImportFailedAsync(
                        It.IsAny<string>(),
                        It.IsAny<string>(),
                        It.IsAny<string>(),
                        It.IsAny<string?>()))
                    .Returns(Task.CompletedTask);

                return new CompletedDownloadProcessor(
                    repo,
                    fileFinalizer,
                    configMock.Object,
                    scopeFactoryMock.Object,
                    importMock.Object,
                    archiveExtractor,
                    queueMock.Object,
                    hubContextMock.Object,
                    loggerMock.Object,
                    hubBroadcaster: null,
                    metrics: null,
                    downloadHistoryService: downloadHistoryMock.Object);
            }

            var method = typeof(CompletedDownloadProcessor)
                .GetMethod("MarkImportFailureAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var firstProcessor = BuildProcessor();
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

            var afterFirst = await repo.FindAsync(downloadId);
            Assert.NotNull(afterFirst);
            Assert.Equal(DownloadStatus.ImportPending, afterFirst!.Status);
            Assert.Equal(2, afterFirst.ImportAttempts);

            var restartedProcessor = BuildProcessor();
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

            var afterSecond = await repo.FindAsync(downloadId);
            Assert.NotNull(afterSecond);
            Assert.Equal(DownloadStatus.ImportBlocked, afterSecond!.Status);
            Assert.Equal(3, afterSecond.ImportAttempts);
            Assert.Equal("TransientFailure", afterSecond.ImportBlockReason);
        }

        [Fact]
        [Trait("Scenario", "NoImportableFilesBlocksAndSignalsManualInteraction")]
        public async Task ProcessCompletedDownloadAsync_NoImportableFiles_BlocksDownload_AndSignalsManualInteraction()
        {
            var downloadId = Guid.NewGuid().ToString();

            var repo = new TestDownloadRepository();
            await repo.AddAsync(new Download
            {
                Id = downloadId,
                Status = DownloadStatus.Downloading,
                DownloadClientId = "client-1",
                Title = "Broken Import"
            });

            var fileFinalizer = new TestFileFinalizer(null);

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings());

            var toastMock = new Mock<IToastService>();
            toastMock
                .Setup(t => t.PublishToastAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<int?>()))
                .Returns(Task.CompletedTask);

            Listenarr.Domain.Models.History? capturedHistory = null;
            var historyRepoMock = new Mock<IHistoryRepository>();
            historyRepoMock
                .Setup(h => h.AddAsync(It.IsAny<Listenarr.Domain.Models.History>()))
                .Callback<Listenarr.Domain.Models.History>(h => capturedHistory = h)
                .ReturnsAsync((Listenarr.Domain.Models.History h) => h);

            var downloadHistoryMock = new Mock<IDownloadHistoryService>();
            downloadHistoryMock
                .Setup(h => h.RecordImportFailedAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>()))
                .Returns(Task.CompletedTask);

            var scopeFactoryMock = new Mock<IServiceScopeFactory>();
            var scopeMock = new Mock<IServiceScope>();
            var spMock = new Mock<IServiceProvider>();
            spMock.Setup(sp => sp.GetService(typeof(ListenArrDbContext))).Returns(null);
            spMock.Setup(sp => sp.GetService(typeof(IToastService))).Returns(toastMock.Object);
            spMock.Setup(sp => sp.GetService(typeof(IHistoryRepository))).Returns(historyRepoMock.Object);
            scopeMock.Setup(s => s.ServiceProvider).Returns(spMock.Object);
            scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

            var importMock = new Mock<IImportService>();
            var queueMock = new Mock<IDownloadQueueService>();
            queueMock.Setup(q => q.GetQueueAsync()).ReturnsAsync(new List<Listenarr.Domain.Models.QueueItem>());
            var hubContextMock = new Mock<IHubContext<Listenarr.Api.Hubs.DownloadHub>>();
            var loggerMock = new Mock<ILogger<CompletedDownloadProcessor>>();
            var archiveExtractor = new ArchiveExtractor(new Mock<ILogger<ArchiveExtractor>>().Object);

            var processor = new CompletedDownloadProcessor(
                repo,
                fileFinalizer,
                configMock.Object,
                scopeFactoryMock.Object,
                importMock.Object,
                archiveExtractor,
                queueMock.Object,
                hubContextMock.Object,
                loggerMock.Object,
                hubBroadcaster: null,
                metrics: null,
                downloadHistoryService: downloadHistoryMock.Object);

            await processor.ProcessCompletedDownloadAsync(downloadId, string.Empty);

            var tracked = await repo.FindAsync(downloadId);
            Assert.NotNull(tracked);
            Assert.Equal(DownloadStatus.ImportBlocked, tracked!.Status);
            Assert.Equal("NoImportableFiles", tracked.ImportBlockReason);
            Assert.Equal(1, tracked.ImportAttempts);
            Assert.NotNull(tracked.ImportBlockMessages);
            Assert.Contains(tracked.ImportBlockMessages!, m => m.Contains("Manual interaction is required.", StringComparison.OrdinalIgnoreCase));

            downloadHistoryMock.Verify(h => h.RecordImportFailedAsync(
                downloadId,
                "client-1",
                "Broken Import",
                It.Is<string>(msg => msg.Contains("Manual interaction is required.", StringComparison.OrdinalIgnoreCase))), Times.Once);

            toastMock.Verify(t => t.PublishToastAsync(
                "warning",
                "Manual Interaction Required",
                It.Is<string>(msg => msg.Contains("could not be imported automatically", StringComparison.OrdinalIgnoreCase)),
                8000), Times.Once);

            historyRepoMock.Verify(h => h.AddAsync(It.IsAny<Listenarr.Domain.Models.History>()), Times.Once);
            Assert.NotNull(capturedHistory);
            Assert.Equal("ImportBlocked", capturedHistory!.EventType);
            Assert.Contains("Manual interaction is required.", capturedHistory.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        [Trait("Scenario", "SingleFileImportSetsMovedAndFinalPath")]
        public async Task ProcessCompletedDownloadAsync_SingleFile_UpdatesFinalPathAndStatus()
        {
            // Arrange
            var downloadId = Guid.NewGuid().ToString();
            var finalPath = "C:\\temp\\audiobook.mp3";

            var repo = new TestDownloadRepository();
            await repo.AddAsync(new Download { Id = downloadId, Status = DownloadStatus.Downloading, AudiobookId = null });

            var fileFinalizer = new TestFileFinalizer(null);

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings());

            var scopeFactoryMock = new Mock<IServiceScopeFactory>();
            var scopeMock = new Mock<IServiceScope>();
            var spMock = new Mock<IServiceProvider>();
            spMock.Setup(sp => sp.GetService(typeof(ListenArrDbContext))).Returns(null);
            scopeMock.Setup(s => s.ServiceProvider).Returns(spMock.Object);
            scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

            var importMock = new Mock<IImportService>();

            var queueMock = new Mock<IDownloadQueueService>();
            queueMock.Setup(q => q.GetQueueAsync()).ReturnsAsync(new List<Listenarr.Domain.Models.QueueItem>());

            var hubContextMock = new Mock<IHubContext<Listenarr.Api.Hubs.DownloadHub>>();
            var loggerMock = new Mock<ILogger<CompletedDownloadProcessor>>();

            var archiveExtractor = new ArchiveExtractor(new Mock<ILogger<ArchiveExtractor>>().Object);

            var processor = new CompletedDownloadProcessor(repo, fileFinalizer, configMock.Object, scopeFactoryMock.Object, importMock.Object, archiveExtractor, queueMock.Object, hubContextMock.Object, loggerMock.Object);

            // Act
            await processor.ProcessCompletedDownloadAsync(downloadId, finalPath);

            // Assert
            var tracked = await repo.FindAsync(downloadId);
            Assert.NotNull(tracked);
            Assert.True(tracked!.Status == DownloadStatus.Completed || tracked.Status == DownloadStatus.Moved, $"Expected Completed or Moved, got {tracked.Status}");
            // TestFileFinalizer returns FinalPath equal to source when no import service; so FinalPath should be set
            Assert.Equal(finalPath, tracked.FinalPath);
        }

        [Fact]
        [Trait("Scenario", "DirectoryImportInvokesDirectoryPathFlow")]
        public async Task ProcessCompletedDownloadAsync_Directory_InvokesDirectoryImport()
        {
            // Arrange
            var downloadId = Guid.NewGuid().ToString();
            var tempDir = System.IO.Path.Join(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
            System.IO.Directory.CreateDirectory(tempDir);
            var filePath = System.IO.Path.Join(tempDir, "file1.mp3");
            System.IO.File.WriteAllText(filePath, "dummy");

            var repo = new TestDownloadRepository();
            await repo.AddAsync(new Download { Id = downloadId, Status = DownloadStatus.Downloading });

            var fileFinalizer = new TestFileFinalizer(null);

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings());

            var scopeFactoryMock = new Mock<IServiceScopeFactory>();
            var scopeMock = new Mock<IServiceScope>();
            var spMock = new Mock<IServiceProvider>();
            spMock.Setup(sp => sp.GetService(typeof(ListenArrDbContext))).Returns(null);
            scopeMock.Setup(s => s.ServiceProvider).Returns(spMock.Object);
            scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

            var importMock = new Mock<IImportService>();
            var queueMock = new Mock<IDownloadQueueService>();
            queueMock.Setup(q => q.GetQueueAsync()).ReturnsAsync(new List<Listenarr.Domain.Models.QueueItem>());
            var hubContextMock = new Mock<IHubContext<Listenarr.Api.Hubs.DownloadHub>>();
            var loggerMock = new Mock<ILogger<CompletedDownloadProcessor>>();
            var archiveExtractor = new ArchiveExtractor(new Mock<ILogger<ArchiveExtractor>>().Object);

            var processor = new CompletedDownloadProcessor(repo, fileFinalizer, configMock.Object, scopeFactoryMock.Object, importMock.Object, archiveExtractor, queueMock.Object, hubContextMock.Object, loggerMock.Object);

            // Act
            await processor.ProcessCompletedDownloadAsync(downloadId, tempDir);

            // Assert
            var tracked = await repo.FindAsync(downloadId);
            Assert.NotNull(tracked);
            Assert.True(tracked!.Status == DownloadStatus.Completed || tracked.Status == DownloadStatus.Moved, $"Expected Completed or Moved, got {tracked.Status}");

            // cleanup
            TryDeleteFile(filePath);
            TryDeleteDirectory(tempDir);
        }

        [Fact]
        [Trait("Scenario", "RecursiveDirectoryImportsNestedFile")]
        public async Task ProcessCompletedDownloadAsync_RecursiveDirectory_ImportsNestedFile()
        {
            var downloadId = Guid.NewGuid().ToString();
            var tempDir = System.IO.Path.Join(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
            var nested = System.IO.Path.Join(tempDir, "nested");
            System.IO.Directory.CreateDirectory(nested);
            var filePath = System.IO.Path.Join(nested, "file2.mp3");
            System.IO.File.WriteAllText(filePath, "dummy");

            var repo = new TestDownloadRepository();
            await repo.AddAsync(new Download { Id = downloadId, Status = DownloadStatus.Downloading });

            var fileFinalizer = new TestFileFinalizer(null);

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings());

            var scopeFactoryMock = new Mock<IServiceScopeFactory>();
            var scopeMock = new Mock<IServiceScope>();
            var spMock = new Mock<IServiceProvider>();
            spMock.Setup(sp => sp.GetService(typeof(ListenArrDbContext))).Returns(null);
            scopeMock.Setup(s => s.ServiceProvider).Returns(spMock.Object);
            scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

            var importMock = new Mock<IImportService>();
            var queueMock = new Mock<IDownloadQueueService>();
            queueMock.Setup(q => q.GetQueueAsync()).ReturnsAsync(new List<Listenarr.Domain.Models.QueueItem>());
            var hubContextMock = new Mock<IHubContext<Listenarr.Api.Hubs.DownloadHub>>();
            var loggerMock = new Mock<ILogger<CompletedDownloadProcessor>>();
            var archiveExtractor = new ArchiveExtractor(new Mock<ILogger<ArchiveExtractor>>().Object);

            var processor = new CompletedDownloadProcessor(repo, fileFinalizer, configMock.Object, scopeFactoryMock.Object, importMock.Object, archiveExtractor, queueMock.Object, hubContextMock.Object, loggerMock.Object);

            await processor.ProcessCompletedDownloadAsync(downloadId, tempDir);

            var tracked = await repo.FindAsync(downloadId);
            Assert.NotNull(tracked);
            Assert.True(tracked!.Status == DownloadStatus.Completed || tracked.Status == DownloadStatus.Moved, $"Expected Completed or Moved, got {tracked.Status}");
            Assert.Equal(filePath, tracked.FinalPath);

            // cleanup
            TryDeleteFile(filePath);
            TryDeleteDirectory(tempDir, recursive: true);
        }

        [Fact]
        [Trait("Scenario", "ArchiveExtractionImportsContainedFile")]
        public async Task ProcessCompletedDownloadAsync_ArchiveExtraction_ImportsContainedFile()
        {
            var downloadId = Guid.NewGuid().ToString();
            var tempDir = System.IO.Path.Join(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
            var inner = System.IO.Path.Join(tempDir, "inner");
            System.IO.Directory.CreateDirectory(inner);
            var audioPath = System.IO.Path.Join(inner, "audio.mp3");
            System.IO.File.WriteAllText(audioPath, "dummy");

            var zipPath = System.IO.Path.Join(tempDir, "release.zip");
            ZipFile.CreateFromDirectory(inner, zipPath);

            var repo = new TestDownloadRepository();
            await repo.AddAsync(new Download { Id = downloadId, Status = DownloadStatus.Downloading });

            var fileFinalizer = new TestFileFinalizer(null);

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings());

            var scopeFactoryMock = new Mock<IServiceScopeFactory>();
            var scopeMock = new Mock<IServiceScope>();
            var spMock = new Mock<IServiceProvider>();
            spMock.Setup(sp => sp.GetService(typeof(ListenArrDbContext))).Returns(null);
            scopeMock.Setup(s => s.ServiceProvider).Returns(spMock.Object);
            scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

            var importMock = new Mock<IImportService>();
            var queueMock = new Mock<IDownloadQueueService>();
            queueMock.Setup(q => q.GetQueueAsync()).ReturnsAsync(new List<Listenarr.Domain.Models.QueueItem>());
            var hubContextMock = new Mock<IHubContext<Listenarr.Api.Hubs.DownloadHub>>();
            var loggerMock = new Mock<ILogger<CompletedDownloadProcessor>>();
            var archiveExtractor = new ArchiveExtractor(new Mock<ILogger<ArchiveExtractor>>().Object);

            var processor = new CompletedDownloadProcessor(repo, fileFinalizer, configMock.Object, scopeFactoryMock.Object, importMock.Object, archiveExtractor, queueMock.Object, hubContextMock.Object, loggerMock.Object);

            await processor.ProcessCompletedDownloadAsync(downloadId, zipPath);

            var tracked = await repo.FindAsync(downloadId);
            Assert.NotNull(tracked);
            Assert.True(tracked!.Status == DownloadStatus.Completed || tracked.Status == DownloadStatus.Moved, $"Expected Completed or Moved, got {tracked.Status}");
            // FinalPath should have been updated to the extracted audio file path
            Assert.Contains("audio.mp3", tracked.FinalPath ?? string.Empty);

            // cleanup
            TryDeleteFile(zipPath);
            TryDeleteDirectory(tempDir, recursive: true);
        }

        [Fact]
        [Trait("Scenario", "InvalidTransitionIsRejected")]
        public async Task InvalidTransition_IsRejectedAndLogged()
        {
            var downloadId = Guid.NewGuid().ToString();

            var repo = new TestDownloadRepository();
            await repo.AddAsync(new Download
            {
                Id = downloadId,
                Status = DownloadStatus.Moved,
                DownloadClientId = "client-terminal",
                Title = "Already Imported",
                FinalPath = "C:\\library\\already-imported.m4b"
            });

            var fileFinalizer = new TestFileFinalizer(null);

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings());

            var scopeFactoryMock = new Mock<IServiceScopeFactory>();
            var scopeMock = new Mock<IServiceScope>();
            var spMock = new Mock<IServiceProvider>();
            spMock.Setup(sp => sp.GetService(typeof(ListenArrDbContext))).Returns(null);
            scopeMock.Setup(s => s.ServiceProvider).Returns(spMock.Object);
            scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

            var importMock = new Mock<IImportService>();
            var queueMock = new Mock<IDownloadQueueService>();
            queueMock.Setup(q => q.GetQueueAsync()).ReturnsAsync(new List<Listenarr.Domain.Models.QueueItem>());
            var hubContextMock = new Mock<IHubContext<Listenarr.Api.Hubs.DownloadHub>>();
            var loggerMock = new Mock<ILogger<CompletedDownloadProcessor>>();
            var archiveExtractor = new ArchiveExtractor(new Mock<ILogger<ArchiveExtractor>>().Object);

            var processor = new CompletedDownloadProcessor(
                repo,
                fileFinalizer,
                configMock.Object,
                scopeFactoryMock.Object,
                importMock.Object,
                archiveExtractor,
                queueMock.Object,
                hubContextMock.Object,
                loggerMock.Object);

            await processor.ProcessCompletedDownloadAsync(downloadId, "C:\\temp\\should-not-run.m4b");

            var tracked = await repo.FindAsync(downloadId);
            Assert.NotNull(tracked);
            Assert.Equal(DownloadStatus.Moved, tracked!.Status);

            queueMock.Verify(q => q.GetQueueAsync(), Times.Never);
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                System.IO.File.Delete(path);
            }
            catch (System.IO.IOException ex)
            {
                Console.Error.WriteLine($"Ignoring cleanup failure for '{path}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine($"Ignoring cleanup failure for '{path}': {ex.Message}");
            }
        }

        private static void TryDeleteDirectory(string path, bool recursive = false)
        {
            try
            {
                System.IO.Directory.Delete(path, recursive);
            }
            catch (System.IO.IOException ex)
            {
                Console.Error.WriteLine($"Ignoring cleanup failure for '{path}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine($"Ignoring cleanup failure for '{path}': {ex.Message}");
            }
        }
    }
}
