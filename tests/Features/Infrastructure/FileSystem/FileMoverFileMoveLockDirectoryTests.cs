using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Area", "FileSystem")]
[Trait("Name", "FileMoverFileMoveLockDirectoryTests")]
[Trait("Category", "FileSystem")]
public sealed class FileMoverFileMoveLockDirectoryTests : BaseTests
{
    [Fact]
    [Trait("Method", "PerformActionOn")]
    [Trait("Scenario", "CreatesApplicationOwnedLockHierarchyFromScratch")]
    public async Task PerformActionOn_CreatesApplicationOwnedLockHierarchyFromScratch()
    {
        var contentRoot = FileService.GetTempDirectory("file-move-lock-app-root");
        var paths = new ApplicationPathService(contentRoot);
        var sourceRoot = FileService.GetTempDirectory("file-move-lock-source");
        var source = await FileService.GetFileAsync(sourceRoot, "source.m4b", "audio");
        var destination = Path.Join(sourceRoot, "destination.m4b");
        Assert.False(Directory.Exists(paths.ConfigRootPath));
        Assert.False(Directory.Exists(paths.FileMoveLockRootPath));

        var mover = CreateMover(paths);

        var result = await mover.PerformActionOn(
            FileAction.Copy,
            source,
            destination,
            Guid.NewGuid());

        Assert.True(result);
        Assert.Equal("audio", await File.ReadAllTextAsync(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(destination));
        Assert.True(Directory.Exists(paths.FileMoveLockRootPath));
        Assert.NotEmpty(Directory.EnumerateFiles(
            paths.FileMoveLockRootPath,
            "stripe-*.lock",
            SearchOption.TopDirectoryOnly));
        Assert.Empty(Directory.EnumerateFiles(
            sourceRoot,
            "stripe-*.lock",
            SearchOption.AllDirectories));
    }

    [Fact]
    [Trait("Method", "DependencyInjection")]
    [Trait("Scenario", "ResolvedMoverUsesRegisteredApplicationLockRoot")]
    public async Task DependencyInjection_ResolvedMoverUsesRegisteredApplicationLockRoot()
    {
        var sourceRoot = FileService.GetTempDirectory("file-move-lock-di-source");
        var source = await FileService.GetFileAsync(sourceRoot, "source.m4b", "audio");
        var destination = Path.Join(sourceRoot, "destination.m4b");
        Assert.False(Directory.Exists(_applicationPathService.FileMoveLockRootPath));
        var mover = _provider.GetRequiredService<FileMover>();

        var result = await mover.PerformActionOn(
            FileAction.Copy,
            source,
            destination,
            Guid.NewGuid());

        Assert.True(result);
        Assert.True(Directory.Exists(_applicationPathService.FileMoveLockRootPath));
        Assert.NotEmpty(Directory.EnumerateFiles(
            _applicationPathService.FileMoveLockRootPath,
            "stripe-*.lock",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    [Trait("Method", "PerformActionOn")]
    [Trait("Scenario", "MissingApplicationLockRootFailsClosed")]
    public async Task PerformActionOn_MissingApplicationLockRootFailsClosed()
    {
        var sourceRoot = FileService.GetTempDirectory("file-move-lock-missing-root");
        var source = await FileService.GetFileAsync(sourceRoot, "source.m4b", "audio");
        var destination = Path.Join(sourceRoot, "destination.m4b");
        var paths = Mock.Of<IApplicationPathService>(service =>
            service.FileMoveLockRootPath == string.Empty);
        var mover = CreateMover(paths);

        var result = await mover.PerformActionOn(
            FileAction.Copy,
            source,
            destination,
            Guid.NewGuid());

        Assert.False(result);
        Assert.Equal("audio", await File.ReadAllTextAsync(source));
        Assert.False(File.Exists(destination));
    }

    [DirectoryLinkFact]
    [Trait("Method", "PerformActionOn")]
    [Trait("Scenario", "LinkedApplicationLockAncestorFailsClosed")]
    public async Task PerformActionOn_LinkedApplicationLockAncestorFailsClosed()
    {
        var contentRoot = FileService.GetTempDirectory("file-move-lock-linked-app-root");
        var paths = new ApplicationPathService(contentRoot);
        var sourceRoot = FileService.GetTempDirectory("file-move-lock-linked-source");
        var source = await FileService.GetFileAsync(sourceRoot, "source.m4b", "audio");
        var destination = Path.Join(sourceRoot, "destination.m4b");
        var external = FileService.GetTempDirectory("file-move-lock-linked-external");
        Directory.CreateDirectory(paths.ConfigRootPath);
        Directory.CreateSymbolicLink(
            Path.Join(paths.ConfigRootPath, "runtime"),
            external);
        var mover = CreateMover(paths);

        var result = await mover.PerformActionOn(
            FileAction.Copy,
            source,
            destination,
            Guid.NewGuid());

        Assert.False(result);
        Assert.False(Directory.Exists(Path.Join(external, "file-move-locks")));
        Assert.Equal("audio", await File.ReadAllTextAsync(source));
        Assert.False(File.Exists(destination));
    }

    private FileMover CreateMover(IApplicationPathService applicationPathService)
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        return new FileMover(
            new NullLogger<FileMover>(),
            options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
            semanticsResolver: new FileSystemSemanticsResolver(),
            applicationPathService: applicationPathService,
            dbContextFactory: factory,
            timeProvider: TimeProvider.System);
    }
}
