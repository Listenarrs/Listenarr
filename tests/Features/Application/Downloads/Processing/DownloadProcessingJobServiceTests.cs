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
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Downloads.Processing
{
    [Trait("Name", "DownloadProcessingJobServiceTests")]
    [Trait("Category", "DownloadProcessingJob")]
    public class DownloadProcessingJobServiceTests : BaseTests
    {
        [Fact]
        [Trait("Scenario", "Startup reset stuck processing jobs")]
        public async Task Startup_ResetStuckJobs()
        {
            await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithId("job-processing-1")
                .WithProcessing(at: DateTime.UtcNow)
                .Build());

            await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithId("job-pending-1")
                .WithPending(at: DateTime.UtcNow)
                .Build());

            var downloadProcessingJobService = _provider.GetRequiredService<IDownloadProcessingJobService>();
            await downloadProcessingJobService.ResetStuckJobsAsync(CancellationToken.None);

            var processingJob = await _downloadProcessingJobRepository.GetByIdAsync("job-processing-1");
            var pendingJob = await _downloadProcessingJobRepository.GetByIdAsync("job-pending-1");

            Assert.NotNull(processingJob);
            Assert.Equal(ProcessingJobStatus.Pending, processingJob!.Status);
            Assert.Contains(processingJob.ProcessingLog, m => m.Contains("stuck Processing state", StringComparison.OrdinalIgnoreCase));

            Assert.NotNull(pendingJob);
            Assert.Equal(ProcessingJobStatus.Pending, pendingJob!.Status);
        }

        [Theory]
        [InlineData("qbittorrent")]
        [InlineData("transmission")]
        [InlineData("sabnzbd")]
        [InlineData("nzbget")]
        [InlineData("slskd")]
        [InlineData("ddl")]
        public async Task DownloadProcessingJob_Queued_ForAnyClientType(string clientType)
        {
            var sourceDir = FileService.GetTempDirectory("listenarr-pipeline");
            var sourceFile = await FileService.GetFileAsync(sourceDir, "Pipeline Coverage.m4b");

            var outputDir = FileService.GetTempDirectory("listenarr-pipeline-out");

            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithName(clientType)
                .WithType(clientType)
                .WithEnabled()
                .Build());

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithCompletedStatus(DateTime.UtcNow)
                .WithDownloadClientConfiguration(client)
                .WithPath(sourceDir)
                .Build());

            // Act
            var downloadProcessingJobService = _provider.GetRequiredService<IDownloadProcessingJobService>();
            await downloadProcessingJobService.EnqueueAsync(download);

            var jobs = await _downloadProcessingJobRepository.GetRecentAsync(2);
            Assert.Single(jobs);

            var job = jobs.First();
            Assert.Equal(download.Id, job.DownloadId);
        }

        [Fact]
        [Trait("Scenario", "InvalidTransitionIsRejected")]
        public async Task InvalidTransition_IsRejectedAndLogged()
        {
            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(await CreateAudiobook())
                .WithDownloadClientConfiguration(await CreateDownloadClientConfiguration())
                .WithStatus(DownloadStatus.Moved)
                .Build());

            // Act
            var downloadProcessingJobService = _provider.GetRequiredService<IDownloadProcessingJobService>();
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await downloadProcessingJobService.EnqueueAsync(download));

            var jobs = await _downloadProcessingJobRepository.GetRecentAsync(2);
            Assert.Empty(jobs);

            download = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotNull(download);
            Assert.Equal(DownloadStatus.Moved, download.Status);
        }

        [Fact]
        [Trait("Scenario", "DuplicateActiveJobReturnsExisting")]
        public async Task QueuePreventsDuplicateActiveJob_ReturnsExisting()
        {
            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(await CreateAudiobook())
                .WithDownloadClientConfiguration(await CreateDownloadClientConfiguration())
                .WithCompletedStatus(at: DateTime.UtcNow)
                .Build());

            // Act
            var downloadProcessingJobService = _provider.GetRequiredService<IDownloadProcessingJobService>();
            var job1 = await downloadProcessingJobService.EnqueueAsync(download);
            var job2 = await downloadProcessingJobService.EnqueueAsync(download);

            Assert.Equal(job1, job2);

            // Ensure only one job exists
            var jobs = await downloadProcessingJobService.GetJobsForDownloadAsync(download.Id);
            Assert.Single(jobs);
            Assert.Equal(ProcessingJobStatus.Pending, jobs.First().Status);
        }

        [Fact]
        public async Task ConcurrentInsertConflict_ReturnsPersistedWinner()
        {
            var download = new DownloadBuilder()
                .WithCompletedStatus(DateTime.UtcNow)
                .Build();
            var winner = new DownloadProcessingJobBuilder()
                .WithId("winning-job")
                .WithDownload(download)
                .WithPending(DateTime.UtcNow)
                .Build();
            var repository = new Mock<IDownloadProcessingJobRepository>();
            repository.SetupSequence(repo => repo.GetActiveByDownloadIdAsync(download.Id))
                .ReturnsAsync((DownloadProcessingJob?)null)
                .ReturnsAsync(winner);
            repository.Setup(repo => repo.GetRecentCompletedByDownloadIdAsync(
                    download.Id,
                    It.IsAny<DateTime>()))
                .ReturnsAsync((DownloadProcessingJob?)null);
            repository.Setup(repo => repo.AddAsync(It.IsAny<DownloadProcessingJob>()))
                .ThrowsAsync(new UniqueConstraintViolationException(
                    "duplicate active import job",
                    new InvalidOperationException()));
            var service = new DownloadProcessingJobService(
                repository.Object,
                NullLogger<DownloadProcessingJobService>.Instance,
                TimeProvider.System);

            var jobId = await service.EnqueueAsync(download);

            Assert.Equal(winner.Id, jobId);
        }

        [Fact]
        [Trait("Scenario", "RecentlyCompletedCooldownPreventsDuplicate")]
        public async Task QueueRespectsRecentlyCompletedCooldown_ReturnsCompletedJob()
        {
            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(await CreateAudiobook())
                .WithDownloadClientConfiguration(await CreateDownloadClientConfiguration())
                .WithCompletedStatus(at: DateTime.UtcNow)
                .Build());

            var downloadProcessingJobService = _provider.GetRequiredService<IDownloadProcessingJobService>();

            var jobId = await downloadProcessingJobService.EnqueueAsync(download);
            Assert.NotEmpty(jobId);
            var job = await downloadProcessingJobService.GetJobAsync(jobId);
            Assert.NotNull(job);

            // mark as completed now
            job.Status = ProcessingJobStatus.Completed;
            job.CompletedAt = DateTime.UtcNow;
            await downloadProcessingJobService.UpdateJobAsync(job);

            // attempt to queue again should return the recently completed job id
            var newJobId = await downloadProcessingJobService.EnqueueAsync(download);
            Assert.Equal(jobId, newJobId);

            // now pretend the completed job is old -> set CompletedAt far in past
            job.CompletedAt = DateTime.UtcNow.AddHours(-10);
            await downloadProcessingJobService.UpdateJobAsync(job);

            // now new queue should create a fresh job id
            var newId = await downloadProcessingJobService.EnqueueAsync(download);
            Assert.NotEqual(jobId, newId);
        }

        [Fact]
        [Trait("Scenario", "Job is completed if lower quality are skipped")]
        public async Task LowerQualitySkip_MarksJobCompleted()
        {
            var downloadClientGatewayMock = new DownloadClientGatewayMock();
            _services.AddSingleton<IDownloadClientGateway>(downloadClientGatewayMock);

            var metadataServiceMock = new MetadataServiceMock();
            _services.AddSingleton<IMetadataService>(metadataServiceMock);
            Init();
            await AddAuthorizedRootAsync(FileService.GetTempPath());

            var outputDirectory = FileService.GetTempDirectory("library");
            var existingFile = await FileService.GetFileAsync(outputDirectory, "oldfile1.mp3");

            var sourceDirectory = FileService.GetTempDirectory("download");
            var downloadedFile = await FileService.GetFileAsync(sourceDirectory, "newfile1.mp3");

            downloadClientGatewayMock.SourceFiles = [downloadedFile];

            // We give the new file arbitrary lower bitrate so the import should skip it and keep the existing 320kbps one
            metadataServiceMock.AddMetadata("newfile", new AudioMetadata { BitRate = 128000, SampleRate = 128000 });

            var qualityProfile = await _qualityProfileRepository.AddAsync(new QualityProfileBuilder().Build());

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(outputDirectory)
                .WithQualityProfile(qualityProfile)
                .Build());

            var audiobookFile = await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(existingFile)
                .WithBitrate(320000)
                .Build());

            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithPath(sourceDirectory)
                .Build());

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(audiobook)
                .WithDownloadClientConfiguration(client)
                .WithCompletedStatus(at: DateTime.UtcNow)
                .WithPath(sourceDirectory)
                .Build());

            var downloadProcessingJobService = _provider.GetRequiredService<IDownloadProcessingJobService>();

            // Queue Job
            var jobId = await downloadProcessingJobService.EnqueueAsync(download);
            Assert.NotEmpty(jobId);

            // Job should be pending
            var job = await downloadProcessingJobService.GetJobAsync(jobId);
            Assert.NotNull(job);
            Assert.Equal(ProcessingJobStatus.Pending, job.Status);

            // Process the job
            var downloadProcessingJobProcessor = _provider.GetRequiredService<DownloadProcessingJobProcessor>();
            await downloadProcessingJobProcessor.ProcessQueueAsync(CancellationToken.None);

            // Job should be completed
            job = await downloadProcessingJobService.GetJobAsync(jobId);
            Assert.NotNull(job);
            Assert.Equal(ProcessingJobStatus.Completed, job.Status);

            // There should be only one audiobook file unchanged
            var files = await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id);
            Assert.Single(files);
            var file = files.First();
            Assert.Equal(existingFile, file.Path);
            Assert.Equal(320000, file.Bitrate);

            // Download should be imported
            download = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.Equal(DownloadStatus.Moved, download.Status);
        }

        [Fact]
        [Trait("Scenario", "Job is not completed if no audio files are imported")]
        public async Task FilesNotFound_Retry()
        {
            var downloadClientGatewayMock = new DownloadClientGatewayMock();
            _services.AddSingleton<IDownloadClientGateway>(downloadClientGatewayMock);
            Init();
            await AddAuthorizedRootAsync(FileService.GetTempPath());

            var outputDirectory = FileService.GetTempDirectory("library");

            var sourceDirectory = FileService.GetTempDirectory("download");
            var file1 = Path.Join(sourceDirectory, "file1.mp3");
            var file2 = Path.Join(sourceDirectory, "file2.mp3");
            var companion1 = await FileService.GetFileAsync(sourceDirectory, "companion.nfo");

            downloadClientGatewayMock.SourceFiles = [file1, file2, companion1];

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(outputDirectory)
                .Build());

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(audiobook)
                .WithDownloadClientConfiguration(await CreateDownloadClientConfiguration())
                .WithCompletedStatus(at: DateTime.UtcNow)
                .WithPath(sourceDirectory)
                .Build());

            var downloadProcessingJobService = _provider.GetRequiredService<IDownloadProcessingJobService>();

            // Queue Job
            var jobId = await downloadProcessingJobService.EnqueueAsync(download);
            Assert.NotEmpty(jobId);

            // Job should be pending
            var job = await downloadProcessingJobService.GetJobAsync(jobId);
            Assert.NotNull(job);
            Assert.Equal(ProcessingJobStatus.Pending, job.Status);

            // Process the job
            var downloadProcessingJobProcessor = _provider.GetRequiredService<DownloadProcessingJobProcessor>();
            await downloadProcessingJobProcessor.ProcessQueueAsync(CancellationToken.None);

            // Job should be pending
            job = await downloadProcessingJobService.GetJobAsync(jobId);
            Assert.NotNull(job);
            Assert.Equal(ProcessingJobStatus.Pending, job.Status);
            Assert.Equal(1, job.RetryCount);

            // One of the files becomes available
            await FileService.GetFileAsync(sourceDirectory, "file1.mp3");

            // Retry to process the job+
            await TestUtils.CancelJobRetryWait(_downloadProcessingJobRepository, job);
            await downloadProcessingJobProcessor.ProcessQueueAsync(CancellationToken.None);

            // Job should be pending
            job = await downloadProcessingJobService.GetJobAsync(jobId);
            Assert.NotNull(job);
            Assert.Equal(ProcessingJobStatus.Pending, job.Status);
            Assert.Equal(2, job.RetryCount);

            // The last file becomes available
            await FileService.GetFileAsync(sourceDirectory, "file2.mp3");

            // Retry to process the job
            await TestUtils.CancelJobRetryWait(_downloadProcessingJobRepository, job);
            await downloadProcessingJobProcessor.ProcessQueueAsync(CancellationToken.None);

            // Job should be Completed
            job = await downloadProcessingJobService.GetJobAsync(jobId);
            Assert.NotNull(job);
            Assert.Equal(ProcessingJobStatus.Completed, job.Status);

            // Files should be imported
            var importedFiles = Directory.EnumerateFiles(outputDirectory, "*.*", SearchOption.AllDirectories)
                .ToList();
            Assert.Equal(3, importedFiles.Count);

            // Download should be moved
            download = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.Equal(DownloadStatus.Moved, download.Status);
        }
    }
}
