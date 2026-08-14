using System.Threading.Channels;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Library.Scanning;

[Trait("Name", "UnmatchedScanBackgroundServiceTests")]
[Trait("Category", "BackgroundWorkers")]
public sealed class UnmatchedScanBackgroundServiceTests : BaseTests
{
    [Fact]
    public async Task ExecuteAsync_NonFatalProcessorFailure_ContinuesWithLaterJobs()
    {
        var first = new UnmatchedScanJob { RootFolderPath = "/library/first" };
        var second = new UnmatchedScanJob { RootFolderPath = "/library/second" };
        var channel = Channel.CreateUnbounded<UnmatchedScanJob>();
        await channel.Writer.WriteAsync(first);
        await channel.Writer.WriteAsync(second);
        var queue = CreateQueue(channel, first);
        var processor = new Mock<IUnmatchedScanProcessor>(MockBehavior.Strict);
        var secondProcessed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        processor.Setup(candidate => candidate.ProcessJobAsync(
                It.IsAny<UnmatchedScanJob>(),
                It.IsAny<CancellationToken>()))
            .Returns<UnmatchedScanJob, CancellationToken>((job, _) =>
            {
                if (job.Id == first.Id)
                {
                    return Task.FromException(
                        new InvalidOperationException("first scan failed"));
                }

                secondProcessed.TrySetResult();
                return Task.CompletedTask;
            });
        var hub = CreateHubContext(notificationFailure: null);
        var service = CreateService(queue, processor, hub);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await secondProcessed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            queue.Verify(candidate => candidate.UpdateJob(
                first.Id,
                "Failed",
                null,
                "first scan failed"), Times.Once);
            processor.Verify(candidate => candidate.ProcessJobAsync(
                second,
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_InternalCancellation_ContinuesWithLaterJobs()
    {
        var first = new UnmatchedScanJob { RootFolderPath = "/library/first" };
        var second = new UnmatchedScanJob { RootFolderPath = "/library/second" };
        var channel = Channel.CreateUnbounded<UnmatchedScanJob>();
        await channel.Writer.WriteAsync(first);
        await channel.Writer.WriteAsync(second);
        var queue = CreateQueue(channel, first);
        var processor = new Mock<IUnmatchedScanProcessor>(MockBehavior.Strict);
        var secondProcessed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        processor.Setup(candidate => candidate.ProcessJobAsync(
                It.IsAny<UnmatchedScanJob>(),
                It.IsAny<CancellationToken>()))
            .Returns<UnmatchedScanJob, CancellationToken>((job, _) =>
            {
                if (job.Id == first.Id)
                {
                    return Task.FromCanceled(new CancellationToken(canceled: true));
                }

                secondProcessed.TrySetResult();
                return Task.CompletedTask;
            });
        var hub = CreateHubContext(notificationFailure: null);
        var service = CreateService(queue, processor, hub);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await secondProcessed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            queue.Verify(candidate => candidate.UpdateJob(
                first.Id,
                "Failed",
                null,
                It.IsAny<string?>()), Times.Once);
            processor.Verify(candidate => candidate.ProcessJobAsync(
                second,
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ProcessorFailureAfterCompletion_PreservesCompletedJob()
    {
        var first = new UnmatchedScanJob
        {
            RootFolderPath = "/library/first",
            Status = "Completed",
            Results = []
        };
        var second = new UnmatchedScanJob { RootFolderPath = "/library/second" };
        var channel = Channel.CreateUnbounded<UnmatchedScanJob>();
        await channel.Writer.WriteAsync(first);
        await channel.Writer.WriteAsync(second);
        var queue = CreateQueue(channel, first);
        var processor = new Mock<IUnmatchedScanProcessor>(MockBehavior.Strict);
        var secondProcessed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        processor.Setup(candidate => candidate.ProcessJobAsync(
                It.IsAny<UnmatchedScanJob>(),
                It.IsAny<CancellationToken>()))
            .Returns<UnmatchedScanJob, CancellationToken>((job, _) =>
            {
                if (job.Id == first.Id)
                {
                    return Task.FromException(
                        new HubException("completion notification failed"));
                }

                secondProcessed.TrySetResult();
                return Task.CompletedTask;
            });
        var hub = CreateHubContext(notificationFailure: null);
        var service = CreateService(queue, processor, hub);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await secondProcessed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            queue.Verify(candidate => candidate.UpdateJob(
                first.Id,
                "Failed",
                It.IsAny<List<UnmatchedFileResult>?>(),
                It.IsAny<string?>()), Times.Never);
            Assert.Equal("Completed", first.Status);
            processor.Verify(candidate => candidate.ProcessJobAsync(
                second,
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_FailedStatusUpdateFailure_ContinuesWithLaterJobs()
    {
        var first = new UnmatchedScanJob { RootFolderPath = "/library/first" };
        var second = new UnmatchedScanJob { RootFolderPath = "/library/second" };
        var channel = Channel.CreateUnbounded<UnmatchedScanJob>();
        await channel.Writer.WriteAsync(first);
        await channel.Writer.WriteAsync(second);
        var queue = CreateQueue(
            channel,
            first,
            new InvalidOperationException("status store unavailable"));
        var processor = CreateFirstFailureProcessor(first, second, out var secondProcessed);
        var hub = CreateHubContext(notificationFailure: null);
        var service = CreateService(queue, processor, hub);

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

    [Fact]
    public async Task ExecuteAsync_FailureNotificationFailure_ContinuesWithLaterJobs()
    {
        var first = new UnmatchedScanJob { RootFolderPath = "/library/first" };
        var second = new UnmatchedScanJob { RootFolderPath = "/library/second" };
        var channel = Channel.CreateUnbounded<UnmatchedScanJob>();
        await channel.Writer.WriteAsync(first);
        await channel.Writer.WriteAsync(second);
        var queue = CreateQueue(channel, first);
        var processor = CreateFirstFailureProcessor(first, second, out var secondProcessed);
        var hub = CreateHubContext(new HubException("hub unavailable"));
        var service = CreateService(queue, processor, hub);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await secondProcessed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            queue.Verify(candidate => candidate.UpdateJob(
                first.Id,
                "Failed",
                null,
                "first scan failed"), Times.Once);
            processor.Verify(candidate => candidate.ProcessJobAsync(
                second,
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static UnmatchedScanBackgroundService CreateService(
        Mock<IUnmatchedScanQueueService> queue,
        Mock<IUnmatchedScanProcessor> processor,
        Mock<IHubContext<SettingsHub>> hub) =>
        new(
            queue.Object,
            processor.Object,
            TestLibraryFilesystemReadiness.Ready(),
            NullLogger<UnmatchedScanBackgroundService>.Instance,
            hub.Object,
            new Mock<IAppMetricsService>().Object);

    private static Mock<IUnmatchedScanProcessor> CreateFirstFailureProcessor(
        UnmatchedScanJob first,
        UnmatchedScanJob second,
        out TaskCompletionSource secondProcessed)
    {
        var processor = new Mock<IUnmatchedScanProcessor>(MockBehavior.Strict);
        secondProcessed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = secondProcessed;
        processor.Setup(candidate => candidate.ProcessJobAsync(
                It.IsAny<UnmatchedScanJob>(),
                It.IsAny<CancellationToken>()))
            .Returns<UnmatchedScanJob, CancellationToken>((job, _) =>
            {
                if (job.Id == first.Id)
                {
                    return Task.FromException(
                        new InvalidOperationException("first scan failed"));
                }

                if (job.Id == second.Id)
                {
                    completion.TrySetResult();
                }
                return Task.CompletedTask;
            });
        return processor;
    }

    private static Mock<IUnmatchedScanQueueService> CreateQueue(
        Channel<UnmatchedScanJob> channel,
        UnmatchedScanJob failedJob,
        Exception? statusFailure = null)
    {
        var queue = new Mock<IUnmatchedScanQueueService>(MockBehavior.Strict);
        queue.SetupGet(candidate => candidate.Reader).Returns(channel.Reader);
        UnmatchedScanJob? currentJob = failedJob;
        queue.Setup(candidate => candidate.TryGetJob(
                failedJob.Id,
                out currentJob))
            .Returns(true);
        var update = queue.Setup(candidate => candidate.UpdateJob(
            failedJob.Id,
            "Failed",
            null,
            It.IsAny<string?>()));
        if (statusFailure != null)
        {
            update.Throws(statusFailure);
        }
        else
        {
            update.Callback<Guid, string, List<UnmatchedFileResult>?, string?>(
                (_, status, results, error) =>
                {
                    failedJob.Status = status;
                    failedJob.Error = error;
                    if (results != null)
                    {
                        failedJob.Results = results;
                    }
                });
        }

        return queue;
    }

    private static Mock<IHubContext<SettingsHub>> CreateHubContext(
        Exception? notificationFailure)
    {
        var proxy = new Mock<IClientProxy>(MockBehavior.Strict);
        var send = proxy.Setup(candidate => candidate.SendCoreAsync(
            "UnmatchedScanComplete",
            It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()));
        if (notificationFailure == null)
        {
            send.Returns(Task.CompletedTask);
        }
        else
        {
            send.ThrowsAsync(notificationFailure);
        }

        var clients = new Mock<IHubClients>(MockBehavior.Strict);
        clients.SetupGet(candidate => candidate.All).Returns(proxy.Object);
        var hub = new Mock<IHubContext<SettingsHub>>(MockBehavior.Strict);
        hub.SetupGet(candidate => candidate.Clients).Returns(clients.Object);
        return hub;
    }
}
