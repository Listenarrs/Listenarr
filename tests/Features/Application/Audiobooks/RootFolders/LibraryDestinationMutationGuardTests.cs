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
            resolver.Object);

        var reason = await guard.GetBlockingReasonAsync(destinationPath);

        Assert.Equal("Destination overlaps an active root folder relocation.", reason);
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
            resolver.Object);

        var reason = await guard.GetBlockingReasonAsync(destinationPath);

        Assert.Equal("Destination filesystem identity is unavailable.", reason);
    }
}
