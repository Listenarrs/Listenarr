using Microsoft.Extensions.Options;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "FilePublicationCapabilityResolverTests")]
[Trait("Category", "Infrastructure")]
public sealed class FilePublicationCapabilityResolverTests : BaseTests
{
    [Fact]
    public async Task ResolveAsync_WeakWritableDestination_DowngradesMoveToCopyAndRetain()
    {
        var root = BuildRoot();
        var repository = new Mock<IRootFolderRepository>(MockBehavior.Strict);
        repository.Setup(candidate => candidate.GetAllAsync())
            .ReturnsAsync([root]);
        var health = new Mock<IRootFolderStorageHealthResolver>(MockBehavior.Strict);
        health.Setup(candidate => candidate.ResolveAsync(
                root,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(WeakWritableObservation());
        var resolver = new FilePublicationCapabilityResolver(
            repository.Object,
            health.Object);

        var plan = await resolver.ResolveAsync(
            FileAction.Move,
            Path.Join(FileService.GetTempDirectory("publication-source"), "source.m4b"),
            Path.Join(root.Path, "book", "target.m4b"),
            DurableProof());

        Assert.True(plan.IsAllowed);
        Assert.Equal(
            FilePublicationExecutionMode.AdditiveCopyRetainSource,
            plan.Mode);
        Assert.Equal(FileAction.Copy, plan.EffectiveAction);
        Assert.Equal(
            FilePublicationSourceDisposition.Retained,
            plan.SourceDisposition);
        repository.VerifyAll();
        health.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_WeakModeDisabled_BlocksWithoutGrantingMutation()
    {
        var root = BuildRoot();
        var repository = new Mock<IRootFolderRepository>(MockBehavior.Strict);
        repository.Setup(candidate => candidate.GetAllAsync())
            .ReturnsAsync([root]);
        var health = new Mock<IRootFolderStorageHealthResolver>(MockBehavior.Strict);
        health.Setup(candidate => candidate.ResolveAsync(
                root,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(WeakWritableObservation());
        var resolver = new FilePublicationCapabilityResolver(
            repository.Object,
            health.Object,
            Options.Create(new FileMoverOptions
            {
                WeakPublicationMode = WeakPublicationMode.Disabled
            }));

        var plan = await resolver.ResolveAsync(
            FileAction.Move,
            Path.Join(FileService.GetTempDirectory("publication-disabled-source"), "source.m4b"),
            Path.Join(root.Path, "book", "target.m4b"),
            DurableProof());

        Assert.False(plan.IsAllowed);
        Assert.Equal(FilePublicationExecutionMode.Blocked, plan.Mode);
        Assert.Equal(
            "compatibility_publication_disabled",
            plan.ReasonCode);
        repository.VerifyAll();
        health.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_WeakMoveWithExplicitPolicy_UsesVerifiedCleanup()
    {
        var root = BuildRoot();
        root.WeakStorageSourceCleanupPolicy =
            WeakStorageSourceCleanupPolicy.DeleteSourceAfterVerifiedCopy;
        root.WeakStoragePolicyRevision = 7;
        root.StorageContractRevision = 11;
        var repository = new Mock<IRootFolderRepository>(MockBehavior.Strict);
        repository.Setup(candidate => candidate.GetAllAsync())
            .ReturnsAsync([root]);
        var health = new Mock<IRootFolderStorageHealthResolver>(MockBehavior.Strict);
        health.Setup(candidate => candidate.ResolveAsync(
                root,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(WeakWritableObservation());
        var resolver = new FilePublicationCapabilityResolver(
            repository.Object,
            health.Object);
        var batchId = Guid.NewGuid();

        var plan = await resolver.ResolveAsync(
            FileAction.Move,
            Path.Join(FileService.GetTempDirectory("verified-cleanup-source"), "source.m4b"),
            Path.Join(root.Path, "book", "target.m4b"),
            DurableProof(),
            compatibilityBatchId: batchId,
            cleanupOwner: CompatibilityCleanupOwner.Listenarr);

        Assert.True(plan.IsAllowed);
        Assert.Equal(
            FilePublicationExecutionMode.CompatibilityCopyVerifiedCleanup,
            plan.Mode);
        Assert.Equal(FileAction.Copy, plan.EffectiveAction);
        Assert.Equal(FilePublicationSourceDisposition.Retained, plan.SourceDisposition);
        Assert.Equal(batchId, plan.CompatibilityBatchId);
        Assert.Equal(CompatibilityCleanupOwner.Listenarr, plan.CleanupOwner);
        Assert.Equal(root.Id, plan.DestinationRootFolderId);
        Assert.Equal(7, plan.DestinationPolicyRevision);
        Assert.Equal(11, plan.DestinationStorageContractRevision);
        repository.VerifyAll();
        health.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_SourceMayOverlapUnresolvedManagedRoot_RetainsSource()
    {
        var destinationRoot = BuildRoot();
        destinationRoot.WeakStorageSourceCleanupPolicy =
            WeakStorageSourceCleanupPolicy.DeleteSourceAfterVerifiedCopy;
        destinationRoot.WeakStoragePolicyRevision = 7;
        destinationRoot.StorageContractRevision = 11;
        var sourceRoot = new RootFolder
        {
            Id = 92,
            Name = "Unresolved source root",
            Path = FileService.GetTempDirectory("publication-unresolved-source-root"),
            CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
            PathIdentityState = PathIdentityState.Unavailable,
            ResolvedCaseSensitivity = FileSystemCaseSensitivity.Unknown,
            WeakStorageSourceCleanupPolicy = WeakStorageSourceCleanupPolicy.RetainSource
        };
        var repository = new Mock<IRootFolderRepository>(MockBehavior.Strict);
        repository.Setup(candidate => candidate.GetAllAsync())
            .ReturnsAsync([sourceRoot, destinationRoot]);
        var health = new Mock<IRootFolderStorageHealthResolver>(MockBehavior.Strict);
        health.Setup(candidate => candidate.ResolveAsync(
                destinationRoot,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(WeakWritableObservation());
        var resolver = new FilePublicationCapabilityResolver(
            repository.Object,
            health.Object);

        var plan = await resolver.ResolveAsync(
            FileAction.Move,
            Path.Join(sourceRoot.Path, "incoming", "source.m4b"),
            Path.Join(destinationRoot.Path, "book", "target.m4b"),
            DurableProof(),
            compatibilityBatchId: Guid.NewGuid(),
            cleanupOwner: CompatibilityCleanupOwner.Listenarr);

        Assert.True(plan.IsAllowed);
        Assert.Equal(FilePublicationExecutionMode.AdditiveCopyRetainSource, plan.Mode);
        Assert.Equal(FilePublicationSourceDisposition.Retained, plan.SourceDisposition);
        Assert.Null(plan.SourceRootFolderId);
        repository.VerifyAll();
        health.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_WeakPolicyWithoutRetirementCapability_RetainsSource()
    {
        var root = BuildRoot();
        root.WeakStorageSourceCleanupPolicy =
            WeakStorageSourceCleanupPolicy.DeleteSourceAfterVerifiedCopy;
        var repository = new Mock<IRootFolderRepository>(MockBehavior.Strict);
        repository.Setup(candidate => candidate.GetAllAsync())
            .ReturnsAsync([root]);
        var health = new Mock<IRootFolderStorageHealthResolver>(MockBehavior.Strict);
        health.Setup(candidate => candidate.ResolveAsync(
                root,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(WeakWritableObservation() with
            {
                CanRetireAfterVerifiedCopy = false
            });
        var resolver = new FilePublicationCapabilityResolver(
            repository.Object,
            health.Object);

        var plan = await resolver.ResolveAsync(
            FileAction.Move,
            Path.Join(root.Path, "incoming", "source.m4b"),
            Path.Join(root.Path, "book", "target.m4b"),
            DurableProof(),
            compatibilityBatchId: Guid.NewGuid(),
            cleanupOwner: CompatibilityCleanupOwner.Listenarr);

        Assert.True(plan.IsAllowed);
        Assert.Equal(FilePublicationExecutionMode.AdditiveCopyRetainSource, plan.Mode);
        Assert.Equal(FilePublicationSourceDisposition.Retained, plan.SourceDisposition);
        repository.VerifyAll();
        health.VerifyAll();
    }

    [Theory]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task ResolveAsync_ExplicitCopyNeverAuthorizesSourceCleanup(FileAction action)
    {
        var root = BuildRoot();
        root.WeakStorageSourceCleanupPolicy =
            WeakStorageSourceCleanupPolicy.DeleteSourceAfterVerifiedCopy;
        var repository = new Mock<IRootFolderRepository>(MockBehavior.Strict);
        repository.Setup(candidate => candidate.GetAllAsync())
            .ReturnsAsync([root]);
        var health = new Mock<IRootFolderStorageHealthResolver>(MockBehavior.Strict);
        health.Setup(candidate => candidate.ResolveAsync(
                root,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(WeakWritableObservation());
        var resolver = new FilePublicationCapabilityResolver(
            repository.Object,
            health.Object);

        var plan = await resolver.ResolveAsync(
            action,
            Path.Join(FileService.GetTempDirectory("explicit-copy-source"), "source.m4b"),
            Path.Join(root.Path, "book", "target.m4b"),
            DurableProof(),
            compatibilityBatchId: Guid.NewGuid(),
            cleanupOwner: CompatibilityCleanupOwner.Listenarr);

        Assert.Equal(FilePublicationExecutionMode.AdditiveCopyRetainSource, plan.Mode);
        Assert.Equal(FilePublicationSourceDisposition.Retained, plan.SourceDisposition);
        repository.VerifyAll();
        health.VerifyAll();
    }

    private RootFolder BuildRoot()
    {
        var path = FileService.GetTempDirectory("publication-capability-root");
        return new RootFolder
        {
            Id = 91,
            Name = "Weak storage",
            Path = path,
            PathIdentityState = PathIdentityState.Valid,
            ResolvedCaseSensitivity =
                FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity,
            CaseSensitivityMode =
                FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity
                    == FileSystemCaseSensitivity.Sensitive
                    ? FileSystemCaseSensitivityMode.Sensitive
                    : FileSystemCaseSensitivityMode.Insensitive
        };
    }

    private static RootFolderStorageObservation WeakWritableObservation() =>
        new(
            RootFolderStorageState.Limited,
            RootFolderStorageReason.IdentityUnsupported,
            "Durable identity is unavailable.",
            CanConfirmCurrentFolder: false,
            CanChangePath: true,
            CanMutateFilesystem: false,
            ConfirmationToken: null,
            CanPublishNewFiles: true,
            CanRetireWithDurableIdentity: false,
            CanRetireAfterVerifiedCopy: true);

    private static FilePublicationSourceProof DurableProof() =>
        new(
            "durable:test",
            5,
            new string('A', 64));
}
