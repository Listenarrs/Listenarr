using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;

namespace Listenarr.Api.Tests
{
    [Trait("Area", "ProcessingQueueRecovery")]
    public class DownloadProcessingQueueServiceTests
    {
        [Fact]
        [Trait("Scenario", "DuplicateActiveJobReturnsExisting")]
        public async Task QueuePreventsDuplicateActiveJob_ReturnsExisting()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(options);

            var services = new ServiceCollection();
            services.AddSingleton(db);
            services.AddScoped<IDownloadProcessingQueueService, DownloadProcessingQueueService>();
            services.AddLogging();

            var provider = services.BuildServiceProvider();
            var svc = provider.GetRequiredService<IDownloadProcessingQueueService>();

            // Enqueue first
            var job1 = await svc.QueueDownloadProcessingAsync("dl-1", "C:/tmp/source.mp3", null);

            // Add a pending job check creates same id
            var job2 = await svc.QueueDownloadProcessingAsync("dl-1", "C:/tmp/source.mp3", null);

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
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(options);

            var services = new ServiceCollection();
            services.AddSingleton(db);
            services.AddScoped<IDownloadProcessingQueueService, DownloadProcessingQueueService>();
            services.AddLogging();

            var provider = services.BuildServiceProvider();
            var svc = provider.GetRequiredService<IDownloadProcessingQueueService>();

            var jobId = await svc.QueueDownloadProcessingAsync("dl-2", "C:/tmp/s1.mp3", null);
            var job = await svc.GetJobAsync(jobId);
            Assert.NotNull(job);

            // mark as completed now
            job.Status = ProcessingJobStatus.Completed;
            job.CompletedAt = DateTime.UtcNow;
            await svc.UpdateJobAsync(job);

            // attempt to queue again should return the recently completed job id
            var returned = await svc.QueueDownloadProcessingAsync("dl-2", "C:/tmp/s1.mp3", null);
            Assert.Equal(jobId, returned);

            // now pretend the completed job is old -> set CompletedAt far in past
            job.CompletedAt = DateTime.UtcNow.AddHours(-10);
            await svc.UpdateJobAsync(job);

            // now new queue should create a fresh job id
            var newId = await svc.QueueDownloadProcessingAsync("dl-2", "C:/tmp/s1.mp3", null);
            Assert.NotEqual(jobId, newId);
        }
    }
}

