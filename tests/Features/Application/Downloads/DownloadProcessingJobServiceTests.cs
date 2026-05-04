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
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Listenarr.Tests.Features.Application.Downloads
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
    }
}
