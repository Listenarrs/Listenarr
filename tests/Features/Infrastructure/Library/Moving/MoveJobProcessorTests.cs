using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving
{
    [Trait("Name", "MoveJobProcessorTests")]
    [Trait("Category", "BackgroundWorkers")]
    public class MoveJobProcessorTests : BaseTests
    {
        [Fact]
        public async Task ProcessJobAsync_HappyPath_MovesFilesAndCompletesJob()
        {
            var src = FileService.GetTempDirectory("move-processor-src");
            await FileService.GetFileAsync(src, "book.m4b", "audio");
            var dst = Path.Join(FileService.GetTempPath(), "move-processor-dst");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook { Title = "Move Processor", BasePath = src });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, dst, src);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = await queue.GetJobAsync(job.Id);
            Assert.NotNull(updatedJob);
            Assert.Equal("Completed", updatedJob!.Status);
            Assert.False(Directory.Exists(src));
            Assert.True(File.Exists(Path.Join(dst, "book.m4b")));

            var history = await _historyRepository.GetByAudiobookIdAsync(audiobook.Id);
            var moveEvent = Assert.Single(history, entry => entry.EventType == "Moved");
            Assert.True(moveEvent.NotificationSent);

            var metricsMock = _provider.GetRequiredService<Mock<IAppMetricsService>>();
            metricsMock.Verify(m => m.Increment("worker.move.job.started", It.IsAny<double>()), Times.Once);
            metricsMock.Verify(m => m.Increment("worker.move.job.completed", It.IsAny<double>()), Times.Once);
        }

        [Fact]
        public async Task ProcessJobAsync_TargetContainsFiles_MarksJobFailed()
        {
            var src = FileService.GetTempDirectory("move-processor-fail-src");
            await FileService.GetFileAsync(src, "book.m4b", "audio");
            var dst = FileService.GetTempDirectory("move-processor-fail-dst");
            await FileService.GetFileAsync(dst, "existing.txt", "blocked");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook { Title = "Move Processor Fail", BasePath = src });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, dst, src);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = await queue.GetJobAsync(job.Id);
            Assert.NotNull(updatedJob);
            Assert.Equal("Failed", updatedJob!.Status);
            Assert.True(Directory.Exists(src));

            var metricsMock = _provider.GetRequiredService<Mock<IAppMetricsService>>();
            metricsMock.Verify(m => m.Increment("worker.move.job.failed", It.IsAny<double>()), Times.Once);
        }

        [Fact]
        public async Task ProcessJobAsync_CanceledToken_ThrowsBeforeStateChange()
        {
            var src = FileService.GetTempDirectory("move-processor-cancel");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook { Title = "Move Processor Cancel", BasePath = src });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, src, src);
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await Assert.ThrowsAsync<OperationCanceledException>(() => processor.ProcessJobAsync(job, cts.Token));

            var updatedJob = await queue.GetJobAsync(job.Id);
            Assert.NotNull(updatedJob);
            Assert.Equal("Queued", updatedJob!.Status);
        }

        [Fact]
        public async Task ProcessJobAsync_ReplayedNoOpJob_RemainsCompleted()
        {
            var src = FileService.GetTempDirectory("move-processor-replay");
            await FileService.GetFileAsync(src, "book.m4b", "audio");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook { Title = "Move Processor Replay", BasePath = src });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, src, src);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = await queue.GetJobAsync(job.Id);
            Assert.NotNull(updatedJob);
            Assert.Equal("Completed", updatedJob!.Status);
            Assert.True(File.Exists(Path.Join(src, "book.m4b")));
        }

        [Fact]
        public async Task ProcessJobAsync_StubTarget_ReplacedWhenFlagSet()
        {
            // Target holds only leftover metadata (no audio) and the organize
            // flow queued the job with ReplaceStubTarget — the stub is deleted
            // and replaced instead of failing the move.
            var src = FileService.GetTempDirectory("move-stub-src");
            await FileService.GetFileAsync(src, "book.m4b", "audio");
            var dst = FileService.GetTempDirectory("move-stub-dst");
            await FileService.GetFileAsync(dst, "Title - Author.opf", "stale-metadata");
            await FileService.GetFileAsync(dst, "cover.jpg", "stale-cover");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook { Title = "Stub Replace", BasePath = src });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, dst, src, replaceStubTarget: true);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = await queue.GetJobAsync(job.Id);
            Assert.Equal("Completed", updatedJob!.Status);
            Assert.True(File.Exists(Path.Join(dst, "book.m4b")));
            Assert.False(File.Exists(Path.Join(dst, "Title - Author.opf")));
            Assert.False(File.Exists(Path.Join(dst, "cover.jpg")));
            Assert.False(Directory.Exists(src));
        }

        [Fact]
        public async Task ProcessJobAsync_StubTarget_RefusedWithoutFlag()
        {
            var src = FileService.GetTempDirectory("move-stub-noflag-src");
            await FileService.GetFileAsync(src, "book.m4b", "audio");
            var dst = FileService.GetTempDirectory("move-stub-noflag-dst");
            await FileService.GetFileAsync(dst, "Title - Author.opf", "stale-metadata");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook { Title = "Stub No Flag", BasePath = src });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, dst, src);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = await queue.GetJobAsync(job.Id);
            Assert.Equal("Failed", updatedJob!.Status);
            Assert.True(File.Exists(Path.Join(src, "book.m4b")));
            Assert.True(File.Exists(Path.Join(dst, "Title - Author.opf")));
        }

        [Fact]
        public async Task ProcessJobAsync_AudioInTarget_RefusedDespiteFlag()
        {
            // Re-verification at execute time: if the target holds audio (even
            // nested), the replace flag must not destroy it.
            var src = FileService.GetTempDirectory("move-stub-audio-src");
            await FileService.GetFileAsync(src, "book.m4b", "fresh-audio");
            var dst = FileService.GetTempDirectory("move-stub-audio-dst");
            await FileService.GetFileAsync(dst, "cover.jpg", "cover");
            var nested = Path.Join(dst, "Sub");
            Directory.CreateDirectory(nested);
            await FileService.GetFileAsync(nested, "real-book.mp3", "protected-audio");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook { Title = "Stub Audio Guard", BasePath = src });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, dst, src, replaceStubTarget: true);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = await queue.GetJobAsync(job.Id);
            Assert.Equal("Failed", updatedJob!.Status);
            Assert.True(File.Exists(Path.Join(src, "book.m4b")));
            Assert.True(File.Exists(Path.Join(nested, "real-book.mp3")));
        }

        private async Task<(IMoveQueueService Queue, MoveJob Job)> CreateQueuedMoveJobAsync(
            Audiobook audiobook,
            string requestedPath,
            string sourcePath,
            bool replaceStubTarget = false)
        {
            var queue = _provider.GetRequiredService<IMoveQueueService>();
            var jobId = await queue.EnqueueMoveAsync(audiobook.Id, requestedPath, sourcePath, replaceStubTarget);
            var job = await queue.GetJobAsync(jobId);
            Assert.NotNull(job);
            return (queue, job!);
        }
    }
}
