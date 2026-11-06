using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Audiobooks.Jobs
{
    [Trait("Area", "Jobs")]
    [Trait("Name", "UnmatchedScanQueueCancellationTests")]
    [Trait("Category", "Application")]
    public sealed class UnmatchedScanQueueCancellationTests : BaseTests
    {
        private const string RootFolderPath = "/library/import";

        [Fact]
        public async Task TryCancel_QueuedJob_IsCanceledAndSignalsItsToken()
        {
            var queue = CreateQueue();
            var jobId = await queue.EnqueueAsync(RootFolderPath);

            var found = queue.TryCancel(jobId, out var job);

            Assert.True(found);
            Assert.NotNull(job);
            Assert.Equal("Canceled", job.Status);
            Assert.NotNull(job.CompletedAt);
            Assert.True(job.CancellationToken.IsCancellationRequested);
        }

        [Fact]
        public async Task TryCancel_RunningJob_LateResultsDoNotReplaceTheCancellation()
        {
            var queue = CreateQueue();
            var jobId = await queue.EnqueueAsync(RootFolderPath);
            queue.UpdateJob(jobId, "Processing");

            queue.TryCancel(jobId, out _);
            queue.UpdateJob(jobId, "Completed", new List<UnmatchedFileResult> { new() { FullPath = "/library/import/book.m4b" } });

            Assert.True(queue.TryGetJob(jobId, out var job));
            Assert.Equal("Canceled", job!.Status);
            Assert.Null(job.Results);
            Assert.False(queue.TryGetLastJobForPath(RootFolderPath, out _));
        }

        [Fact]
        public async Task TryCancel_FinishedJob_KeepsItsStatus()
        {
            var queue = CreateQueue();
            var jobId = await queue.EnqueueAsync(RootFolderPath);
            queue.UpdateJob(jobId, "Completed", new List<UnmatchedFileResult>());

            var found = queue.TryCancel(jobId, out var job);

            Assert.True(found);
            Assert.Equal("Completed", job!.Status);
            Assert.False(job.CancellationToken.IsCancellationRequested);
            Assert.True(queue.TryGetLastJobForPath(RootFolderPath, out _));
        }

        [Fact]
        public void TryCancel_UnknownJob_ReturnsFalse()
        {
            var queue = CreateQueue();

            Assert.False(queue.TryCancel(Guid.NewGuid(), out var job));
            Assert.Null(job);
        }

        [Fact]
        public async Task EnqueueAsync_AfterCancellation_StartsANewJobForTheSameFolder()
        {
            var queue = CreateQueue();
            var canceledJobId = await queue.EnqueueAsync(RootFolderPath);
            queue.TryCancel(canceledJobId, out _);

            var nextJobId = await queue.EnqueueAsync(RootFolderPath);

            Assert.NotEqual(canceledJobId, nextJobId);
        }

        private static UnmatchedScanQueueService CreateQueue() =>
            new(
                NullLogger<UnmatchedScanQueueService>.Instance,
                new Mock<IFileSystemSemanticsResolver>().Object);
    }
}
