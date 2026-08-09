using System.Threading.Channels;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

[Trait("Area", "Library")]
[Trait("Name", "MoveBackgroundServiceTests")]
[Trait("Category", "Infrastructure")]
public sealed class MoveBackgroundServiceTests : BaseTests
{
    [Fact]
    public async Task FilesystemNotReady_DoesNotRecoverClaimOrProcessJobsUntilGateOpens()
    {
        var jobs = Channel.CreateUnbounded<MoveJob>();
        var job = new MoveJob { Id = Guid.NewGuid(), AudiobookId = 42 };
        await jobs.Writer.WriteAsync(job);
        var readiness = new TestLibraryFilesystemReadiness();
        readiness.SetRunning("AudiobookFileIdentities");
        var recovered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new Mock<IMoveQueueService>(MockBehavior.Strict);
        queue.SetupGet(service => service.Reader).Returns(jobs.Reader);
        queue.Setup(service => service.RecoverActiveJobsAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                recovered.TrySetResult();
                return Task.CompletedTask;
            });
        queue.Setup(service => service.TryClaimJobAsync(
                job.Id,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        var processor = new Mock<IMoveJobProcessor>(MockBehavior.Strict);
        processor.Setup(service => service.ProcessJobAsync(job, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                processed.TrySetResult();
                return Task.CompletedTask;
            });
        var worker = new MoveBackgroundService(
            queue.Object,
            processor.Object,
            readiness,
            NullLogger<MoveBackgroundService>.Instance,
            heartbeatInterval: TimeSpan.FromHours(1));

        await worker.StartAsync(CancellationToken.None);

        Assert.False(recovered.Task.IsCompleted);
        queue.Verify(service => service.TryClaimJobAsync(
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        processor.Verify(service => service.ProcessJobAsync(
            It.IsAny<MoveJob>(),
            It.IsAny<CancellationToken>()), Times.Never);

        readiness.SetReady();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await processed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        queue.Verify(service => service.TryClaimJobAsync(
            job.Id,
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
        processor.Verify(service => service.ProcessJobAsync(
            job,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LeaseLoss_DoesNotRewriteJobAsFailed()
    {
        var jobs = Channel.CreateUnbounded<MoveJob>();
        var job = new MoveJob { Id = Guid.NewGuid(), AudiobookId = 42 };
        await jobs.Writer.WriteAsync(job);
        var queue = new Mock<IMoveQueueService>();
        queue.SetupGet(service => service.Reader).Returns(jobs.Reader);
        queue.Setup(service => service.RecoverActiveJobsAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        queue.Setup(service => service.TryClaimJobAsync(
                job.Id,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        var processorInvoked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = new Mock<IMoveJobProcessor>();
        processor.Setup(service => service.ProcessJobAsync(job, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                processorInvoked.TrySetResult();
                return Task.FromException(new MoveLeaseLostException(job.Id, 1));
            });
        var worker = new MoveBackgroundService(
            queue.Object,
            processor.Object,
            TestLibraryFilesystemReadiness.Ready(),
            NullLogger<MoveBackgroundService>.Instance,
            heartbeatInterval: TimeSpan.FromHours(1));

        await worker.StartAsync(CancellationToken.None);
        await processorInvoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        queue.Verify(service => service.UpdateJobStatusAsync(
            job.Id,
            It.IsAny<string>(),
            It.IsAny<int>(),
            MoveJobStatus.Failed,
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HeartbeatException_CancelsInFlightProcessing()
    {
        var jobs = Channel.CreateUnbounded<MoveJob>();
        var job = new MoveJob { Id = Guid.NewGuid(), AudiobookId = 42 };
        await jobs.Writer.WriteAsync(job);
        var processingCanceled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new Mock<IMoveQueueService>();
        queue.SetupGet(service => service.Reader).Returns(jobs.Reader);
        queue.Setup(service => service.RecoverActiveJobsAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        queue.Setup(service => service.TryClaimJobAsync(
                job.Id,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        queue.Setup(service => service.HeartbeatJobAsync(
                job.Id,
                It.IsAny<string>(),
                1,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PersistenceException(
                "heartbeat failed",
                new InvalidOperationException("database unavailable")));
        var processor = new Mock<IMoveJobProcessor>();
        processor.Setup(service => service.ProcessJobAsync(job, It.IsAny<CancellationToken>()))
            .Returns(async (MoveJob _, CancellationToken cancellationToken) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    processingCanceled.TrySetResult();
                    throw;
                }
            });
        var worker = new MoveBackgroundService(
            queue.Object,
            processor.Object,
            TestLibraryFilesystemReadiness.Ready(),
            NullLogger<MoveBackgroundService>.Instance,
            heartbeatInterval: TimeSpan.FromMilliseconds(10));

        await worker.StartAsync(CancellationToken.None);
        await processingCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        queue.Verify(service => service.UpdateJobStatusAsync(
            job.Id,
            It.IsAny<string>(),
            It.IsAny<int>(),
            MoveJobStatus.Failed,
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OwnershipDeadline_CancelsInFlightProcessing()
    {
        var jobs = Channel.CreateUnbounded<MoveJob>();
        var job = new MoveJob { Id = Guid.NewGuid(), AudiobookId = 42 };
        await jobs.Writer.WriteAsync(job);
        var processingCanceled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new Mock<IMoveQueueService>();
        queue.SetupGet(service => service.Reader).Returns(jobs.Reader);
        queue.Setup(service => service.RecoverActiveJobsAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        queue.Setup(service => service.TryClaimJobAsync(
                job.Id,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        queue.Setup(service => service.HeartbeatJobAsync(
                job.Id,
                It.IsAny<string>(),
                1,
                It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return MoveHeartbeatOutcome.Renewed;
            });
        var processor = new Mock<IMoveJobProcessor>();
        processor.Setup(service => service.ProcessJobAsync(job, It.IsAny<CancellationToken>()))
            .Returns(async (MoveJob _, CancellationToken cancellationToken) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    processingCanceled.TrySetResult();
                    throw;
                }
            });
        var worker = new MoveBackgroundService(
            queue.Object,
            processor.Object,
            TestLibraryFilesystemReadiness.Ready(),
            NullLogger<MoveBackgroundService>.Instance,
            heartbeatInterval: TimeSpan.FromMilliseconds(10),
            ownershipDuration: TimeSpan.FromMilliseconds(50));

        await worker.StartAsync(CancellationToken.None);
        await processingCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        queue.Verify(service => service.UpdateJobStatusAsync(
            job.Id,
            It.IsAny<string>(),
            It.IsAny<int>(),
            MoveJobStatus.Failed,
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HeartbeatLeaseLoss_CancelsInFlightProcessing()
    {
        var jobs = Channel.CreateUnbounded<MoveJob>();
        var job = new MoveJob { Id = Guid.NewGuid(), AudiobookId = 42 };
        await jobs.Writer.WriteAsync(job);
        var processingCanceled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new Mock<IMoveQueueService>();
        queue.SetupGet(service => service.Reader).Returns(jobs.Reader);
        queue.Setup(service => service.RecoverActiveJobsAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        queue.Setup(service => service.TryClaimJobAsync(
                job.Id,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        queue.Setup(service => service.HeartbeatJobAsync(
                job.Id,
                It.IsAny<string>(),
                1,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(MoveHeartbeatOutcome.Lost);
        var processor = new Mock<IMoveJobProcessor>();
        processor.Setup(service => service.ProcessJobAsync(job, It.IsAny<CancellationToken>()))
            .Returns(async (MoveJob _, CancellationToken cancellationToken) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    processingCanceled.TrySetResult();
                    throw;
                }
            });
        var worker = new MoveBackgroundService(
            queue.Object,
            processor.Object,
            TestLibraryFilesystemReadiness.Ready(),
            NullLogger<MoveBackgroundService>.Instance,
            heartbeatInterval: TimeSpan.FromMilliseconds(10));

        await worker.StartAsync(CancellationToken.None);
        await processingCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, job.LeaseGeneration);
        queue.Verify(service => service.HeartbeatJobAsync(
            job.Id,
            It.IsAny<string>(),
            1,
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        queue.Verify(service => service.UpdateJobStatusAsync(
            job.Id,
            It.IsAny<string>(),
            It.IsAny<int>(),
            MoveJobStatus.Failed,
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
