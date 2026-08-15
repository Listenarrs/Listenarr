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
        var audiobookRepository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        audiobookRepository.Setup(repository => repository.GetPathReferenceSnapshotAsync(
                claim.AudiobookId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AudiobookPathReferenceSnapshot(
                claim.AudiobookId,
                target,
                FilePath: null));
        var authorization = new Mock<IScanPathAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(service => service.AuthorizeAsync(
                target,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException(
                "Injected internal authorization cancellation."));
        using var provider = new ServiceCollection()
            .AddSingleton(audiobookRepository.Object)
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

    [Fact]
    public async Task TryDispatchPendingAsync_NewerAudiobookDestination_SupersedesBeforeAuthorization()
    {
        var handoffId = Guid.NewGuid();
        var oldTarget = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"move-scan-stale-{Guid.NewGuid():N}");
        var currentTarget = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"move-scan-current-{Guid.NewGuid():N}");
        var boundary = Path.GetPathRoot(Path.GetFullPath(oldTarget))
            ?? throw new InvalidOperationException("Test target root is unavailable.");
        var identity = PathIdentitySnapshot.FromResolution(
            FileSystemPathSemantics.CurrentHostDefault,
            FileSystemCaseSensitivityMode.Auto,
            boundary,
            oldTarget);
        var claim = new MoveScanHandoffClaim(
            handoffId,
            Guid.NewGuid(),
            4402,
            oldTarget,
            identity,
            [],
            AttemptGeneration: 101,
            LeaseOwner: "dispatch-stale-owner",
            LeaseGeneration: 102);
        var handoffStore = new Mock<IMoveScanHandoffStore>(MockBehavior.Strict);
        handoffStore.Setup(store => store.TryClaimAsync(
                handoffId,
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(claim);
        handoffStore.Setup(store => store.CompleteAttemptAsync(
                handoffId,
                claim.AttemptGeneration,
                scanJobId: null,
                MoveScanTerminalOutcome.Superseded,
                It.Is<string?>(error => error != null && error.Contains("newer", StringComparison.OrdinalIgnoreCase)),
                0,
                0,
                oldTarget,
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MoveScanAttemptResult(
                MoveScanAttemptOutcome.Superseded,
                null));
        var audiobookRepository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        audiobookRepository.Setup(repository => repository.GetPathReferenceSnapshotAsync(
                claim.AudiobookId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AudiobookPathReferenceSnapshot(
                claim.AudiobookId,
                currentTarget,
                FilePath: null));
        var authorization = new Mock<IScanPathAuthorizationService>(MockBehavior.Strict);
        var scanQueue = new Mock<IScanQueueService>(MockBehavior.Strict);
        using var provider = new ServiceCollection()
            .AddSingleton(audiobookRepository.Object)
            .AddSingleton(authorization.Object)
            .BuildServiceProvider();

        var result = await MoveScanHandoffDispatchWorkflow.TryDispatchPendingAsync(
            handoffId,
            ownerPrefix: "dispatch-stale-test",
            knownAudiobook: null,
            beforeEnqueue: null,
            scanQueue.Object,
            handoffStore.Object,
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(MoveScanDispatchOutcome.Superseded, result.Outcome);
        Assert.Null(result.ScanJobId);
        authorization.VerifyNoOtherCalls();
        scanQueue.VerifyNoOtherCalls();
        handoffStore.Verify(store => store.CompleteAttemptAsync(
            handoffId,
            claim.AttemptGeneration,
            null,
            MoveScanTerminalOutcome.Superseded,
            It.IsAny<string?>(),
            0,
            0,
            oldTarget,
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [WindowsFact]
    public async Task VerifyPublishedManifestAsync_HashlessNativeRenameReplacementGeneration_RequiresAttention()
    {
        var target = FileService.GetTempDirectory("move-scan-native-replacement");
        var filePath = await FileService.GetFileAsync(
            target,
            "book.mp3",
            "audio");
        var originalLastWriteTimeUtc = File.GetLastWriteTimeUtc(filePath);
        string originalIdentity;
        using (var targetAnchor = PinnedDirectoryCreation.OpenPinnedBoundary(target))
        using (var file = targetAnchor.OpenExistingFile(
            Path.GetFileName(filePath),
            requireDeleteAccess: false))
        {
            originalIdentity = file.GetObjectIdentity();
        }

        File.Delete(filePath);
        await File.WriteAllTextAsync(filePath, "audio");
        File.SetLastWriteTimeUtc(filePath, originalLastWriteTimeUtc);
        using (var targetAnchor = PinnedDirectoryCreation.OpenPinnedBoundary(target))
        using (var replacement = targetAnchor.OpenExistingFile(
            Path.GetFileName(filePath),
            requireDeleteAccess: false))
        {
            Assert.NotEqual(originalIdentity, replacement.GetObjectIdentity());
        }

        var entry = new MoveJobEntry
        {
            RelativePath = "book.mp3",
            EntryType = MoveJobEntryType.File,
            Length = new FileInfo(filePath).Length,
            LastWriteTimeUtc = originalLastWriteTimeUtc,
            Sha256 = null,
            CopyState = MoveJobEntryCopyState.Verified,
            CleanupState = MoveJobEntryCleanupState.Deleted,
            SourcePhysicalObjectIdentity = originalIdentity,
            TargetPhysicalObjectIdentity = originalIdentity
        };

        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            AudiobookContentMoveService.VerifyPublishedManifestAsync(
                target,
                [entry],
                FileSystemPathSemantics.CurrentHostDefault,
                CancellationToken.None));

        Assert.Contains("generation", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryDispatchPendingAsync_HashlessNativeRenameManifest_DispatchesByPhysicalGeneration()
    {
        var target = FileService.GetTempDirectory("move-scan-native-target");
        var filePath = await FileService.GetFileAsync(
            target,
            "book.mp3",
            "audio");
        string fileIdentity;
        var lastWriteTimeUtc = File.GetLastWriteTimeUtc(filePath);
        using (var targetAnchor = PinnedDirectoryCreation.OpenPinnedBoundary(target))
        using (var file = targetAnchor.OpenExistingFile(
            Path.GetFileName(filePath),
            requireDeleteAccess: false))
        {
            fileIdentity = file.GetObjectIdentity();
        }

        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        var identity = PathIdentitySnapshot.FromResolution(
            semantics,
            FileSystemCaseSensitivityMode.Auto,
            target,
            target);
        var entry = new MoveJobEntry
        {
            RelativePath = "book.mp3",
            EntryType = MoveJobEntryType.File,
            Length = new FileInfo(filePath).Length,
            LastWriteTimeUtc = lastWriteTimeUtc,
            Sha256 = null,
            CopyState = MoveJobEntryCopyState.Verified,
            CleanupState = MoveJobEntryCleanupState.Deleted,
            SourcePhysicalObjectIdentity = fileIdentity,
            TargetPhysicalObjectIdentity = fileIdentity
        };
        var handoffId = Guid.NewGuid();
        var claim = new MoveScanHandoffClaim(
            handoffId,
            Guid.NewGuid(),
            4403,
            target,
            identity,
            [entry],
            AttemptGeneration: 1,
            LeaseOwner: "dispatch-native-owner",
            LeaseGeneration: 1);
        var handoffStore = new Mock<IMoveScanHandoffStore>(MockBehavior.Strict);
        handoffStore.Setup(store => store.TryClaimAsync(
                handoffId,
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(claim);
        var audiobookRepository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        audiobookRepository.Setup(repository => repository.GetPathReferenceSnapshotAsync(
                claim.AudiobookId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AudiobookPathReferenceSnapshot(
                claim.AudiobookId,
                target,
                FilePath: null));
        var physicalIdentity = new ScanPathPhysicalIdentity(
            "boundary-generation",
            "scan-root-generation");
        var authorizationResult = ScanPathAuthorizationResult.Authorized(
            target,
            identity,
            physicalIdentity);
        var authorization = new Mock<IScanPathAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(service => service.AuthorizeAsync(
                target,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(authorizationResult);
        var scanJobId = Guid.NewGuid();
        var scanQueue = new Mock<IScanQueueService>(MockBehavior.Strict);
        scanQueue.Setup(queue => queue.EnqueueMoveHandoffScanAsync(
                It.Is<Audiobook>(audiobook =>
                    audiobook.Id == claim.AudiobookId
                    && audiobook.BasePath == target),
                claim,
                physicalIdentity))
            .ReturnsAsync(scanJobId);
        using var provider = new ServiceCollection()
            .AddSingleton(audiobookRepository.Object)
            .AddSingleton(authorization.Object)
            .BuildServiceProvider();

        var result = await MoveScanHandoffDispatchWorkflow.TryDispatchPendingAsync(
            handoffId,
            ownerPrefix: "dispatch-native-test",
            knownAudiobook: new Audiobook { Id = claim.AudiobookId, Title = "Book" },
            beforeEnqueue: null,
            scanQueue.Object,
            handoffStore.Object,
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(MoveScanDispatchOutcome.Dispatched, result.Outcome);
        Assert.Equal(scanJobId, result.ScanJobId);
        authorization.Verify(service => service.AuthorizeAsync(
            target,
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        scanQueue.VerifyAll();
    }
}
