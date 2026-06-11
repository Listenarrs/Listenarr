using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Notifications
{
    public class NotificationsTests : BaseTests
    {
        private Mock<INotificationService> notificationServiceMock = new();
        private int failedCount = 0;
        private int importedCount = 0;

        public override async Task InitializeAsync()
        {
            notificationServiceMock.Setup(m => m.OnDownloadFailedAsync(It.IsAny<Download>()))
                .Returns(async (Download _) =>
                {
                    failedCount++;
                    return;
                });
            notificationServiceMock.Setup(m => m.OnDownloadImportedAsync(It.IsAny<Download>()))
                .Returns(async (Download _) =>
                {
                    importedCount++;
                    return;
                });

            _services.AddSingleton(notificationServiceMock.Object);
            Init();
        }

        [Fact]
        public async Task Notification_OnBLockedImport()
        {
            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithCompletedStatus(at: DateTime.UtcNow)
                .WithDownloadClientConfiguration(await CreateDownloadClientConfiguration())
                .WithAudiobook(await CreateAudiobook())
                .Build());

            var downloadProcessingJobService = _provider.GetRequiredService<IDownloadProcessingJobService>();
            var jobId = await downloadProcessingJobService.EnqueueAsync(download);
            Assert.NotNull(jobId);
            var job = await _downloadProcessingJobRepository.GetByIdAsync(jobId);
            Assert.NotNull(job);
            Assert.Equal(ProcessingJobStatus.Pending, job.Status);

            job.RetryCount = job.MaxRetries;
            await _downloadProcessingJobRepository.UpdateAsync(job);

            // Check notifications
            Assert.Equal(0, failedCount);
            Assert.Equal(0, importedCount);

            var downloadProcessingJobProcessor = _provider.GetRequiredService<DownloadProcessingJobProcessor>();
            await downloadProcessingJobProcessor.ProcessQueueAsync(CancellationToken.None);

            download = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotNull(download);
            Assert.Equal(DownloadStatus.ImportBlocked, download.Status);

            // Check notifications
            Assert.Equal(1, failedCount);
            Assert.Equal(0, importedCount);
        }
    }
}
