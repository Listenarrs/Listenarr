using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Audiobooks.RootFolders;

[Trait("Name", "LibraryDestinationMutationGuardTests")]
[Trait("Category", "Application")]
public sealed class LibraryDestinationMutationGuardTests : BaseTests
{
    [Fact]
    public async Task GetBlockingReasonAsync_UsesConfiguredRootSemanticsAndBlocksActiveRelocation()
    {
        var rootPath = Path.GetFullPath(Path.Join(Path.GetTempPath(), $"guard-root-{Guid.NewGuid():N}"));
        var destinationPath = Path.Join(rootPath, "Author", "Title");
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSemantics.CurrentHostDefault.Syntax,
            FileSystemCaseSensitivity.Sensitive);
        var rootFolderService = new Mock<IRootFolderService>(MockBehavior.Strict);
        rootFolderService.Setup(service => service.GetAllAsync())
            .ReturnsAsync([
                new RootFolder
                {
                    Id = 1,
                    Name = "Library",
                    Path = rootPath,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive
                }
            ]);
        var resolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        resolver.Setup(service => service.ResolveAsync(
                rootPath,
                FileSystemCaseSensitivityMode.Sensitive,
                It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<FileSystemSemanticsResolution>(
                new FileSystemSemanticsResolution(
                    semantics,
                    PathIdentityState.Valid,
                    rootPath)));
        var relocationService = new Mock<IRootFolderRelocationService>(MockBehavior.Strict);
        relocationService.Setup(service => service.IsBoundaryProtectedAsync(
                destinationPath,
                semantics,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var guard = new LibraryDestinationMutationGuard(
            rootFolderService.Object,
            relocationService.Object,
            resolver.Object,
            Mock.Of<IAudiobookRepository>());

        var reason = await guard.GetBlockingReasonAsync(destinationPath);

        Assert.Equal("Destination overlaps an active root folder relocation.", reason);
        resolver.Verify(service => service.ResolveAsync(
            destinationPath,
            FileSystemCaseSensitivityMode.Auto,
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetBlockingReasonAsync_ExistingAudiobookOwnsSameDestination_BlocksIdentifierlessDuplicate()
    {
        var rootPath = Path.GetFullPath(Path.Join(Path.GetTempPath(), $"guard-duplicate-root-{Guid.NewGuid():N}"));
        var destinationPath = Path.Join(rootPath, "Author", "Title");
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        var rootFolderService = new Mock<IRootFolderService>(MockBehavior.Strict);
        rootFolderService.Setup(service => service.GetAllAsync())
            .ReturnsAsync([
                new RootFolder
                {
                    Id = 1,
                    Name = "Library",
                    Path = rootPath,
                    CaseSensitivityMode = semantics.CaseSensitivity == FileSystemCaseSensitivity.Sensitive
                        ? FileSystemCaseSensitivityMode.Sensitive
                        : FileSystemCaseSensitivityMode.Insensitive
                }
            ]);
        var resolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        resolver.Setup(service => service.ResolveAsync(
                rootPath,
                It.IsAny<FileSystemCaseSensitivityMode>(),
                It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<FileSystemSemanticsResolution>(
                new FileSystemSemanticsResolution(
                    semantics,
                    PathIdentityState.Valid,
                    rootPath)));
        var relocationService = new Mock<IRootFolderRelocationService>(MockBehavior.Strict);
        relocationService.Setup(service => service.IsBoundaryProtectedAsync(
                destinationPath,
                semantics,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        repository.Setup(service => service.GetOtherPathReferenceSnapshotsAsync(
                0,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new AudiobookPathReferenceSnapshot(17, destinationPath, null)
            ]);
        var guard = new LibraryDestinationMutationGuard(
            rootFolderService.Object,
            relocationService.Object,
            resolver.Object,
            repository.Object);

        var reason = await guard.GetBlockingReasonAsync(destinationPath);

        Assert.Equal(
            "Destination is already assigned to another audiobook in the library.",
            reason);
        repository.VerifyAll();
    }

    [WindowsFact]
    public async Task GetBlockingReasonAsync_DeviceAliasExistingAudiobook_BlocksSameDestination()
    {
        var rootPath = Path.GetFullPath(Path.Join(
            Path.GetTempPath(),
            $"guard-device-duplicate-root-{Guid.NewGuid():N}"));
        var destinationPath = Path.Join(rootPath, "Author", "Title");
        var deviceAliasDestination = @"\\?\" + destinationPath;
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive);
        var rootFolderService = new Mock<IRootFolderService>(MockBehavior.Strict);
        rootFolderService.Setup(service => service.GetAllAsync())
            .ReturnsAsync([
                new RootFolder
                {
                    Id = 1,
                    Name = "Library",
                    Path = rootPath,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive,
                    ResolvedCaseSensitivity = FileSystemCaseSensitivity.Insensitive,
                    PathIdentityState = PathIdentityState.Valid
                }
            ]);
        var resolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        resolver.Setup(service => service.ResolveAsync(
                rootPath,
                It.IsAny<FileSystemCaseSensitivityMode>(),
                It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<FileSystemSemanticsResolution>(
                new FileSystemSemanticsResolution(
                    semantics,
                    PathIdentityState.Valid,
                    rootPath)));
        var relocationService = new Mock<IRootFolderRelocationService>(MockBehavior.Strict);
        relocationService.Setup(service => service.IsBoundaryProtectedAsync(
                destinationPath,
                semantics,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        repository.Setup(service => service.GetOtherPathReferenceSnapshotsAsync(
                0,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new AudiobookPathReferenceSnapshot(17, deviceAliasDestination, null)
            ]);
        var guard = new LibraryDestinationMutationGuard(
            rootFolderService.Object,
            relocationService.Object,
            resolver.Object,
            repository.Object);

        var reason = await guard.GetBlockingReasonAsync(destinationPath);

        Assert.Equal(
            "Destination is already assigned to another audiobook in the library.",
            reason);
    }

    [Fact]
    public async Task GetBlockingReasonAsync_AmbiguousConfiguredRoot_DoesNotBorrowConflictingAutoSemantics()
    {
        var syntax = FileSystemPathSemantics.CurrentHostDefault.Syntax;
        var ambiguousRoot = syntax == FileSystemPathSyntax.Windows
            ? "//server/share"
            : "//mnt/library";
        var canonicalRoot = syntax == FileSystemPathSyntax.Windows
            ? "\\\\server\\share"
            : "/mnt/library";
        var destinationPath = syntax == FileSystemPathSyntax.Windows
            ? "\\\\server\\share\\Author\\Title"
            : "/mnt/library/Author/Title";
        var existingAlias = syntax == FileSystemPathSyntax.Windows
            ? "\\\\server\\share\\author\\title"
            : "/mnt/library/author/title";
        Assert.False(FileSystemPathIdentity.TryDetectAbsoluteSyntax(
            ambiguousRoot,
            out _));
        var configuredSemantics = new FileSystemPathSemantics(
            syntax,
            FileSystemCaseSensitivity.Insensitive);
        var autoSemantics = new FileSystemPathSemantics(
            syntax,
            FileSystemCaseSensitivity.Sensitive);
        var rootFolderService = new Mock<IRootFolderService>(MockBehavior.Strict);
        rootFolderService.Setup(service => service.GetAllAsync())
            .ReturnsAsync([
                new RootFolder
                {
                    Id = 1,
                    Name = "Legacy Library",
                    Path = ambiguousRoot,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
                }
            ]);
        var resolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        resolver.Setup(service => service.ResolveAsync(
                destinationPath,
                FileSystemCaseSensitivityMode.Auto,
                It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<FileSystemSemanticsResolution>(
                new FileSystemSemanticsResolution(
                    autoSemantics,
                    PathIdentityState.Valid,
                    canonicalRoot)));
        var relocationService = new Mock<IRootFolderRelocationService>(MockBehavior.Strict);
        relocationService.Setup(service => service.IsBoundaryProtectedAsync(
                destinationPath,
                autoSemantics,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        repository.Setup(service => service.GetOtherPathReferenceSnapshotsAsync(
                0,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new AudiobookPathReferenceSnapshot(17, existingAlias, null)
            ]);
        var guard = new LibraryDestinationMutationGuard(
            rootFolderService.Object,
            relocationService.Object,
            resolver.Object,
            repository.Object);

        var reason = await guard.GetBlockingReasonAsync(destinationPath);

        Assert.Equal(
            "Destination filesystem identity is unavailable.",
            reason);
        Assert.True(FileSystemPathIdentity.AreEquivalent(
            existingAlias,
            destinationPath,
            configuredSemantics));
    }

    [Fact]
    public async Task GetBlockingReasonAsync_ChangedAutoRootSemantics_FailsClosedUntilRepaired()
    {
        var rootPath = Path.GetFullPath(Path.Join(
            Path.GetTempPath(),
            $"guard-changed-auto-root-{Guid.NewGuid():N}"));
        var destinationPath = Path.Join(rootPath, "Author", "Title");
        var liveSemantics = FileSystemPathSemantics.CurrentHostDefault;
        var persistedSensitivity = liveSemantics.CaseSensitivity
            == FileSystemCaseSensitivity.Sensitive
                ? FileSystemCaseSensitivity.Insensitive
                : FileSystemCaseSensitivity.Sensitive;
        var persistedSemantics = new FileSystemPathSemantics(
            liveSemantics.Syntax,
            persistedSensitivity);
        var rootFolderService = new Mock<IRootFolderService>(MockBehavior.Strict);
        rootFolderService.Setup(service => service.GetAllAsync())
            .ReturnsAsync([
                new RootFolder
                {
                    Id = 1,
                    Name = "Changed Auto Library",
                    Path = rootPath,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                    ResolvedCaseSensitivity = persistedSensitivity,
                    PathIdentityState = PathIdentityState.Valid,
                    PathIdentityKey = FileSystemPathIdentity.CreateKey(
                        "root",
                        rootPath,
                        persistedSemantics)
                }
            ]);
        var resolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        resolver.Setup(service => service.ResolveAsync(
                rootPath,
                FileSystemCaseSensitivityMode.Auto,
                It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<FileSystemSemanticsResolution>(
                new FileSystemSemanticsResolution(
                    liveSemantics,
                    PathIdentityState.Valid,
                    rootPath)));
        var guard = new LibraryDestinationMutationGuard(
            rootFolderService.Object,
            Mock.Of<IRootFolderRelocationService>(),
            resolver.Object,
            Mock.Of<IAudiobookRepository>());

        var reason = await guard.GetBlockingReasonAsync(destinationPath);

        Assert.Equal("Destination filesystem identity is unavailable.", reason);
    }

    [Fact]
    public async Task GetBlockingReasonAsync_UnavailableConfiguredRoot_DoesNotBorrowAutoSemantics()
    {
        var rootPath = Path.GetFullPath(Path.Join(
            Path.GetTempPath(),
            $"guard-unavailable-root-{Guid.NewGuid():N}"));
        var destinationPath = Path.Join(rootPath, "Author", "Title");
        var configuredSemantics = new FileSystemPathSemantics(
            FileSystemPathSemantics.CurrentHostDefault.Syntax,
            FileSystemCaseSensitivity.Insensitive);
        var autoSemantics = new FileSystemPathSemantics(
            FileSystemPathSemantics.CurrentHostDefault.Syntax,
            FileSystemCaseSensitivity.Sensitive);
        var rootFolderService = new Mock<IRootFolderService>(MockBehavior.Strict);
        rootFolderService.Setup(service => service.GetAllAsync())
            .ReturnsAsync([
                new RootFolder
                {
                    Id = 1,
                    Name = "Unavailable Library",
                    Path = rootPath,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
                }
            ]);
        var resolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        resolver.Setup(service => service.ResolveAsync(
                rootPath,
                FileSystemCaseSensitivityMode.Insensitive,
                It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<FileSystemSemanticsResolution>(
                new FileSystemSemanticsResolution(
                    configuredSemantics,
                    PathIdentityState.Unavailable,
                    rootPath,
                    "configured root probe failed")));
        resolver.Setup(service => service.ResolveAsync(
                destinationPath,
                FileSystemCaseSensitivityMode.Auto,
                It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<FileSystemSemanticsResolution>(
                new FileSystemSemanticsResolution(
                    autoSemantics,
                    PathIdentityState.Valid,
                    rootPath)));
        var relocationService = new Mock<IRootFolderRelocationService>(MockBehavior.Strict);
        relocationService.Setup(service => service.IsBoundaryProtectedAsync(
                destinationPath,
                autoSemantics,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        repository.Setup(service => service.GetOtherPathReferenceSnapshotsAsync(
                0,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var guard = new LibraryDestinationMutationGuard(
            rootFolderService.Object,
            relocationService.Object,
            resolver.Object,
            repository.Object);

        var reason = await guard.GetBlockingReasonAsync(destinationPath);

        Assert.Equal("Destination filesystem identity is unavailable.", reason);
        resolver.Verify(service => service.ResolveAsync(
            destinationPath,
            FileSystemCaseSensitivityMode.Auto,
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetBlockingReasonAsync_UnavailableCustomDestinationFailsClosed()
    {
        var destinationPath = Path.GetFullPath(Path.Join(
            Path.GetTempPath(),
            $"guard-custom-{Guid.NewGuid():N}"));
        var rootFolderService = new Mock<IRootFolderService>(MockBehavior.Strict);
        rootFolderService.Setup(service => service.GetAllAsync())
            .ReturnsAsync([]);
        var resolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        resolver.Setup(service => service.ResolveAsync(
                destinationPath,
                FileSystemCaseSensitivityMode.Auto,
                It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<FileSystemSemanticsResolution>(
                new FileSystemSemanticsResolution(
                    FileSystemPathSemantics.CurrentHostDefault,
                    PathIdentityState.Unavailable,
                    destinationPath,
                    "unavailable")));
        var guard = new LibraryDestinationMutationGuard(
            rootFolderService.Object,
            Mock.Of<IRootFolderRelocationService>(),
            resolver.Object,
            Mock.Of<IAudiobookRepository>());

        var reason = await guard.GetBlockingReasonAsync(destinationPath);

        Assert.Equal("Destination filesystem identity is unavailable.", reason);
    }
}
