using Microsoft.Extensions.Options;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "FilePublicationCapabilityResolverTests")]
[Trait("Category", "Infrastructure")]
public sealed class FilePublicationCapabilityResolverTests : BaseTests
{
    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, true, true)]
    public async Task ResolveAsync_UnresolvedBoundary_DoesNotBorrowOuterOrExternalAuthority(
        bool unresolvedSource, bool paddedRoot, bool withOuterRoot, bool equalBoundary)
    {
        var destinationRoot = BuildRoot();
        var outer = BuildRoot();
        outer.Id = 93;
        outer.Path = FileService.GetTempDirectory("publication-source-boundary");
        var unresolvedPath = equalBoundary ? outer.Path : Path.Join(outer.Path, "nested");
        var unresolved = new RootFolder
        {
            Id = 92,
            Name = "Unavailable boundary",
            Path = paddedRoot ? " " + unresolvedPath + " " : unresolvedPath,
            CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
            PathIdentityState = PathIdentityState.Unavailable,
            ResolvedCaseSensitivity = FileSystemCaseSensitivity.Unknown
        };
        var roots = new List<RootFolder> { destinationRoot, unresolved };
        if (withOuterRoot) roots.Add(outer);
        var repository = new Mock<IRootFolderRepository>(MockBehavior.Strict);
        repository.Setup(candidate => candidate.GetAllAsync()).ReturnsAsync(roots);
        var health = new Mock<IRootFolderStorageHealthResolver>(MockBehavior.Strict);
        if (unresolvedSource)
        {
            health.Setup(candidate => candidate.ResolveAsync(destinationRoot, It.IsAny<CancellationToken>()))
                .ReturnsAsync(StrongWritableObservation());
        }
        var resolver = new FilePublicationCapabilityResolver(repository.Object, health.Object);

        var plan = await resolver.ResolveAsync(
            FileAction.Move,
            Path.Join(unresolvedSource ? unresolvedPath : FileService.GetTempDirectory("unmanaged-source"), "source.m4b"),
            Path.Join(unresolvedSource ? destinationRoot.Path : unresolvedPath, "target.m4b"),
            ContentOnlyProof(),
            compatibilityBatchId: Guid.NewGuid(),
            cleanupOwner: CompatibilityCleanupOwner.Listenarr);

        Assert.Equal(unresolvedSource, plan.IsAllowed);
        Assert.Equal(unresolvedSource ? FilePublicationExecutionMode.AdditiveCopyRetainSource
            : FilePublicationExecutionMode.Blocked, plan.Mode);
        Assert.Equal(unresolvedSource ? FilePublicationSourceDisposition.Retained
            : FilePublicationSourceDisposition.Unchanged, plan.SourceDisposition);
        Assert.Null(plan.SourceRootFolderId);
        repository.VerifyAll();
        health.VerifyAll();
        health.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ResolveAsync_ValidMoreSpecificBoundary_OverridesUnavailableAncestor()
    {
        var root = BuildRoot();
        var ancestorPath = root.Path;
        root.Path = Path.Join(ancestorPath, "authorized");
        var ancestor = new RootFolder
        {
            Id = 92,
            Name = "Unavailable ancestor",
            Path = ancestorPath,
            CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
            PathIdentityState = PathIdentityState.Unavailable,
            ResolvedCaseSensitivity = FileSystemCaseSensitivity.Unknown
        };
        var repository = new Mock<IRootFolderRepository>(MockBehavior.Strict);
        repository.Setup(candidate => candidate.GetAllAsync()).ReturnsAsync([ancestor, root]);
        var health = new Mock<IRootFolderStorageHealthResolver>(MockBehavior.Strict);
        health.Setup(candidate => candidate.ResolveAsync(root, It.IsAny<CancellationToken>()))
            .ReturnsAsync(StrongWritableObservation());
        var resolver = new FilePublicationCapabilityResolver(repository.Object, health.Object);

        var plan = await resolver.ResolveAsync(FileAction.Move,
            Path.Join(FileService.GetTempDirectory("external-source"), "source.m4b"),
            Path.Join(root.Path, "target.m4b"), ContentOnlyProof());

        // The existing deepest-boundary contract lets an explicit valid nested
        // root authorize its own descendants without borrowing ancestor semantics.
        Assert.Equal(FilePublicationExecutionMode.Durable, plan.Mode);
        repository.VerifyAll();
        health.VerifyAll();
        health.VerifyNoOtherCalls();
    }

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
    public async Task ResolveAsync_VerifiedMoveCapability_DoesNotRequireWeakStoragePolicy()
    {
        var root = BuildRoot();
        root.WeakStorageSourceCleanupPolicy =
            WeakStorageSourceCleanupPolicy.RetainSource;
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
        Assert.Null(plan.DestinationPolicyRevision);
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

    [Fact]
    public async Task ResolveAsync_UnverifiableHardlink_SelectsCopyWithoutCleanupAuthority()
    {
        var root = BuildRoot();
        var repository = new Mock<IRootFolderRepository>();
        repository.Setup(service => service.GetAllAsync()).ReturnsAsync([root]);
        var health = new Mock<IRootFolderStorageHealthResolver>();
        health.Setup(service => service.ResolveAsync(root, It.IsAny<CancellationToken>()))
            .ReturnsAsync(StrongWritableObservation());
        var resolver = new FilePublicationCapabilityResolver(repository.Object, health.Object)
        {
            HardlinkIdentityProbe = _ => false
        };

        var plan = await resolver.ResolveAsync(FileAction.HardlinkCopy,
            Path.Join(root.Path, "source.mp3"), Path.Join(root.Path, "target.mp3"), ContentOnlyProof());

        Assert.Equal(FileAction.HardlinkCopy, plan.RequestedAction);
        Assert.Equal(FileAction.Copy, plan.EffectiveAction);
        Assert.Equal(FilePublicationExecutionMode.AdditiveCopyRetainSource, plan.Mode);
        Assert.Equal(FilePublicationSourceDisposition.Retained, plan.SourceDisposition);
        Assert.Equal("hardlink_identity_unavailable", plan.ReasonCode);
        Assert.Equal(CompatibilityCleanupOwner.None, plan.CleanupOwner);
    }

    [Theory]
    [InlineData(0xff534d42u, false)]
    [InlineData(0xfe534d42u, false)]
    [InlineData(0x65735546u, false)]
    [InlineData(0xef53u, true)]
    [InlineData(0x58465342u, true)]
    public void HardlinkIdentity_FileSystemClassification(uint type, bool supported)
    {
        Assert.Equal(supported, HardlinkIdentityCapability.SupportsFileSystemType(type));
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
            CanRetireSource: false,
            CanRetireAfterVerifiedCopy: true);

    private static FilePublicationSourceProof DurableProof() =>
        new(
            "durable:test",
            5,
            new string('A', 64));

    private static FilePublicationSourceProof ContentOnlyProof() =>
        new("content-only:" + new string('A', 64), 5, new string('A', 64),
            FilePublicationSourceAuthority.ContentOnly);

    private static RootFolderStorageObservation StrongWritableObservation() =>
        new(RootFolderStorageState.Healthy, RootFolderStorageReason.None, null,
            CanConfirmCurrentFolder: true, CanChangePath: true,
            CanMutateFilesystem: true, ConfirmationToken: null,
            CanPublishNewFiles: true, CanRetireSource: true,
            CanRetireAfterVerifiedCopy: true);
}
