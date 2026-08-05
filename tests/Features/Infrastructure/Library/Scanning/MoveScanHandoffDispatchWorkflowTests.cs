using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Library.Scanning;

[Trait("Area", "LibraryScanning")]
[Trait("Name", "MoveScanHandoffDispatchWorkflowTests")]
[Trait("Category", "Infrastructure")]
public sealed class MoveScanHandoffDispatchWorkflowTests : BaseTests
{
    [Fact]
    public async Task TryDispatchPendingAsync_InternalAuthorizationCancellation_ReleasesClaimForRecovery()
    {
        var handoffId = Guid.NewGuid();
        var target = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"move-scan-dispatch-{Guid.NewGuid():N}");
        var boundary = Path.GetPathRoot(Path.GetFullPath(target))
            ?? throw new InvalidOperationException("Test target root is unavailable.");
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        var identity = PathIdentitySnapshot.FromResolution(
            semantics,
            FileSystemCaseSensitivityMode.Auto,
            boundary,
            target);
        var claim = new MoveScanHandoffClaim(
            handoffId,
            Guid.NewGuid(),
            4401,
            target,
            identity,
            [],
            AttemptGeneration: 1,
            LeaseOwner: "dispatch-test-owner",
            LeaseGeneration: 2);
        var handoffStore = new Mock<IMoveScanHandoffStore>(MockBehavior.Strict);
        handoffStore.Setup(store => store.TryClaimAsync(
                handoffId,
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(claim);
        handoffStore.Setup(store => store.ReleaseClaimAsync(
                handoffId,
                claim.LeaseOwner,
                claim.LeaseGeneration,
                It.Is<string?>(error => error != null && error.Contains("cancellation", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var authorization = new Mock<IScanPathAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(service => service.AuthorizeAsync(
                target,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException(
                "Injected internal authorization cancellation."));
        using var provider = new ServiceCollection()
            .AddSingleton(authorization.Object)
            .BuildServiceProvider();
        var scanQueue = new Mock<IScanQueueService>(MockBehavior.Strict);

        var result = await MoveScanHandoffDispatchWorkflow.TryDispatchPendingAsync(
            handoffId,
            ownerPrefix: "dispatch-test",
            knownAudiobook: new Audiobook { Id = claim.AudiobookId, Title = "Book" },
            beforeEnqueue: null,
            scanQueue.Object,
            handoffStore.Object,
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(MoveScanDispatchOutcome.Failed, result.Outcome);
        Assert.Null(result.ScanJobId);
        handoffStore.Verify(store => store.ReleaseClaimAsync(
            handoffId,
            claim.LeaseOwner,
            claim.LeaseGeneration,
            It.IsAny<string?>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);
        scanQueue.VerifyNoOtherCalls();
    }
}
