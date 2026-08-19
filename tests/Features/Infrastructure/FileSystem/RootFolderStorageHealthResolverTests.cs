using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "RootFolderStorageHealthResolverTests")]
[Trait("Category", "Infrastructure")]
public sealed class RootFolderStorageHealthResolverTests : BaseTests
{
    [Fact]
    public async Task ResolveAsync_AuthorizedGenerationMatches_ReturnsHealthy()
    {
        var path = Path.GetFullPath("root-storage-healthy");
        var root = BuildRoot(path, identity: "authorized");
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveExistingAsync(
                path,
                ManagedDirectoryIdentity.CurrentVersion,
                "authorized",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion,
                "authorized",
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
    public async Task ResolveAsync_AuthorizedGenerationWithBehavioralAutoSemantics_DisablesMutation()
    {
        var path = Path.GetFullPath("root-storage-behavioral-auto-semantics");
        var root = BuildRoot(path, identity: "authorized");
        root.CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto;
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveExistingAsync(
                path,
                ManagedDirectoryIdentity.CurrentVersion,
                "authorized",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion,
                "authorized",
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

        Assert.Equal(RootFolderStorageState.Limited, result.State);
        Assert.Equal(RootFolderStorageReason.MutationSemanticsUnproven, result.Reason);
        Assert.True(result.CanReadFilesystem);
        Assert.True(result.CanScanFilesystem);
        Assert.False(result.CanMutateFilesystem);
        Assert.Contains(
            "Sensitive or Insensitive",
            result.Message ?? string.Empty,
            StringComparison.Ordinal);
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
            .Setup(resolver => resolver.ResolveExistingAsync(
                path,
                ManagedDirectoryIdentity.CurrentVersion,
                "authorized",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion,
                "authorized",
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
            .Setup(resolver => resolver.ResolveExistingAsync(
                path,
                ManagedDirectoryIdentity.CurrentVersion,
                "authorized",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion,
                "authorized",
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
            .Setup(resolver => resolver.ResolveExistingAsync(
                path,
                ManagedDirectoryIdentity.CurrentVersion,
                "authorized",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion,
                "authorized",
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
    public async Task ResolveAsync_AuthorizedGenerationMissing_ReturnsMissingWithoutConfirmation()
    {
        var path = Path.GetFullPath("root-storage-missing");
        var root = BuildRoot(path, identity: "authorized");
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveExistingAsync(
                path,
                ManagedDirectoryIdentity.CurrentVersion,
                "authorized",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DirectoryObjectIdentityResolution.Unavailable(
                "Directory not found.",
                DirectoryObjectIdentityFailureKind.Missing));
        var resolver = new RootFolderStorageHealthResolver(identityResolver.Object);

        var result = await resolver.ResolveAsync(root);

        Assert.Equal(RootFolderStorageState.Missing, result.State);
        Assert.Equal(RootFolderStorageReason.PathMissing, result.Reason);
        Assert.False(result.CanMutateFilesystem);
        Assert.False(result.CanConfirmCurrentFolder);
        Assert.Null(result.ConfirmationToken);
        identityResolver.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_AuthorizedGenerationReplaced_ReturnsChangedBoundToObservedGeneration()
    {
        var path = Path.GetFullPath("root-storage-changed");
        var root = BuildRoot(path, identity: "authorized");
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveExistingAsync(
                path,
                ManagedDirectoryIdentity.CurrentVersion,
                "authorized",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DirectoryObjectIdentityResolution.Unavailable(
                "Identity mismatch.",
                DirectoryObjectIdentityFailureKind.IdentityMismatch));
        identityResolver
            .Setup(resolver => resolver.ResolveAsync(path, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion,
                "replacement",
                null));
        var resolver = new RootFolderStorageHealthResolver(identityResolver.Object);

        var result = await resolver.ResolveAsync(root);

        Assert.Equal(RootFolderStorageState.Changed, result.State);
        Assert.Equal(RootFolderStorageReason.IdentityMismatch, result.Reason);
        Assert.False(result.CanMutateFilesystem);
        Assert.True(result.CanConfirmCurrentFolder);
        Assert.False(string.IsNullOrWhiteSpace(result.ConfirmationToken));
        identityResolver.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_LegacyWeakIdentity_AllowsScanAndExplicitIdentityUpgrade()
    {
        var path = Path.GetFullPath("root-storage-legacy-weak");
        var root = BuildRoot(path, identity: "legacy");
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveExistingAsync(
                path,
                ManagedDirectoryIdentity.CurrentVersion,
                "legacy",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DirectoryObjectIdentityResolution.Unavailable(
                "Legacy Linux identity requires upgrade.",
                DirectoryObjectIdentityFailureKind.LegacyWeakIdentity));
        identityResolver
            .Setup(resolver => resolver.ResolveAsync(
                path,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion,
                "strong-current",
                null));
        var resolver = new RootFolderStorageHealthResolver(identityResolver.Object);

        var result = await resolver.ResolveAsync(root);

        Assert.Equal(RootFolderStorageState.Limited, result.State);
        Assert.Equal(RootFolderStorageReason.IdentityUnsupported, result.Reason);
        Assert.True(result.CanReadFilesystem);
        Assert.True(result.CanScanFilesystem);
        Assert.False(result.CanMutateFilesystem);
        Assert.True(result.CanConfirmCurrentFolder);
        Assert.False(string.IsNullOrWhiteSpace(result.ConfirmationToken));
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
    public async Task ResolveAsync_NoAuthorizedGeneration_ReturnsUnconfirmed()
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
        var resolver = new RootFolderStorageHealthResolver(identityResolver.Object);

        var result = await resolver.ResolveAsync(root);

        Assert.Equal(RootFolderStorageState.Unconfirmed, result.State);
        Assert.Equal(RootFolderStorageReason.NoAuthorizedIdentity, result.Reason);
        Assert.False(result.CanMutateFilesystem);
        Assert.True(result.CanConfirmCurrentFolder);
        Assert.False(string.IsNullOrWhiteSpace(result.ConfirmationToken));
        identityResolver.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_ReplacementAlsoChangesFilesystemSemantics_RemainsChangedButCannotConfirm()
    {
        var path = Path.GetFullPath("root-storage-replacement-semantics-changed");
        var root = BuildRoot(path, identity: "authorized");
        root.CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto;
        var opposite = root.ResolvedCaseSensitivity == FileSystemCaseSensitivity.Sensitive
            ? FileSystemCaseSensitivity.Insensitive
            : FileSystemCaseSensitivity.Sensitive;
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveExistingAsync(
                path,
                ManagedDirectoryIdentity.CurrentVersion,
                "authorized",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DirectoryObjectIdentityResolution.Unavailable(
                "Identity mismatch.",
                DirectoryObjectIdentityFailureKind.IdentityMismatch));
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

        Assert.Equal(RootFolderStorageState.Changed, result.State);
        Assert.Equal(RootFolderStorageReason.FilesystemSemanticsChanged, result.Reason);
        Assert.False(result.CanMutateFilesystem);
        Assert.False(result.CanConfirmCurrentFolder);
        Assert.Null(result.ConfirmationToken);
        identityResolver.VerifyAll();
        semanticsResolver.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_PhysicalGenerationMatchesButFilesystemSemanticsChanged_RequiresPathRepair()
    {
        var path = Path.GetFullPath("root-storage-semantics-changed");
        var root = BuildRoot(path, identity: "authorized");
        root.CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto;
        var opposite = root.ResolvedCaseSensitivity == FileSystemCaseSensitivity.Sensitive
            ? FileSystemCaseSensitivity.Insensitive
            : FileSystemCaseSensitivity.Sensitive;
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveExistingAsync(
                path,
                ManagedDirectoryIdentity.CurrentVersion,
                "authorized",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion,
                "authorized",
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
    public async Task ResolveAsync_IdentityUnsupported_PreservesTechnicalFailureDetail()
    {
        var path = Path.GetFullPath("root-storage-identity-unsupported");
        var root = BuildRoot(path, identity: "authorized");
        const string detail =
            "statx omitted birth time and name_to_handle_at returned operation not permitted.";
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveExistingAsync(
                path,
                ManagedDirectoryIdentity.CurrentVersion,
                "authorized",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DirectoryObjectIdentityResolution.Unavailable(
                detail,
                DirectoryObjectIdentityFailureKind.IdentityUnsupported));
        identityResolver
            .Setup(resolver => resolver.ResolveAsync(
                path,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DirectoryObjectIdentityResolution.Unavailable(
                detail,
                DirectoryObjectIdentityFailureKind.IdentityUnsupported));
        var resolver = new RootFolderStorageHealthResolver(identityResolver.Object);

        var result = await resolver.ResolveAsync(root);

        Assert.Equal(RootFolderStorageState.Limited, result.State);
        Assert.Equal(RootFolderStorageReason.IdentityUnsupported, result.Reason);
        Assert.Equal(detail, result.Detail);
        Assert.Contains(
            "read and scanned",
            result.Message ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(result.CanReadFilesystem);
        Assert.True(result.CanScanFilesystem);
        Assert.False(result.CanMutateFilesystem);
        identityResolver.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_UnsupportedPersistedIdentityWithCurrentStrongIdentity_RequiresConfirmationBeforeScan()
    {
        var path = Path.GetFullPath("root-storage-unsupported-persisted-identity");
        var root = BuildRoot(path, identity: "legacy-version");
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveExistingAsync(
                path,
                ManagedDirectoryIdentity.CurrentVersion,
                "legacy-version",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DirectoryObjectIdentityResolution.Unavailable(
                "Directory identity version is unsupported.",
                DirectoryObjectIdentityFailureKind.IdentityUnsupported));
        identityResolver
            .Setup(resolver => resolver.ResolveAsync(
                path,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion,
                "current-strong-identity",
                null));
        var resolver = new RootFolderStorageHealthResolver(identityResolver.Object);

        var result = await resolver.ResolveAsync(root);

        Assert.Equal(RootFolderStorageState.Unconfirmed, result.State);
        Assert.Equal(RootFolderStorageReason.IdentityUnsupported, result.Reason);
        Assert.False(result.CanReadFilesystem);
        Assert.False(result.CanScanFilesystem);
        Assert.False(result.CanMutateFilesystem);
        Assert.True(result.CanConfirmCurrentFolder);
        Assert.False(string.IsNullOrWhiteSpace(result.ConfirmationToken));
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
