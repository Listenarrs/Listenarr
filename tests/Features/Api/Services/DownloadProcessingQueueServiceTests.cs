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
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Listenarr.Api.Services;
using Listenarr.Domain.Utils;
using Listenarr.Tests.Common;
using Listenarr.Domain.Models;

namespace Listenarr.Tests.Features.Api.Services
{
    [Trait("Area", "ProcessingQueueRecovery")]
    [Trait("Name", "DownloadProcessingQueueServiceTests")]
    [Trait("Category", "DownloadProcessing")]
    public class DownloadProcessingQueueServiceTests : BaseTests
    {
        [Fact]
        [Trait("Scenario", "DuplicateActiveJobReturnsExisting")]
        public async Task QueuePreventsDuplicateActiveJob_ReturnsExisting()
        {
            var svc = _provider.GetRequiredService<IDownloadProcessingQueueService>();

            // Enqueue first
            var job1 = await svc.QueueDownloadProcessingAsync("dl-1", FileUtils.GetAbsolutePath("tmp", "source.mp3"), null);

            // Add a pending job check creates same id
            var job2 = await svc.QueueDownloadProcessingAsync("dl-1", FileUtils.GetAbsolutePath("tmp", "source.mp3"), null);

            Assert.Equal(job1, job2);

            // Ensure only one job exists
            var jobs = await svc.GetJobsForDownloadAsync("dl-1");
            Assert.Single(jobs);
            Assert.Equal(ProcessingJobStatus.Pending, jobs[0].Status);
        }

        [Fact]
        [Trait("Scenario", "RecentlyCompletedCooldownPreventsDuplicate")]
        public async Task QueueRespectsRecentlyCompletedCooldown_ReturnsCompletedJob()
        {
            var svc = _provider.GetRequiredService<IDownloadProcessingQueueService>();

            var jobId = await svc.QueueDownloadProcessingAsync("dl-2", FileUtils.GetAbsolutePath("tmp", "s1.mp3"), null);
            var job = await svc.GetJobAsync(jobId);
            Assert.NotNull(job);

            // mark as completed now
            job.Status = ProcessingJobStatus.Completed;
            job.CompletedAt = DateTime.UtcNow;
            await svc.UpdateJobAsync(job);

            // attempt to queue again should return the recently completed job id
            var returned = await svc.QueueDownloadProcessingAsync("dl-2", FileUtils.GetAbsolutePath("tmp", "s1.mp3"), null);
            Assert.Equal(jobId, returned);

            // now pretend the completed job is old -> set CompletedAt far in past
            job.CompletedAt = DateTime.UtcNow.AddHours(-10);
            await svc.UpdateJobAsync(job);

            // now new queue should create a fresh job id
            var newId = await svc.QueueDownloadProcessingAsync("dl-2", FileUtils.GetAbsolutePath("tmp", "s1.mp3"), null);
            Assert.NotEqual(jobId, newId);
        }
    }
}

