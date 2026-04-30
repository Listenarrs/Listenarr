using System.Reflection;
using System.Runtime.InteropServices;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Listenarr.Domain.Utils;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    [Trait("Name", "DownloadProcessingBackgroundServiceTests")]
    [Trait("Category", "DownloadProcessingBackgroundService")]
    public class DownloadProcessingBackgroundServiceTests : BaseTests
    {
        private readonly string CLIENT_CONFIG_ID = "dl-client-1";
        private readonly string DOWNLOAD_COMPLETE_ID = "dl-complete-1";

        [Fact]
        [Trait("Method", "EnqueueCompletedDownloadsAsync")]
        [Trait("Scenario", "Space in directory where downloaded files are located")]
        [Trait("OSPlatform", "Linux")]
        [Trait("OSPlatform", "OSX")]
        public async Task EnqueueCompletedDownloadsAsync_SpaceInFinalPath()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return;
            }

            var remoteSource = FileService.GetTempDirectory("dl-remote-source ");
            var localSource = FileService.GetTempDirectory("dl-local-source /");
            var localDestination = FileService.GetTempDirectory("dl-destination");

            var remoteChapter1 = Path.Join(remoteSource, "01 - Seconde Fondation Isaac Asimov.mp3");
            var remoteChapter2 = Path.Join(remoteSource, "02 - Seconde Fondation Isaac Asimov.mp3");
            var remoteChapter3 = Path.Join(remoteSource, "03 - Seconde Fondation Isaac Asimov.mp3");
            var remoteChapter4 = Path.Join(remoteSource, "04 - Seconde Fondation Isaac Asimov.mp3");
            var remoteCompanion = Path.Join(remoteSource, "Seconde Fondation Isaac Asimov.nfo");

            var localChapter1 = await FileService.GetFileAsync(localSource, "01 - Seconde Fondation Isaac Asimov.mp3");
            var localChapter2 = await FileService.GetFileAsync(localSource, "02 - Seconde Fondation Isaac Asimov.mp3");
            var localChapter3 = await FileService.GetFileAsync(localSource, "03 - Seconde Fondation Isaac Asimov.mp3");
            var localChapter4 = await FileService.GetFileAsync(localSource, "04 - Seconde Fondation Isaac Asimov.mp3");
            var localCompanion = await FileService.GetFileAsync(localSource, "Seconde Fondation Isaac Asimov.nfo");

            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithId(CLIENT_CONFIG_ID)
                .WithName("Slskd")
                .WithType("slskd")
                .WithHost("localhost")
                .WithPort(5030)
                .Enabled()
                .Build());

            var download = new DownloadBuilder()
                .WithId(DOWNLOAD_COMPLETE_ID)
                .WithDownloadClientConfiguration(client)
                .WithPath(remoteSource)
                .WithProtocol(DownloadProtocol.Torrent)
                .WithUploader("USER1")
                .WithCompletedStatus(DateTime.UtcNow)
                .Build();
            await _downloadRepository.AddAsync(download);

            await _remotePathMappingRepository.SaveAsync(new RemotePathMappingBuilder()
                .WithDownloadClientConfiguration(client)
                .WithLocalPath(localSource)
                .WithRemotePath(remoteSource)
                .WithName("TEST_REMOTE_MAPPING")
                .Build());

            var importItemResolutionServiceMock = _provider.GetRequiredService<Mock<IImportItemResolutionService>>();
            importItemResolutionServiceMock
                .Setup(r => r.ResolveImportItemAsync(
                    It.Is<Download>(d => d.Id == download.Id),
                    It.IsAny<QueueItem>(),
                    It.IsAny<QueueItem?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((Download download, QueueItem queueItem, QueueItem? previousAttempt, CancellationToken ct) =>
                {
                    queueItem.SourceFiles = new List<string> {
                        remoteChapter1,
                        remoteChapter2,
                        remoteChapter3,
                        remoteChapter4,
                        remoteCompanion
                    };
                    return queueItem;
                });

            var method = typeof(DownloadProcessingBackgroundService).GetMethod("EnqueueCompletedDownloadsAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var downloadProcessingBackgroundService = _provider.GetRequiredService<DownloadProcessingBackgroundService>();
            var task = (Task?)method!.Invoke(downloadProcessingBackgroundService, [CancellationToken.None]);
            Assert.NotNull(task);
            await task!;

            Assert.Single(await _downloadProcessingJobRepository.GetRecentAsync(2));
            var job = (await _downloadProcessingJobRepository.GetRecentAsync(2)).Single();
            Assert.NotNull(job);
            Assert.Equal(localSource, job.SourcePath);
        }
        [Fact]
        [Trait("Scenario", "StartupResetRequeuesStuckProcessingJobs")]
        public async Task ResetStuckJobsAsync_ProcessingJobs_AreResetToPending()
        {
            await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJob
            {
                Id = "job-processing-1",
                DownloadId = "dl-1",
                JobType = ProcessingJobType.MoveOrCopyFile,
                Status = ProcessingJobStatus.Processing,
                SourcePath = FileUtils.GetAbsolutePath("tmp", "a.mp3")
            });
            await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJob
            {
                Id = "job-pending-1",
                DownloadId = "dl-2",
                JobType = ProcessingJobType.MoveOrCopyFile,
                Status = ProcessingJobStatus.Pending,
                SourcePath = FileUtils.GetAbsolutePath("tmp", "b.mp3")
            });

            var service = _provider.GetRequiredService<DownloadProcessingBackgroundService>();

            var method = typeof(DownloadProcessingBackgroundService)
                .GetMethod("ResetStuckJobsAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var task = (Task?)method!.Invoke(service, [CancellationToken.None]);
            Assert.NotNull(task);
            await task!;

            var processingJob = await _downloadProcessingJobRepository.GetByIdAsync("job-processing-1");
            var pendingJob = await _downloadProcessingJobRepository.GetByIdAsync("job-pending-1");

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
            var tempFile = await FileService.GetTempFileAsync("tmp.mp3");

            var queueServiceMock = new Mock<IDownloadProcessingQueueService>();
            queueServiceMock.Setup(q => q.QueueDownloadProcessingAsync("ddl-1", tempFile, "DDL"))
                .ReturnsAsync("job-1");
            _services.AddSingleton(queueServiceMock.Object);
            Init();

            var client = new DownloadClientConfigurationBuilder()
                .WithId("DDL")
                .Build();

            var download = new DownloadBuilder()
                .WithId("ddl-1")
                .WithCompletedStatus(DateTime.UtcNow)
                .WithDownloadClientConfiguration(client)
                .WithPath(tempFile)
                .Build();

            await _downloadClientConfigurationRepository.SaveAsync(client);
            await _downloadRepository.AddAsync(download);

            var importItemResolutionMock = _provider.GetRequiredService<Mock<IImportItemResolutionService>>();
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

            var method = typeof(DownloadProcessingBackgroundService)
                .GetMethod("EnqueueCompletedDownloadsAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var service = _provider.GetRequiredService<DownloadProcessingBackgroundService>();
            var task = (Task?)method!.Invoke(service, [CancellationToken.None]);
            Assert.NotNull(task);
            await task!;

            queueServiceMock.Verify(q => q.QueueDownloadProcessingAsync("ddl-1", tempFile, "DDL"), Times.Once);
        }
    }
}
