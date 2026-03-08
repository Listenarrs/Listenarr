using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Listenarr.Api.Tests
{
    [Trait("Area", "ProcessingBackgroundRecovery")]
    public class DownloadProcessingBackgroundServiceRecoveryTests
    {
        [Fact]
        [Trait("Scenario", "StartupResetRequeuesStuckProcessingJobs")]
        public async Task ResetStuckJobsAsync_ProcessingJobs_AreResetToPending()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);
            db.DownloadProcessingJobs.AddRange(
                new DownloadProcessingJob
                {
                    Id = "job-processing-1",
                    DownloadId = "dl-1",
                    JobType = ProcessingJobType.MoveOrCopyFile,
                    Status = ProcessingJobStatus.Processing,
                    SourcePath = "C:/tmp/a.mp3"
                },
                new DownloadProcessingJob
                {
                    Id = "job-pending-1",
                    DownloadId = "dl-2",
                    JobType = ProcessingJobType.MoveOrCopyFile,
                    Status = ProcessingJobStatus.Pending,
                    SourcePath = "C:/tmp/b.mp3"
                });
            await db.SaveChangesAsync();

            var services = new ServiceCollection();
            services.AddSingleton(db);
            services.AddScoped<IDownloadProcessingQueueService, DownloadProcessingQueueService>();
            services.AddLogging();

            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger<DownloadProcessingBackgroundService>>();
            var metricsMock = new Mock<IAppMetricsService>();

            var service = new DownloadProcessingBackgroundService(scopeFactory, loggerMock.Object, metricsMock.Object);

            var method = typeof(DownloadProcessingBackgroundService)
                .GetMethod("ResetStuckJobsAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var task = (Task?)method!.Invoke(service, new object[] { CancellationToken.None });
            Assert.NotNull(task);
            await task!;

            var processingJob = await db.DownloadProcessingJobs.FindAsync("job-processing-1");
            var pendingJob = await db.DownloadProcessingJobs.FindAsync("job-pending-1");

            Assert.NotNull(processingJob);
            Assert.Equal(ProcessingJobStatus.Pending, processingJob!.Status);
            Assert.Contains(processingJob.ProcessingLog, m => m.Contains("stuck Processing state", StringComparison.OrdinalIgnoreCase));

            Assert.NotNull(pendingJob);
            Assert.Equal(ProcessingJobStatus.Pending, pendingJob!.Status);
        }

        [Fact]
        [Trait("Scenario", "DirectDownloadsRemainEligibleForProcessing")]
        public async Task EnqueueCompletedDownloadsAsync_DirectDownload_IsQueuedForProcessing()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);
            var tempFile = Path.GetTempFileName();

            try
            {
                db.Downloads.Add(new Download
                {
                    Id = "ddl-1",
                    Title = "Direct Download",
                    DownloadClientId = "DDL",
                    DownloadPath = tempFile,
                    Status = DownloadStatus.Completed,
                    CompletedAt = DateTime.UtcNow
                });
                await db.SaveChangesAsync();

                var queueServiceMock = new Mock<IDownloadProcessingQueueService>();
                queueServiceMock.Setup(q => q.QueueDownloadProcessingAsync("ddl-1", tempFile, "DDL"))
                    .ReturnsAsync("job-1");

                var importItemResolutionMock = new Mock<IImportItemResolutionService>();
                importItemResolutionMock.Setup(r => r.ResolveImportItemAsync(
                        It.Is<Download>(d => d.Id == "ddl-1"),
                        It.IsAny<QueueItem>(),
                        It.IsAny<QueueItem?>(),
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync((Download download, QueueItem queueItem, QueueItem? previousAttempt, CancellationToken cancellationToken) =>
                    {
                        queueItem.ContentPath = tempFile;
                        return queueItem;
                    });

                var configMock = new Mock<IConfigurationService>();
                configMock.Setup(c => c.GetDownloadClientConfigurationsAsync())
                    .ReturnsAsync(new List<DownloadClientConfiguration>());

                var services = new ServiceCollection();
                services.AddSingleton(db);
                services.AddSingleton(queueServiceMock.Object);
                services.AddSingleton(importItemResolutionMock.Object);
                services.AddSingleton(configMock.Object);
                services.AddLogging();

                var provider = services.BuildServiceProvider();
                var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

                var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger<DownloadProcessingBackgroundService>>();
                var metricsMock = new Mock<IAppMetricsService>();
                var service = new DownloadProcessingBackgroundService(scopeFactory, loggerMock.Object, metricsMock.Object);

                var method = typeof(DownloadProcessingBackgroundService)
                    .GetMethod("EnqueueCompletedDownloadsAsync", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(method);

                var task = (Task?)method!.Invoke(service, new object[] { CancellationToken.None });
                Assert.NotNull(task);
                await task!;

                queueServiceMock.Verify(q => q.QueueDownloadProcessingAsync("ddl-1", tempFile, "DDL"), Times.Once);
            }
            finally
            {
                File.Delete(tempFile);
            }
        }
    }
}
