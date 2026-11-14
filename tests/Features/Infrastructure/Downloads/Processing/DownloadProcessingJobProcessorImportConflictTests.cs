using Listenarr.Application.Common.Exceptions;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks;

namespace Listenarr.Tests.Features.Infrastructure.Downloads.Processing
{
    [Trait("Name", "DownloadProcessingJobProcessorImportConflictTests")]
    [Trait("Category", "DownloadProcessingJob")]
    public class DownloadProcessingJobProcessorImportConflictTests : BaseTests
    {
        private const string RepairRequiredMessage =
            "A previously committed file import (00000000-0000-0000-0000-000000000001) requires repair before this audiobook's files can be changed.";

        private readonly Mock<IDownloadImportService> importServiceMock = new();
        private readonly DownloadClientGatewayMock downloadClientGatewayMock = new();

        public override async Task InitializeAsync()
        {
            _services.AddSingleton<IDownloadClientGateway>(downloadClientGatewayMock);
            Init(builder => builder.WithSingleton<IDownloadImportService>(importServiceMock.Object));
            await AddAuthorizedRootAsync(FileService.GetTempPath());
        }

        [Fact]
        public async Task ImportConflict_FailsJobAndBlocksDownloadWithTheConflictReason()
        {
            var (download, job) = await QueueImportThatThrowsAsync(
                new ApplicationConflictException("registration_recovery_repair_required", RepairRequiredMessage));

            await _provider.GetRequiredService<DownloadProcessingJobProcessor>()
                .ProcessQueueAsync(CancellationToken.None);

            var savedJob = (await _downloadProcessingJobRepository.GetByIdAsync(job.Id))!;
            Assert.Equal(ProcessingJobStatus.Failed, savedJob.Status);
            Assert.Equal(RepairRequiredMessage, savedJob.ErrorMessage);
            Assert.Equal(0, savedJob.RetryCount);
            Assert.Null(savedJob.NextRetryAt);

            var savedDownload = (await _downloadRepository.GetByIdAsync(download.Id))!;
            Assert.Equal(DownloadStatus.ImportBlocked, savedDownload.Status);
            Assert.NotNull(savedDownload.ImportBlockReason);
            Assert.Contains(RepairRequiredMessage, savedDownload.ImportBlockMessages!);

            var history = await _historyRepository.GetByCorrelationIdAsync(savedJob.GetOrCreateCorrelationId());
            Assert.Contains(history, entry =>
                entry.EventType == HistoryEvents.ImportFailed && entry.Error == RepairRequiredMessage);
        }

        [Fact]
        public async Task ImportInvalidOperation_StillFailsJobAndBlocksDownloadAsBefore()
        {
            const string message = "Audiobook 1 no longer exists";
            var (download, job) = await QueueImportThatThrowsAsync(new InvalidOperationException(message));

            await _provider.GetRequiredService<DownloadProcessingJobProcessor>()
                .ProcessQueueAsync(CancellationToken.None);

            var savedJob = (await _downloadProcessingJobRepository.GetByIdAsync(job.Id))!;
            Assert.Equal(ProcessingJobStatus.Failed, savedJob.Status);
            Assert.Equal(message, savedJob.ErrorMessage);

            var savedDownload = (await _downloadRepository.GetByIdAsync(download.Id))!;
            Assert.Equal(DownloadStatus.ImportBlocked, savedDownload.Status);
            Assert.DoesNotContain(message, savedDownload.ImportBlockMessages!);
        }

        [Fact]
        public async Task ImportUnexpectedException_StillPropagatesOutOfTheQueuePass()
        {
            var (_, job) = await QueueImportThatThrowsAsync(new IOException("disk went away"));

            await Assert.ThrowsAsync<IOException>(() => _provider
                .GetRequiredService<DownloadProcessingJobProcessor>()
                .ProcessQueueAsync(CancellationToken.None));

            var savedJob = (await _downloadProcessingJobRepository.GetByIdAsync(job.Id))!;
            Assert.NotEqual(ProcessingJobStatus.Failed, savedJob.Status);
        }

        private async Task<(Download Download, DownloadProcessingJob Job)> QueueImportThatThrowsAsync(Exception exception)
        {
            var sourceDirectory = FileService.GetTempDirectory("import-conflict-source");
            var source = await FileService.GetFileAsync(sourceDirectory, "book.mp3", "audio");
            downloadClientGatewayMock.SourceFiles = [source];
            downloadClientGatewayMock.CanMoveFiles = true;
            importServiceMock
                .Setup(service => service.ImportDownloadFilesAsync(
                    It.IsAny<Audiobook>(),
                    It.IsAny<List<string>>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<DownloadImportOptions?>()))
                .ThrowsAsync(exception);

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(await CreateAudiobook())
                .WithDownloadClientConfiguration(await CreateDownloadClientConfiguration())
                .WithPath(sourceDirectory)
                .WithCompletedStatus(DateTime.UtcNow)
                .Build());
            var job = await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithDownload(download)
                .Build());
            return (download, job);
        }
    }
}
