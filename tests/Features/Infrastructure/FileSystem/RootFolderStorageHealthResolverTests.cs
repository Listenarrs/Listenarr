using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "RootFolderStorageHealthResolverTests")]
[Trait("Category", "Infrastructure")]
public sealed class RootFolderStorageHealthResolverTests : BaseTests
{
    [Theory]
    [InlineData(DirectoryObjectIdentityFailureKind.Unknown)]
    [InlineData(DirectoryObjectIdentityFailureKind.IdentityUnstable)]
    [InlineData(DirectoryObjectIdentityFailureKind.AccessDenied)]
    public async Task ResolveAsync_CurrentAccessibleRoot_OptionalDiagnosticFailureDoesNotDisableCapabilities(
        DirectoryObjectIdentityFailureKind failureKind)
    {
        var path = FileService.GetTempDirectory("root-storage-optional-diagnostic");
        var root = BuildRoot(path, "legacy-diagnostic");
        var diagnostic = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        diagnostic.Setup(candidate => candidate.ResolveAsync(
                path, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DirectoryObjectIdentityResolution.Unavailable(
                "The optional identity observation failed.", failureKind));
        var resolver = new RootFolderStorageHealthResolver(
            diagnostic.Object, readOnlyFileSystemProbe: _ => false);

        var result = await resolver.ResolveAsync(root);

        Assert.True(result.CanReadFilesystem);
        Assert.True(result.CanScanFilesystem);
        Assert.True(result.CanPublishAdditively);
        Assert.True(result.CanMutateFilesystem);
        Assert.False(result.CanConfirmCurrentFolder);
        Assert.Equal("legacy-diagnostic", root.DirectoryObjectIdentity);
        diagnostic.VerifyAll();
    }


    [LinuxFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task ResolveAsync_OptionalDiagnosticFailure_CurrentAccessDeniedRemainsUnavailable()
    {
        var path = FileService.GetTempDirectory("root-storage-real-access-denial");
        var root = BuildRoot(path, "legacy-diagnostic");
        var diagnostic = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        diagnostic.Setup(candidate => candidate.ResolveAsync(
                path, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DirectoryObjectIdentityResolution.Unavailable(
                "The optional diagnostic failed.", DirectoryObjectIdentityFailureKind.Unknown));
        var resolver = new RootFolderStorageHealthResolver(
            diagnostic.Object, readOnlyFileSystemProbe: _ => false);
        var originalMode = File.GetUnixFileMode(path);
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.None);

            var result = await resolver.ResolveAsync(root);

            Assert.False(result.CanReadFilesystem);
            Assert.False(result.CanMutateFilesystem);
            Assert.False(result.CanConfirmCurrentFolder);
            Assert.Equal(RootFolderStorageReason.AccessDenied, result.Reason);
        }
        finally
        {
            File.SetUnixFileMode(path, originalMode);
        }
    }

    [Fact]
    public async Task ResolveAsync_CurrentPathAvailable_ReturnsHealthy()
    {
        var path = Path.GetFullPath("root-storage-healthy");
        var root = BuildRoot(path, identity: "authorized");
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveAsync(
                path,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion,
                "current-observation",
                null));
        var resolver = new RootFolderStorageHealthResolver(
            identityResolver.Object,
            readOnlyFileSystemProbe: _ => false);

        var result = await resolver.ResolveAsync(root);

        Assert.Equal(RootFolderStorageState.Healthy, result.State);
        Assert.Equal(RootFolderStorageReason.None, result.Reason);
        Assert.True(result.CanMutateFilesystem);
        Assert.False(result.CanConfirmCurrentFolder);
        Assert.Null(result.ConfirmationToken);
        identityResolver.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_PersistedPhysicalIdentityMismatch_DoesNotChangeWritableRootAvailability()
    {
        var path = Path.GetFullPath("root-storage-remounted-device");
        var root = BuildRoot(path, identity: "persisted-device-a");
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveAsync(path, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion,
                "current-device-b",
                null));
        var semanticsResolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        semanticsResolver
            .Setup(resolver => resolver.ResolveAsync(
                path,
                root.CaseSensitivityMode,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileSystemSemanticsResolution(
                new FileSystemPathSemantics(
                    FileSystemPathSemantics.CurrentHostDefault.Syntax,
                    root.ResolvedCaseSensitivity),
                PathIdentityState.Valid,
                path));
        var resolver = new RootFolderStorageHealthResolver(
            identityResolver.Object,
            semanticsResolver.Object,
            readOnlyFileSystemProbe: _ => false);

        var result = await resolver.ResolveAsync(root);

        Assert.Equal(RootFolderStorageState.Healthy, result.State);
        Assert.Equal(RootFolderStorageReason.None, result.Reason);
        Assert.True(result.CanReadFilesystem);
        Assert.True(result.CanScanFilesystem);
        Assert.True(result.CanMutateFilesystem);
        Assert.False(result.CanConfirmCurrentFolder);
        Assert.Null(result.ConfirmationToken);
        identityResolver.VerifyAll();
        semanticsResolver.VerifyAll();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolveAsync_CephFsReporterDeviceChange_KeepsRootAvailable(bool returnToOriginalNode)
    {
        // Issue #947 reports inode 1099512613376 on mounts 0:278 and 0:687.
        // Use identical synthetic file-handle evidence to isolate the device change.
        var nodeA = PinnedDirectoryCreation.CreateLinuxObjectIdentityCandidatesFromEvidence(
            deviceMajor: 0, deviceMinor: 278, inode: 1099512613376,
            hasBirthTime: false, birthTimeSeconds: 0, birthTimeNanoseconds: 0,
            generationIdentities: ["fh:00000001:01020304"]);
        var nodeB = PinnedDirectoryCreation.CreateLinuxObjectIdentityCandidatesFromEvidence(
            deviceMajor: 0, deviceMinor: 687, inode: 1099512613376,
            hasBirthTime: false, birthTimeSeconds: 0, birthTimeNanoseconds: 0,
            generationIdentities: ["fh:00000001:01020304"]);
        var stored = ManagedDirectoryIdentity.CreateMarkerless(
            Assert.Single(returnToOriginalNode ? nodeB : nodeA));
        var current = ManagedDirectoryIdentity.CreateMarkerless(
            Assert.Single(returnToOriginalNode ? nodeA : nodeB));
        Assert.NotEqual(stored, current);
        var path = Path.GetFullPath("cephfs-storage/books");
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSemantics.CurrentHostDefault.Syntax,
            FileSystemCaseSensitivity.Sensitive);
        var root = BuildRoot(path, stored);
        root.CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive;
        root.ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive;
        root.PathIdentityKey = FileSystemPathIdentity.CreateKey("root", path, semantics);
        var storedPathKey = root.PathIdentityKey;
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver.Setup(resolver => resolver.ResolveAsync(path, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion, current, null));
        var semanticsResolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        semanticsResolver.Setup(resolver => resolver.ResolveAsync(
                path, root.CaseSensitivityMode, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileSystemSemanticsResolution(semantics, PathIdentityState.Valid, path));
        var resolver = new RootFolderStorageHealthResolver(
            identityResolver.Object, semanticsResolver.Object, readOnlyFileSystemProbe: _ => false);

        var result = await resolver.ResolveAsync(root);

        Assert.Equal(RootFolderStorageState.Healthy, result.State);
        Assert.Equal(RootFolderStorageReason.None, result.Reason);
        Assert.True(result.CanReadFilesystem);
        Assert.True(result.CanScanFilesystem);
        Assert.True(result.CanPublishAdditively);
        Assert.True(result.CanRetireVerifiedSource);
        Assert.True(result.CanMutateFilesystem);
        Assert.False(result.CanConfirmCurrentFolder);
        Assert.Null(result.ConfirmationToken);
        Assert.Equal(storedPathKey, root.PathIdentityKey);
        Assert.Equal(stored, root.DirectoryObjectIdentity);
        identityResolver.VerifyAll();
        semanticsResolver.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_NoPersistedPhysicalIdentity_WithWritableCurrentCapabilities_IsHealthy()
    {
        var path = Path.GetFullPath("root-storage-no-persisted-object-identity");
        var root = BuildRoot(path, identity: null);
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveAsync(path, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion,
                "current-observation",
                null));
        var semanticsResolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        semanticsResolver
            .Setup(resolver => resolver.ResolveAsync(
                path,
                root.CaseSensitivityMode,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileSystemSemanticsResolution(
                new FileSystemPathSemantics(
                    FileSystemPathSemantics.CurrentHostDefault.Syntax,
                    root.ResolvedCaseSensitivity),
                PathIdentityState.Valid,
                path));
        var resolver = new RootFolderStorageHealthResolver(
            identityResolver.Object,
            semanticsResolver.Object,
            readOnlyFileSystemProbe: _ => false);

        var result = await resolver.ResolveAsync(root);

        Assert.Equal(RootFolderStorageState.Healthy, result.State);
        Assert.Equal(RootFolderStorageReason.None, result.Reason);
        Assert.True(result.CanMutateFilesystem);
        Assert.False(result.CanConfirmCurrentFolder);
        Assert.Null(result.ConfirmationToken);
        identityResolver.VerifyAll();
        semanticsResolver.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_AuthorizedGenerationWithBehavioralAutoSemantics_AllowsGuardedMutation()
    {
        var path = Path.GetFullPath("root-storage-behavioral-auto-semantics");
        var root = BuildRoot(path, identity: "authorized");
        root.CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto;
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveAsync(
                path,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion,
                "current-observation",
                null));
        var semanticsResolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        semanticsResolver
            .Setup(resolver => resolver.ResolveAsync(
                path,
                FileSystemCaseSensitivityMode.Auto,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileSystemSemanticsResolution(
                new FileSystemPathSemantics(
                    FileSystemPathSemantics.CurrentHostDefault.Syntax,
                    root.ResolvedCaseSensitivity),
                PathIdentityState.Valid,
                path,
                EvidenceKind: FileSystemSemanticsEvidenceKind.BehavioralObservation));
        var resolver = new RootFolderStorageHealthResolver(
            identityResolver.Object,
            semanticsResolver.Object,
            readOnlyFileSystemProbe: _ => false);

        var result = await resolver.ResolveAsync(root);

        Assert.Equal(RootFolderStorageState.Healthy, result.State);
        Assert.Equal(RootFolderStorageReason.None, result.Reason);
        Assert.True(result.CanReadFilesystem);
        Assert.True(result.CanScanFilesystem);
        Assert.True(result.CanMutateFilesystem);
        Assert.True(result.CanPublishAdditively);
        Assert.True(result.CanRetireSourceNow);
        Assert.True(result.CanRetireVerifiedSource);
        Assert.Null(result.ConfirmationToken);
        identityResolver.VerifyAll();
        semanticsResolver.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_BehavioralAutoSemanticsOnReadOnlyMount_PrefersReadOnlyReason()
    {
        var path = Path.GetFullPath("root-storage-behavioral-auto-read-only");
        var root = BuildRoot(path, identity: "authorized");
        root.CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto;
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveAsync(
                path,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion,
                "current-observation",
                null));
        var semanticsResolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        semanticsResolver
            .Setup(resolver => resolver.ResolveAsync(
                path,
                FileSystemCaseSensitivityMode.Auto,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileSystemSemanticsResolution(
                new FileSystemPathSemantics(
                    FileSystemPathSemantics.CurrentHostDefault.Syntax,
                    root.ResolvedCaseSensitivity),
                PathIdentityState.Valid,
                path,
                EvidenceKind: FileSystemSemanticsEvidenceKind.BehavioralObservation));
        var resolver = new RootFolderStorageHealthResolver(
            identityResolver.Object,
            semanticsResolver.Object,
            readOnlyFileSystemProbe: _ => true);

        var result = await resolver.ResolveAsync(root);

        Assert.Equal(RootFolderStorageState.Limited, result.State);
        Assert.Equal(RootFolderStorageReason.ReadOnlyFilesystem, result.Reason);
        Assert.False(result.CanMutateFilesystem);
        identityResolver.VerifyAll();
        semanticsResolver.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_AuthorizedGenerationOnReadOnlyMount_ReturnsLimitedScanOnlyCapability()
    {
        var path = Path.GetFullPath("root-storage-read-only");
        var root = BuildRoot(path, identity: "authorized");
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveAsync(
                path,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion,
                "current-observation",
                null));
        var resolver = new RootFolderStorageHealthResolver(
            identityResolver.Object,
            readOnlyFileSystemProbe: _ => true);

        var result = await resolver.ResolveAsync(root);

        Assert.Equal(RootFolderStorageState.Limited, result.State);
        Assert.Equal(RootFolderStorageReason.ReadOnlyFilesystem, result.Reason);
        Assert.True(result.CanReadFilesystem);
        Assert.True(result.CanScanFilesystem);
        Assert.False(result.CanMutateFilesystem);
        Assert.False(result.CanPublishAdditively);
        Assert.False(result.CanRetireSourceNow);
        Assert.False(result.CanRetireVerifiedSource);
        Assert.Contains("read-only", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        identityResolver.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_AuthorizedGenerationWhenMountAccessUnknown_FailsMutationClosed()
    {
        var path = Path.GetFullPath("root-storage-mount-unknown");
        var root = BuildRoot(path, identity: "authorized");
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveAsync(
                path,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion,
                "current-observation",
                null));
        var resolver = new RootFolderStorageHealthResolver(
            identityResolver.Object,
            readOnlyFileSystemProbe: _ => null);

        var result = await resolver.ResolveAsync(root);

        Assert.Equal(RootFolderStorageState.Limited, result.State);
        Assert.Equal(RootFolderStorageReason.MutationCapabilityUnavailable, result.Reason);
        Assert.True(result.CanReadFilesystem);
        Assert.True(result.CanScanFilesystem);
        Assert.False(result.CanMutateFilesystem);
        identityResolver.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_CurrentPathMissing_ReturnsMissingWithoutConfirmation()
    {
        var path = Path.GetFullPath("root-storage-missing");
        var root = BuildRoot(path, identity: "authorized");
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveAsync(
                path,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DirectoryObjectIdentityResolution.Unavailable(
                "Directory not found.",
                DirectoryObjectIdentityFailureKind.Missing));
        var resolver = new RootFolderStorageHealthResolver(
            identityResolver.Object,
            readOnlyFileSystemProbe: _ => false);

        var result = await resolver.ResolveAsync(root);

        Assert.Equal(RootFolderStorageState.Missing, result.State);
        Assert.Equal(RootFolderStorageReason.PathMissing, result.Reason);
        Assert.False(result.CanMutateFilesystem);
        Assert.False(result.CanConfirmCurrentFolder);
        Assert.Null(result.ConfirmationToken);
        identityResolver.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_CurrentPhysicalObservationChanges_DoesNotRequestConfirmation()
    {
        var path = Path.GetFullPath("root-storage-changed");
        var root = BuildRoot(path, identity: "authorized");
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveAsync(path, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion,
                "replacement",
                null));
        var resolver = new RootFolderStorageHealthResolver(
            identityResolver.Object,
            readOnlyFileSystemProbe: _ => false);

        var result = await resolver.ResolveAsync(root);

        Assert.Equal(RootFolderStorageState.Healthy, result.State);
        Assert.Equal(RootFolderStorageReason.None, result.Reason);
        Assert.True(result.CanMutateFilesystem);
        Assert.False(result.CanConfirmCurrentFolder);
        Assert.Null(result.ConfirmationToken);
        identityResolver.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_LegacyPersistedIdentity_DoesNotLimitCurrentCapabilities()
    {
        var path = Path.GetFullPath("root-storage-legacy-weak");
        var root = BuildRoot(path, identity: "legacy");
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveAsync(
                path,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion,
                "strong-current",
                null));
        var resolver = new RootFolderStorageHealthResolver(
            identityResolver.Object,
            readOnlyFileSystemProbe: _ => false);

        var result = await resolver.ResolveAsync(root);

        Assert.Equal(RootFolderStorageState.Healthy, result.State);
        Assert.Equal(RootFolderStorageReason.None, result.Reason);
        Assert.True(result.CanReadFilesystem);
        Assert.True(result.CanScanFilesystem);
        Assert.True(result.CanMutateFilesystem);
        Assert.True(result.CanPublishAdditively);
        Assert.True(result.CanRetireVerifiedSource);
        Assert.False(result.CanConfirmCurrentFolder);
        Assert.Null(result.ConfirmationToken);
        identityResolver.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_CurrentIdentityUnsupported_DoesNotLimitWritableStorage()
    {
        var path = Path.GetFullPath("root-storage-legacy-weak-unsupported-current");
        var root = BuildRoot(path, identity: "legacy");
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        const string detail =
            "The filesystem does not expose a durable file handle or inode generation for this object.";
        identityResolver
            .Setup(resolver => resolver.ResolveAsync(
                path,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DirectoryObjectIdentityResolution.Unavailable(
                detail,
                DirectoryObjectIdentityFailureKind.IdentityUnsupported));
        var resolver = new RootFolderStorageHealthResolver(
            identityResolver.Object,
            readOnlyFileSystemProbe: _ => false);

        var result = await resolver.ResolveAsync(root);

        Assert.Equal(RootFolderStorageState.Healthy, result.State);
        Assert.Equal(RootFolderStorageReason.None, result.Reason);
        Assert.Equal(detail, result.Detail);
        Assert.True(result.CanReadFilesystem);
        Assert.True(result.CanScanFilesystem);
        Assert.True(result.CanMutateFilesystem);
        Assert.False(result.CanConfirmCurrentFolder);
        Assert.Null(result.ConfirmationToken);
        identityResolver.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_LegacyRootWithoutPersistedSemantics_ReturnsUnconfirmed()
    {
        var path = Path.GetFullPath("root-storage-legacy-unconfirmed");
        var root = new RootFolder
        {
            Id = 42,
            Name = "Default",
            Path = path,
            CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
            ResolvedCaseSensitivity = FileSystemCaseSensitivity.Unknown,
            PathIdentityState = PathIdentityState.Unavailable
        };
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveAsync(path, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion,
                "observed",
                null));
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        var semanticsResolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        semanticsResolver
            .Setup(resolver => resolver.ResolveAsync(
                path,
                FileSystemCaseSensitivityMode.Auto,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileSystemSemanticsResolution(
                semantics,
                PathIdentityState.Valid,
                path));
        var resolver = new RootFolderStorageHealthResolver(
            identityResolver.Object,
            semanticsResolver.Object);

        var result = await resolver.ResolveAsync(root);

        Assert.Equal(RootFolderStorageState.Unconfirmed, result.State);
        Assert.Equal(RootFolderStorageReason.NoAuthorizedIdentity, result.Reason);
        Assert.True(result.CanConfirmCurrentFolder);
        Assert.False(result.CanMutateFilesystem);
        Assert.False(string.IsNullOrWhiteSpace(result.ConfirmationToken));
        identityResolver.VerifyAll();
        semanticsResolver.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_NoPersistedPhysicalGeneration_WithPersistedPathSemantics_ReturnsHealthy()
    {
        var path = Path.GetFullPath("root-storage-unconfirmed");
        var root = BuildRoot(path, identity: null);
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveAsync(path, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion,
                "observed",
                null));
        var resolver = new RootFolderStorageHealthResolver(
            identityResolver.Object,
            readOnlyFileSystemProbe: _ => false);

        var result = await resolver.ResolveAsync(root);

        Assert.Equal(RootFolderStorageState.Healthy, result.State);
        Assert.Equal(RootFolderStorageReason.None, result.Reason);
        Assert.True(result.CanMutateFilesystem);
        Assert.False(result.CanConfirmCurrentFolder);
        Assert.Null(result.ConfirmationToken);
        identityResolver.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_PhysicalObservationChangesAndFilesystemSemanticsChange_FailsForSemantics()
    {
        var path = Path.GetFullPath("root-storage-replacement-semantics-changed");
        var root = BuildRoot(path, identity: "authorized");
        root.CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto;
        var opposite = root.ResolvedCaseSensitivity == FileSystemCaseSensitivity.Sensitive
            ? FileSystemCaseSensitivity.Insensitive
            : FileSystemCaseSensitivity.Sensitive;
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveAsync(path, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion,
                "replacement",
                null));
        var semanticsResolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        semanticsResolver
            .Setup(resolver => resolver.ResolveAsync(
                path,
                FileSystemCaseSensitivityMode.Auto,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileSystemSemanticsResolution(
                new FileSystemPathSemantics(
                    FileSystemPathSemantics.CurrentHostDefault.Syntax,
                    opposite),
                PathIdentityState.Valid,
                path));
        var resolver = new RootFolderStorageHealthResolver(
            identityResolver.Object,
            semanticsResolver.Object);

        var result = await resolver.ResolveAsync(root);

        Assert.Equal(RootFolderStorageState.Unavailable, result.State);
        Assert.Equal(RootFolderStorageReason.FilesystemSemanticsChanged, result.Reason);
        Assert.False(result.CanMutateFilesystem);
        Assert.False(result.CanConfirmCurrentFolder);
        Assert.Null(result.ConfirmationToken);
        identityResolver.VerifyAll();
        semanticsResolver.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_CurrentPathAvailableButFilesystemSemanticsChanged_RequiresPathRepair()
    {
        var path = Path.GetFullPath("root-storage-semantics-changed");
        var root = BuildRoot(path, identity: "authorized");
        root.CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto;
        var opposite = root.ResolvedCaseSensitivity == FileSystemCaseSensitivity.Sensitive
            ? FileSystemCaseSensitivity.Insensitive
            : FileSystemCaseSensitivity.Sensitive;
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveAsync(
                path,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion,
                "current-observation",
                null));
        var semanticsResolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        semanticsResolver
            .Setup(resolver => resolver.ResolveAsync(
                path,
                FileSystemCaseSensitivityMode.Auto,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileSystemSemanticsResolution(
                new FileSystemPathSemantics(
                    FileSystemPathSemantics.CurrentHostDefault.Syntax,
                    opposite),
                PathIdentityState.Valid,
                path));
        var resolver = new RootFolderStorageHealthResolver(
            identityResolver.Object,
            semanticsResolver.Object);

        var result = await resolver.ResolveAsync(root);

        Assert.Equal(RootFolderStorageState.Unavailable, result.State);
        Assert.Equal(RootFolderStorageReason.FilesystemSemanticsChanged, result.Reason);
        Assert.False(result.CanMutateFilesystem);
        Assert.False(result.CanConfirmCurrentFolder);
        Assert.Null(result.ConfirmationToken);
        identityResolver.VerifyAll();
        semanticsResolver.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_IdentityUnsupported_IsDiagnosticOnlyForWritableStorage()
    {
        var path = Path.GetFullPath("root-storage-identity-unsupported");
        var root = BuildRoot(path, identity: "authorized");
        const string detail =
            "statx omitted birth time and name_to_handle_at returned operation not permitted.";
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveAsync(
                path,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DirectoryObjectIdentityResolution.Unavailable(
                detail,
                DirectoryObjectIdentityFailureKind.IdentityUnsupported));
        var resolver = new RootFolderStorageHealthResolver(
            identityResolver.Object,
            readOnlyFileSystemProbe: _ => false);

        var result = await resolver.ResolveAsync(root);

        Assert.Equal(RootFolderStorageState.Healthy, result.State);
        Assert.Equal(RootFolderStorageReason.None, result.Reason);
        Assert.Equal(detail, result.Detail);
        Assert.True(result.CanReadFilesystem);
        Assert.True(result.CanScanFilesystem);
        Assert.True(result.CanPublishNewFiles);
        Assert.True(result.CanMutateFilesystem);
        Assert.True(result.CanPublishAdditively);
        Assert.True(result.CanRetireSourceNow);
        Assert.True(result.CanRetireVerifiedSource);
        identityResolver.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_UnsupportedPersistedIdentity_DoesNotRequireConfirmation()
    {
        var path = Path.GetFullPath("root-storage-unsupported-persisted-identity");
        var root = BuildRoot(path, identity: "legacy-version");
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveAsync(
                path,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion,
                "current-strong-identity",
                null));
        var resolver = new RootFolderStorageHealthResolver(
            identityResolver.Object,
            readOnlyFileSystemProbe: _ => false);

        var result = await resolver.ResolveAsync(root);

        Assert.Equal(RootFolderStorageState.Healthy, result.State);
        Assert.Equal(RootFolderStorageReason.None, result.Reason);
        Assert.True(result.CanReadFilesystem);
        Assert.True(result.CanScanFilesystem);
        Assert.True(result.CanMutateFilesystem);
        Assert.True(result.CanPublishAdditively);
        Assert.True(result.CanRetireVerifiedSource);
        Assert.False(result.CanConfirmCurrentFolder);
        Assert.Null(result.ConfirmationToken);
        identityResolver.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_ForeignHostPath_ReturnsUnavailableWithoutFilesystemProbe()
    {
        var path = OperatingSystem.IsWindows()
            ? "/server/mnt/audiobooks"
            : "C:\\server\\audiobooks";
        var root = BuildRoot(path, identity: "authorized");
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        var resolver = new RootFolderStorageHealthResolver(identityResolver.Object);

        var result = await resolver.ResolveAsync(root);

        Assert.Equal(RootFolderStorageState.Unavailable, result.State);
        Assert.Equal(RootFolderStorageReason.ForeignPathSyntax, result.Reason);
        Assert.False(result.CanMutateFilesystem);
        Assert.False(result.CanConfirmCurrentFolder);
        identityResolver.VerifyNoOtherCalls();
    }

    private static RootFolder BuildRoot(string path, string? identity)
    {
        var hostSemantics = FileSystemPathSemantics.CurrentHostDefault;
        var syntax = FileSystemPathIdentity.TryDetectAbsoluteSyntax(path, out var detectedSyntax)
            ? detectedSyntax
            : hostSemantics.Syntax;
        var sensitivity = syntax == hostSemantics.Syntax
            ? hostSemantics.CaseSensitivity
            : syntax == FileSystemPathSyntax.Windows
                ? FileSystemCaseSensitivity.Insensitive
                : FileSystemCaseSensitivity.Sensitive;
        var semantics = new FileSystemPathSemantics(syntax, sensitivity);
        return new RootFolder
        {
            Id = 42,
            Name = "Default",
            Path = path,
            CaseSensitivityMode = sensitivity == FileSystemCaseSensitivity.Sensitive
                ? FileSystemCaseSensitivityMode.Sensitive
                : FileSystemCaseSensitivityMode.Insensitive,
            ResolvedCaseSensitivity = sensitivity,
            PathIdentityState = PathIdentityState.Valid,
            PathIdentityKey = FileSystemPathIdentity.CreateKey("root", path, semantics),
            DirectoryObjectIdentityVersion = identity == null
                ? null
                : ManagedDirectoryIdentity.CurrentVersion,
            DirectoryObjectIdentity = identity
        };
    }
}
