using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.SignalR;

namespace Listenarr.Tests.Features.Infrastructure.Library.Scanning
{
    [Trait("Name", "ScanJobProcessorTests")]
    [Trait("Category", "BackgroundWorkers")]
    public class ScanJobProcessorTests : BaseTests
    {
        public override async Task InitializeAsync()
        {
            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata
                {
                    Duration = TimeSpan.FromSeconds(120),
                    Format = "m4b",
                    BitRate = 64000,
                    SampleRate = 32000,
                    Channels = 1
                });

            _services.AddSingleton(metadataMock.Object);
            Init();
            await Task.CompletedTask;
        }

        [Fact]
        public async Task ProcessJobAsync_HappyPath_ReconcilesFilesAndCompletesJob()
        {
            var basePath = FileService.GetTempDirectory("scan-processor-happy");
            var audioPath = await FileService.GetFileAsync(basePath, "Scan Book.m4b", "audio");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Scan Book")
                .WithBasePath(basePath)
                .Build());
            var (queue, job) = await CreateQueuedScanJobAsync(audiobook);

            var processor = _provider.GetRequiredService<IScanJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            Assert.True(queue.TryGetJob(job.Id, out var updatedJob));
            Assert.Equal("Completed", updatedJob!.Status);

            var files = await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id);
            var file = Assert.Single(files);
            Assert.Equal(audioPath, file.Path);

            var metricsMock = _provider.GetRequiredService<Mock<IAppMetricsService>>();
            metricsMock.Verify(m => m.Increment("worker.scan.job.started", It.IsAny<double>()), Times.Once);
            metricsMock.Verify(m => m.Increment("worker.scan.job.completed", It.IsAny<double>()), Times.Once);
        }

        [Fact]
        public async Task ProcessJobAsync_Failure_MarksJobFailed()
        {
            var failingProxy = new Mock<IClientProxy>();
            failingProxy
                .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("hub down"));

            var hubClients = new Mock<IHubClients>();
            hubClients.Setup(c => c.All).Returns(failingProxy.Object);
            var hubContext = new Mock<IHubContext<DownloadHub>>();
            hubContext.Setup(h => h.Clients).Returns(hubClients.Object);

            _services.AddSingleton(hubContext.Object);
            Init();

            var basePath = FileService.GetTempDirectory("scan-processor-failure");
            await FileService.GetFileAsync(basePath, "Broken Broadcast.m4b", "audio");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Broken Broadcast")
                .WithBasePath(basePath)
                .Build());
            var (queue, job) = await CreateQueuedScanJobAsync(audiobook);

            var processor = _provider.GetRequiredService<IScanJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            Assert.True(queue.TryGetJob(job.Id, out var updatedJob));
            Assert.Equal("Failed", updatedJob!.Status);

            var metricsMock = _provider.GetRequiredService<Mock<IAppMetricsService>>();
            metricsMock.Verify(m => m.Increment("worker.scan.job.failed", It.IsAny<double>()), Times.AtLeastOnce);
        }

        [Fact]
        public async Task ProcessJobAsync_CanceledToken_ThrowsBeforeStateChange()
        {
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(FileService.GetTempDirectory("scan-processor-cancel"))
                .Build());
            var (queue, job) = await CreateQueuedScanJobAsync(audiobook);
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var processor = _provider.GetRequiredService<IScanJobProcessor>();
            await Assert.ThrowsAsync<OperationCanceledException>(() => processor.ProcessJobAsync(job, cts.Token));

            Assert.True(queue.TryGetJob(job.Id, out var updatedJob));
            Assert.Equal("Queued", updatedJob!.Status);
        }

        [Fact]
        public async Task ProcessJobAsync_ReplayedCompletedJob_DoesNotDuplicateFileRows()
        {
            var basePath = FileService.GetTempDirectory("scan-processor-replay");
            await FileService.GetFileAsync(basePath, "Replay Book.m4b", "audio");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Replay Book")
                .WithBasePath(basePath)
                .Build());
            var (queue, job) = await CreateQueuedScanJobAsync(audiobook);

            var processor = _provider.GetRequiredService<IScanJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);
            await processor.ProcessJobAsync(job, CancellationToken.None);

            Assert.True(queue.TryGetJob(job.Id, out var updatedJob));
            Assert.Equal("Completed", updatedJob!.Status);
            var files = await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id);
            Assert.Single(files);
        }

        private async Task<(ScanQueueService Queue, ScanJob Job)> CreateQueuedScanJobAsync(Audiobook audiobook)
        {
            var queue = Assert.IsType<ScanQueueService>(_provider.GetRequiredService<IScanQueueService>());
            var jobId = await queue.EnqueueScanAsync(audiobook);
            Assert.True(queue.Reader.TryRead(out var job));
            Assert.Equal(jobId, job.Id);
            return (queue, job);
        }
    }
}
