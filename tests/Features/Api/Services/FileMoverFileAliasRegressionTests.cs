using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Services;

[Trait("Area", "FileSystem")]
[Trait("Name", "FileMoverFileAliasRegressionTests")]
[Trait("Category", "FileSystem")]
public sealed class FileMoverFileAliasRegressionTests : BaseTests
{
    [FileLinkTheory]
    [InlineData(FileAction.Move)]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task FileOperation_DestinationSymlinkAlias_IsBlocked(FileAction action)
    {
        var root = FileService.GetTempDirectory("file-alias-leaf");
        var source = await FileService.GetFileAsync(root, "source.m4b", "audio");
        var destination = Path.Join(root, "destination.m4b");
        File.CreateSymbolicLink(destination, source);

        Assert.False(await PerformAsync(action, source, destination));
        Assert.Equal("audio", await File.ReadAllTextAsync(source));
    }

    [DirectoryLinkTheory]
    [InlineData(FileAction.Move)]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task FileOperation_SymbolicLinkAncestorAlias_IsBlocked(FileAction action)
    {
        var root = FileService.GetTempDirectory("file-alias-ancestor");
        var physicalParent = Path.Join(root, "physical");
        Directory.CreateDirectory(physicalParent);
        var source = await FileService.GetFileAsync(physicalParent, "book.m4b", "audio");
        var aliasParent = Path.Join(root, "alias");
        Directory.CreateSymbolicLink(aliasParent, physicalParent);
        var destination = Path.Join(aliasParent, "book.m4b");

        Assert.False(await PerformAsync(action, source, destination));
        Assert.Equal("audio", await File.ReadAllTextAsync(source));
    }

    [Theory]
    [InlineData(FileAction.Move)]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task FileOperation_LiteralSamePath_RemainsIdempotent(FileAction action)
    {
        var root = FileService.GetTempDirectory("file-alias-identical");
        var source = await FileService.GetFileAsync(root, "book.m4b", "audio");

        Assert.True(await PerformAsync(action, source, source));
        Assert.Equal("audio", await File.ReadAllTextAsync(source));
    }

    [FileLinkTheory]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task FileOperation_DestinationSymlinkToUnrelatedFile_IsBlocked(FileAction action)
    {
        var root = FileService.GetTempDirectory("file-unrelated-link-destination");
        var source = await FileService.GetFileAsync(root, "source.m4b", "audio");
        var external = await FileService.GetFileAsync(root, "external.m4b", "external");
        var destination = Path.Join(root, "destination.m4b");
        File.CreateSymbolicLink(destination, external);

        Assert.False(await PerformAsync(action, source, destination));
        Assert.Equal("audio", await File.ReadAllTextAsync(source));
        Assert.Equal("external", await File.ReadAllTextAsync(external));
    }

    [FileLinkTheory]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task FileOperation_SourceSymlinkToUnrelatedFile_IsBlocked(FileAction action)
    {
        var root = FileService.GetTempDirectory("file-unrelated-link-source");
        var external = await FileService.GetFileAsync(root, "external.m4b", "external");
        var source = Path.Join(root, "source.m4b");
        var destination = Path.Join(root, "destination.m4b");
        File.CreateSymbolicLink(source, external);

        Assert.False(await PerformAsync(action, source, destination));
        Assert.Equal("external", await File.ReadAllTextAsync(external));
        Assert.False(File.Exists(destination));
    }

    [DirectoryLinkTheory]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task FileOperation_DestinationLinkedAncestor_IsBlockedBeforeCreatingChildren(FileAction action)
    {
        var root = FileService.GetTempDirectory("file-linked-ancestor-external");
        var source = await FileService.GetFileAsync(root, "source.m4b", "audio");
        var external = Path.Join(root, "external");
        Directory.CreateDirectory(external);
        var linkedParent = Path.Join(root, "linked-parent");
        Directory.CreateSymbolicLink(linkedParent, external);
        var destination = Path.Join(linkedParent, "nested", "destination.m4b");

        Assert.False(await PerformAsync(action, source, destination));
        Assert.False(Directory.Exists(Path.Join(external, "nested")));
        Assert.Equal("audio", await File.ReadAllTextAsync(source));
    }

    [DirectoryLinkTheory]
    [InlineData(FileAction.Move)]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task FileOperation_LinkedLockDirectoryAncestor_DoesNotCreateOutsideBoundary(FileAction action)
    {
        var root = FileService.GetTempDirectory("file-lock-linked-ancestor");
        var source = await FileService.GetFileAsync(root, "source.m4b", "audio");
        var destination = Path.Join(root, "destination.m4b");
        var external = Path.Join(root, "external-lock-root");
        Directory.CreateDirectory(external);
        var linkedParent = Path.Join(root, "linked-lock-parent");
        Directory.CreateSymbolicLink(linkedParent, external);
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var mover = new FileMover(
            new NullLogger<FileMover>(),
            dbContextFactory: factory,
            timeProvider: TimeProvider.System)
        {
            FileMoveLockDirectoryForTest = Path.Join(linkedParent, "file-move-locks")
        };

        Assert.False(await mover.PerformActionOn(
            action,
            source,
            destination,
            Guid.NewGuid()));
        Assert.False(Directory.Exists(Path.Join(external, "file-move-locks")));
        Assert.Equal("audio", await File.ReadAllTextAsync(source));
        Assert.False(File.Exists(destination));
    }

    [Theory]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task FileOperation_EmptyOperationId_IsBlockedBeforeMutation(FileAction action)
    {
        var root = FileService.GetTempDirectory("file-empty-operation-id");
        var source = await FileService.GetFileAsync(root, "source.m4b", "audio");
        var destination = Path.Join(root, "destination.m4b");

        Assert.False(await CreateMover().PerformActionOn(
            action,
            source,
            destination,
            Guid.Empty));
        Assert.Equal("audio", await File.ReadAllTextAsync(source));
        Assert.False(File.Exists(destination));
    }

    private Task<bool> PerformAsync(
        FileAction action,
        string source,
        string destination) =>
        CreateMover().PerformActionOn(action, source, destination, Guid.NewGuid());

    private FileMover CreateMover()
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        return new FileMover(
            new NullLogger<FileMover>(),
            dbContextFactory: factory,
            timeProvider: TimeProvider.System)
        {
            FileMoveLockDirectoryForTest = FileService.GetTempDirectory(
                "file-alias-markerless-locks")
        };
    }
}
