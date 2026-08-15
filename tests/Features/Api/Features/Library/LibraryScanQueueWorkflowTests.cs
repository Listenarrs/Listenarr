using Listenarr.Application.Common.Exceptions;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Tests.Features.Api.Features.Library;

[Trait("Area", "LibraryApi")]
[Trait("Name", "LibraryScanQueueWorkflowTests")]
[Trait("Category", "LibraryController")]
public sealed class LibraryScanQueueWorkflowTests : BaseTests
{
    [Fact]
    public async Task TryEnqueueAsync_FilesystemInitializing_FailsBeforeQueuePublication()
    {
        var queue = new Mock<IScanQueueService>(MockBehavior.Strict);
        var broadcaster = new Mock<IHubBroadcaster>(MockBehavior.Strict);
        using var provider = BuildProvider(broadcaster.Object);
        var readiness = new TestLibraryFilesystemReadiness();
        readiness.SetRunning("AudiobookFileIdentities");
        var workflow = new LibraryScanQueueWorkflow(
            provider.GetRequiredService<IServiceScopeFactory>(),
            readiness,
            Mock.Of<ILogger<LibraryScanQueueWorkflow>>(),
            queue.Object);

        var exception = await Assert.ThrowsAsync<ApplicationUnavailableException>(() =>
            workflow.TryEnqueueAsync(
                new Audiobook { Id = 4400, Title = "Blocked scan" },
                requestedPath: null,
                pathIdentity: null,
                physicalIdentity: null,
                isAuthoritativeScope: true));

        Assert.Equal("filesystem_initializing", exception.Code);
        queue.Verify(service => service.EnqueueScanAsync(It.IsAny<ScanEnqueueCommand>()), Times.Never);
        broadcaster.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TryEnqueueAsync_BroadcastCancellationAfterDurableEnqueue_ReturnsAccepted()
    {
        var jobId = Guid.NewGuid();
        var queue = new Mock<IScanQueueService>(MockBehavior.Strict);
        queue.Setup(service => service.EnqueueScanAsync(It.IsAny<ScanEnqueueCommand>()))
            .ReturnsAsync(jobId);
        var broadcaster = CreateCanceledBroadcaster();
        using var provider = BuildProvider(broadcaster.Object);
        var workflow = new LibraryScanQueueWorkflow(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TestLibraryFilesystemReadiness.Ready(),
            Mock.Of<ILogger<LibraryScanQueueWorkflow>>(),
            queue.Object);

        var result = await workflow.TryEnqueueAsync(
            new Audiobook { Id = 4401, Title = "Queued scan" },
            requestedPath: null,
            pathIdentity: null,
            physicalIdentity: null,
            isAuthoritativeScope: true);

        var accepted = Assert.IsType<AcceptedResult>(result);
        Assert.Equal(202, accepted.StatusCode);
        queue.Verify(service => service.EnqueueScanAsync(It.IsAny<ScanEnqueueCommand>()), Times.Once);
        broadcaster.Verify(service => service.BroadcastAsync(
            RealtimeHubTarget.Downloads,
            "ScanJobUpdate",
            It.IsAny<object>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RequeueAsync_BroadcastCancellationAfterDurableRequeue_ReturnsAccepted()
    {
        var originalJobId = Guid.NewGuid();
        var newJobId = Guid.NewGuid();
        var queue = new Mock<IScanQueueService>(MockBehavior.Strict);
        queue.Setup(service => service.RequeueScanAsync(originalJobId))
            .ReturnsAsync(newJobId);
        var broadcaster = CreateCanceledBroadcaster();
        using var provider = BuildProvider(broadcaster.Object);
        var workflow = new LibraryScanQueueWorkflow(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TestLibraryFilesystemReadiness.Ready(),
            Mock.Of<ILogger<LibraryScanQueueWorkflow>>(),
            queue.Object);

        var result = await workflow.RequeueAsync(originalJobId.ToString());

        var accepted = Assert.IsType<AcceptedResult>(result);
        Assert.Equal(202, accepted.StatusCode);
        queue.Verify(service => service.RequeueScanAsync(originalJobId), Times.Once);
        broadcaster.Verify(service => service.BroadcastAsync(
            RealtimeHubTarget.Downloads,
            "ScanJobUpdate",
            It.IsAny<object>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Mock<IHubBroadcaster> CreateCanceledBroadcaster()
    {
        var broadcaster = new Mock<IHubBroadcaster>(MockBehavior.Strict);
        broadcaster.Setup(service => service.BroadcastAsync(
                RealtimeHubTarget.Downloads,
                "ScanJobUpdate",
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("Injected post-commit broadcast cancellation."));
        return broadcaster;
    }

    private static ServiceProvider BuildProvider(IHubBroadcaster broadcaster) =>
        new ServiceCollection()
            .AddSingleton(broadcaster)
            .BuildServiceProvider();
}
