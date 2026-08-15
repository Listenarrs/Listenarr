using System.Threading.Channels;
using Listenarr.Tests.Builders;
using Microsoft.Extensions.Logging.Abstractions;

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Scanning;

[Trait("Name", "ScanBackgroundServiceTests")]
[Trait("Category", "BackgroundWorkers")]
public sealed class ScanBackgroundServiceTests : BaseTests
{
    [Fact]
    public async Task ExecuteAsync_FilesystemNotReady_DoesNotConsumeJobsUntilGateOpens()
    {
        var channel = Channel.CreateUnbounded<ScanJob>();
        var job = new ScanJob { AudiobookId = 500 };
        await channel.Writer.WriteAsync(job);
        var queue = new Mock<IScanQueueService>(MockBehavior.Strict);
        queue.SetupGet(service => service.Reader).Returns(channel.Reader);
        var historyRepository = new Mock<IHistoryRepository>();
        var audiobookRepository = new Mock<IAudiobookRepository>();
        await using var services = new ServiceCollection()
            .AddSingleton(historyRepository.Object)
            .AddSingleton(audiobookRepository.Object)
            .BuildServiceProvider();
        var handoffStore = new Mock<IMoveScanHandoffStore>(MockBehavior.Strict);
        handoffStore.Setup(store => store.GetClaimableIdsAsync(
                It.IsAny<DateTimeOffset>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var recovery = new MoveScanHandoffRecoveryService(
            queue.Object,
            handoffStore.Object,
            services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<MoveScanHandoffRecoveryService>.Instance);
        var readiness = new TestLibraryFilesystemReadiness();
        readiness.SetRunning("AudiobookFileIdentities");
        var processed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = new Mock<IScanJobProcessor>(MockBehavior.Strict);
        processor.Setup(service => service.ProcessJobAsync(job, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                processed.TrySetResult();
                return Task.CompletedTask;
            });
        var service = new ScanBackgroundService(
            queue.Object,
            processor.Object,
            recovery,
            new ImmediateCycleRunner(),
            readiness,
            NullLogger<ScanBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);

        Assert.False(processed.Task.IsCompleted);
        processor.Verify(service => service.ProcessJobAsync(
            It.IsAny<ScanJob>(),
            It.IsAny<CancellationToken>()), Times.Never);
        handoffStore.Verify(store => store.GetClaimableIdsAsync(
            It.IsAny<DateTimeOffset>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);

        readiness.SetReady();
        await processed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        processor.Verify(service => service.ProcessJobAsync(
            job,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ProcessorFailure_ContinuesWithLaterJobs()
    {
        var queue = new ScanQueueService(
            NullLogger<ScanQueueService>.Instance);
        var historyRepository = new Mock<IHistoryRepository>();
        var audiobookRepository = new Mock<IAudiobookRepository>();
        var services = new ServiceCollection()
            .AddSingleton(historyRepository.Object)
            .AddSingleton(audiobookRepository.Object)
            .BuildServiceProvider();
        var handoffStore = new Mock<IMoveScanHandoffStore>();
        handoffStore.Setup(store => store.GetClaimableIdsAsync(
                It.IsAny<DateTimeOffset>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var recovery = new MoveScanHandoffRecoveryService(
            queue,
            handoffStore.Object,
            services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<MoveScanHandoffRecoveryService>.Instance);
        var processor = new Mock<IScanJobProcessor>();
        var invocation = 0;
        var secondProcessed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        processor.Setup(service => service.ProcessJobAsync(
                It.IsAny<ScanJob>(),
                It.IsAny<CancellationToken>()))
            .Returns<ScanJob, CancellationToken>((_, _) =>
            {
                if (Interlocked.Increment(ref invocation) == 1)
                {
                    return Task.FromException(new InvalidOperationException("first scan failed"));
                }

                secondProcessed.TrySetResult();
                return Task.CompletedTask;
            });
        var service = new ScanBackgroundService(
            queue,
            processor.Object,
            recovery,
            new ImmediateCycleRunner(),
            TestLibraryFilesystemReadiness.Ready(),
            NullLogger<ScanBackgroundService>.Instance);
        var audiobook = new AudiobookBuilder()
            .WithId(501)
            .WithTitle("Worker Continuation")
            .Build();
        var firstId = await queue.EnqueueScanAsync(audiobook);
        await queue.EnqueueScanAsync(
            new AudiobookBuilder()
                .WithId(502)
                .WithTitle("Worker Continuation Two")
                .Build());

        await service.StartAsync(CancellationToken.None);
        try
        {
            await secondProcessed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(queue.TryGetJob(firstId, out var first));
            var firstJob = Assert.IsType<ScanJob>(first);
            Assert.Equal("Failed", firstJob.Status);
            processor.Verify(candidate => candidate.ProcessJobAsync(
                It.IsAny<ScanJob>(),
                It.IsAny<CancellationToken>()), Times.Exactly(2));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await services.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecuteAsync_StatusUpdateFailure_DoesNotStopLaterJobs()
    {
        var channel = Channel.CreateUnbounded<ScanJob>();
        var first = new ScanJob { AudiobookId = 601 };
        var second = new ScanJob { AudiobookId = 602 };
        await channel.Writer.WriteAsync(first);
        await channel.Writer.WriteAsync(second);
        var queue = new Mock<IScanQueueService>(MockBehavior.Strict);
        queue.SetupGet(service => service.Reader).Returns(channel.Reader);
        queue.Setup(service => service.UpdateJobStatus(
                first.Id,
                "Failed",
                "first scan failed",
                null,
                null))
            .Throws(new InvalidOperationException("status store unavailable"));
        var historyRepository = new Mock<IHistoryRepository>();
        var audiobookRepository = new Mock<IAudiobookRepository>();
        await using var services = new ServiceCollection()
            .AddSingleton(historyRepository.Object)
            .AddSingleton(audiobookRepository.Object)
            .BuildServiceProvider();
        var handoffStore = new Mock<IMoveScanHandoffStore>();
        handoffStore.Setup(store => store.GetClaimableIdsAsync(
                It.IsAny<DateTimeOffset>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var recovery = new MoveScanHandoffRecoveryService(
            queue.Object,
            handoffStore.Object,
            services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<MoveScanHandoffRecoveryService>.Instance);
        var processor = new Mock<IScanJobProcessor>();
        var secondProcessed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        processor.Setup(service => service.ProcessJobAsync(
                first,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("first scan failed"));
        processor.Setup(service => service.ProcessJobAsync(
                second,
                It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                secondProcessed.TrySetResult();
                return Task.CompletedTask;
            });
        var service = new ScanBackgroundService(
            queue.Object,
            processor.Object,
            recovery,
            new ImmediateCycleRunner(),
            TestLibraryFilesystemReadiness.Ready(),
            NullLogger<ScanBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await secondProcessed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            processor.Verify(candidate => candidate.ProcessJobAsync(
                second,
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private sealed class ImmediateCycleRunner : IWorkerCycleRunner
    {
        public async Task RunPeriodicAsync(
            string workerName,
            TimeSpan? initialDelay,
            Func<TimeSpan> intervalProvider,
            Func<CancellationToken, Task> runCycle,
            CancellationToken cancellationToken)
        {
            await runCycle(cancellationToken);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }
}
