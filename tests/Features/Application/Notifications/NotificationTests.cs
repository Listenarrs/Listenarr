using Listenarr.Domain.Notifications;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Notifications
{
    public class NotificationsTests : BaseTests
    {
        private readonly Mock<INotificationService> notificationServiceMock = new();

        public override async Task InitializeAsync()
        {
            notificationServiceMock
                .Setup(m => m.SendNotificationAsync(
                    It.IsAny<string>(), It.IsAny<object>(), It.IsAny<string>(), It.IsAny<List<string>>()))
                .Returns(Task.CompletedTask);

            _services.AddSingleton(notificationServiceMock.Object);
            Init();
            await Task.CompletedTask;
        }

        [Fact]
        public async Task TerminalImportFailure_FiresBookImportFailedTrigger()
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

            // Nothing fires until the job is actually processed to its terminal state.
            notificationServiceMock.Verify(
                m => m.SendNotificationAsync(
                    NotificationTriggers.BookImportFailed,
                    It.IsAny<object>(),
                    It.IsAny<string>(),
                    It.IsAny<List<string>>()),
                Times.Never);

            var downloadProcessingJobProcessor = _provider.GetRequiredService<DownloadProcessingJobProcessor>();
            await downloadProcessingJobProcessor.ProcessQueueAsync(CancellationToken.None);

            download = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotNull(download);
            Assert.Equal(DownloadStatus.ImportBlocked, download.Status);

            // Retry-exhausted terminal failure fires the real book-import-failed lifecycle trigger
            // (the configurable catalog name) through IBookLifecycleNotifier, not the old hardcoded
            // "Failed" shim that never matched a configured webhook. Regression coverage for #909 —
            // exercising the real completion path end-to-end, which the previous test mocked away.
            notificationServiceMock.Verify(
                m => m.SendNotificationAsync(
                    NotificationTriggers.BookImportFailed,
                    It.IsAny<object>(),
                    It.IsAny<string>(),
                    It.IsAny<List<string>>()),
                Times.Once);
        }
    }
}
