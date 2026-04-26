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
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using Listenarr.Api.Services;
using Listenarr.Tests.Common;
using Listenarr.Tests.Builders;
using Listenarr.Domain.Models;

namespace Listenarr.Tests.Features.Api.Services
{
    [Trait("Name", "DownloadProcessing_FileMissingRetryTests")]
    [Trait("Category", "DownloadProcessing")]
    public class DownloadProcessing_FileMissingRetryTests : BaseTests
    {
        public override async Task InitializeAsync()
        {
            var downloadServiceMock = new Mock<IDownloadService>();
            _services.AddSingleton(downloadServiceMock);
            _services.AddSingleton(downloadServiceMock.Object);
            Init();
        }

        [Fact]
        [Trait("Method", "ProcessMoveOrCopyJobAsync")]
        public async Task ProcessMoveOrCopy_IfSourceMissing_SchedulesRetryAndRecordsMetric()
        {
            var destRoot = FileService.GetTempDirectory("dl-dest");
            var sourceFile = await FileService.GetTempFileAsync("dl-missing.mp3");

            var dl = new DownloadBuilder()
                .WithId("missing-test-1")
                .WithCompletedStatus(DateTime.UtcNow)
                .WithStartDate(DateTime.UtcNow)
                .WithPath(sourceFile)
                .Build();
            await _downloadRepository.AddAsync(dl);

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(destRoot)
                .WithMoveFileOnCompleted()
                .WithoutMetadataProcessing()
                .Build());

            // Enqueue the job pointing to the source file
            var queueService = _provider.GetRequiredService<IDownloadProcessingQueueService>();
            var jobId = await queueService.QueueDownloadProcessingAsync(dl.Id, sourceFile, null);
            var job = await queueService.GetJobAsync(jobId);

            // Delete the source to simulate disappearance before processing
            File.Delete(sourceFile);

            // Create the background service instance (no longer needs importItemResolution in constructor)
            var svc = _provider.GetRequiredService<DownloadProcessingBackgroundService>();

            // Set job to processing (the outer loop normally does this)
            job.Status = ProcessingJobStatus.Processing;
            job.StartedAt = DateTime.UtcNow;
            await queueService.UpdateJobAsync(job);

            using var scope = _provider.CreateScope();

            var method = typeof(DownloadProcessingBackgroundService).GetMethod("ProcessMoveOrCopyJobAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            // Invoke and await the returned Task
            var task = (Task)method!.Invoke(svc, [job, scope, CancellationToken.None])!;
            await task;

            // Persist job updates (ProcessQueueAsync usually updates job at the end)
            await queueService.UpdateJobAsync(job);

            // Reload job
            var updated = await queueService.GetJobAsync(job.Id);
            Assert.NotNull(updated);
            Assert.Equal(ProcessingJobStatus.Retry, updated!.Status);
            Assert.True(updated.RetryCount >= 1);
            Assert.Contains("not found", updated.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            // Ensure metrics increment was recorded for missing source
            var appMetricsServiceMock = _provider.GetRequiredService<Mock<IAppMetricsService>>();
            appMetricsServiceMock.Verify(m => m.Increment("processing.source_missing", It.IsAny<double>()), Times.AtLeastOnce);
        }

        [Fact]
        [Trait("Method", "ProcessMoveOrCopyJobAsync")]
        public async Task ProcessMoveOrCopy_DirectorySource_FinalizesOnceAfterCompanionFilesAreCopied()
        {
            var sourceDir = FileService.GetTempDirectory("dl-dir");
            var destRoot = FileService.GetTempDirectory("dl-dest");

            var audioPath = await FileService.GetFileAsync(sourceDir, "book.m4b");
            var coverPath = await FileService.GetFileAsync(sourceDir, "cover.jpg");
            var txtPath = await FileService.GetFileAsync(sourceDir, "book.txt");

            var download = new DownloadBuilder()
                .WithId("dir-batch-test-1")
                .WithCompletedStatus(DateTime.UtcNow)
                .WithPath(sourceDir)
                .WithStartDate(DateTime.UtcNow)
                .Build();

            await _downloadRepository.AddAsync(download);

            var job = new DownloadProcessingJob
            {
                Id = "job-dir-batch-1",
                DownloadId = download.Id,
                JobType = ProcessingJobType.MoveOrCopyFile,
                Status = ProcessingJobStatus.Processing,
                SourcePath = sourceDir
            };

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(destRoot)
                .WithMoveFileOnCompleted()
                .WithoutMetadataProcessing()
                .Build());

            var expectedAudioDest = Path.Join(destRoot, "book.m4b");
            var expectedCoverDest = Path.Join(destRoot, "cover.jpg");
            var expectedTxtDest = Path.Join(destRoot, "book.txt");

            Assert.False(File.Exists(expectedAudioDest));
            Assert.False(File.Exists(expectedCoverDest));
            Assert.False(File.Exists(expectedTxtDest));
            Assert.Equal(sourceDir, download.DownloadPath);
            Assert.Null(job.DestinationPath);

            var method = typeof(DownloadProcessingBackgroundService).GetMethod("ProcessMoveOrCopyJobAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            var svc = _provider.GetRequiredService<DownloadProcessingBackgroundService>();
            using var scope = _provider.CreateScope();

            var task = (Task)method!.Invoke(svc, [job, scope, CancellationToken.None])!;
            await task;

            Assert.True(File.Exists(expectedAudioDest));
            Assert.True(File.Exists(expectedCoverDest));
            Assert.True(File.Exists(expectedTxtDest));
            Assert.Equal(sourceDir, download.DownloadPath);
            Assert.Equal(destRoot, job.DestinationPath);

            var downloadServiceMock = _provider.GetRequiredService<Mock<IDownloadService>>();
            downloadServiceMock.Verify(d => d.ProcessCompletedDownloadAsync(download.Id, sourceDir), Times.Once);
        }

        [Fact]
        [Trait("Method", "ProcessMoveOrCopyJobAsync")]
        public async Task ProcessMoveOrCopy_DirectorySource_UsesClientReportedFilesToExcludeUnrelatedFiles()
        {
            var sourceDir = FileService.GetTempDirectory($"dl-dir");
            var destRoot = FileService.GetTempDirectory($"dl-dest");

            var audioPath = await FileService.GetFileAsync(sourceDir, "book.m4b");
            var coverPath = await FileService.GetFileAsync(sourceDir, "cover.jpg");
            var txtPath = await FileService.GetFileAsync(sourceDir, "book.txt");
            var unrelatedPath = await FileService.GetFileAsync(sourceDir, "unrelated.txt");

            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithId("client-1")
                .Build());

            var download = new DownloadBuilder()
                .WithId("dir-batch-client-scope-1")
                .WithCompletedStatus(DateTime.UtcNow)
                .WithPath(sourceDir)
                .WithStartDate(DateTime.UtcNow)
                .WithDownloadClientConfiguration(client)
                .WithTorrentHash("ABC123")
                .Build();
            await _downloadRepository.AddAsync(download);

            var job = new DownloadProcessingJob
            {
                Id = "job-dir-batch-client-scope-1",
                DownloadId = download.Id,
                JobType = ProcessingJobType.MoveOrCopyFile,
                Status = ProcessingJobStatus.Processing,
                SourcePath = sourceDir
            };

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(destRoot)
                .WithCopyFileOnCompleted()
                .WithoutMetadataProcessing()
                .Build());

            var importResolverMock = _provider.GetRequiredService<Mock<IImportItemResolutionService>>();
            importResolverMock
                .Setup(r => r.ResolveImportItemAsync(
                    It.Is<Download>(d => d.Id == download.Id),
                    It.IsAny<QueueItem>(),
                    It.IsAny<QueueItem?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((Download download, QueueItem queueItem, QueueItem? previousAttempt, CancellationToken ct) =>
                {
                    queueItem.SourceFiles = [audioPath, coverPath, txtPath];
                    return queueItem;
                });

            string? processedPath = null;
            var downloadServiceMock = _provider.GetRequiredService<Mock<IDownloadService>>();
            downloadServiceMock
                .Setup(d => d.ProcessCompletedDownloadAsync(download.Id, It.IsAny<string>()))
                .Callback<string, string>(async (id, path) =>
                {
                    processedPath = path;
                    var tracked = await _downloadRepository.GetByIdAsync(id);
                    if (tracked != null)
                    {
                        tracked.Status = DownloadStatus.Moved;
                        tracked.FinalPath = path;
                        await _downloadRepository.UpdateAsync(tracked);
                    }
                })
                .Returns(Task.CompletedTask);

            using var scope = _provider.CreateScope();
            var method = typeof(DownloadProcessingBackgroundService).GetMethod("ProcessMoveOrCopyJobAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            var svc = _provider.GetRequiredService<DownloadProcessingBackgroundService>();
            var task = (Task)method!.Invoke(svc, [job, scope, CancellationToken.None])!;
            await task;

            Assert.True(File.Exists(Path.Join(destRoot, "book.m4b")));
            Assert.True(File.Exists(Path.Join(destRoot, "cover.jpg")));
            Assert.True(File.Exists(Path.Join(destRoot, "book.txt")));
            Assert.False(File.Exists(Path.Join(destRoot, "unrelated.txt")));
            Assert.Equal(sourceDir, processedPath);
        }
    }
}

