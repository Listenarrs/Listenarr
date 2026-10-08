using System.Diagnostics;
using System.Reflection;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Services;

[Trait("Area", "FileSystem")]
[Trait("Name", "FileMoverFileAliasRegressionTests")]
[Trait("Category", "FileSystem")]
public sealed class FileMoverFileAliasRegressionTests : BaseTests
{
    [NetworkStorageTheory]
    [InlineData(false, "source")]
    [InlineData(true, "source")]
    [InlineData(false, "ownership")]
    [InlineData(true, "ownership")]
    [InlineData(false, "cleanup")]
    [InlineData(true, "cleanup")]
    public async Task MountedRootCaseAlias_UsesConfiguredSemantics(bool nested, string operation)
    {
        var mount = Environment.GetEnvironmentVariable(NetworkStorageTheoryAttribute.PathEnvironmentVariable)!;
        var rootPath = Path.Join(mount, "SourceCapability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        var alias = Path.Join(mount, Path.GetFileName(rootPath).ToLowerInvariant());
        var insensitive = Directory.Exists(alias);
        var semantics = new FileSystemPathSemantics(FileSystemPathSyntax.Unix,
            insensitive ? FileSystemCaseSensitivity.Insensitive : FileSystemCaseSensitivity.Sensitive);
        var root = new RootFolderBuilder()
            .WithName("Mounted source capability case contract")
            .WithPath(rootPath)
            .WithCaseSensitivityMode(insensitive ? FileSystemCaseSensitivityMode.Insensitive : FileSystemCaseSensitivityMode.Sensitive)
            .Build();
        root.ResolvedCaseSensitivity = semantics.CaseSensitivity;
        root.PathIdentityState = PathIdentityState.Valid;
        root.PathIdentityKey = FileSystemPathIdentity.CreateKey("root", rootPath, semantics);
        await _rootFolderRepository.AddAsync(root);
        var parent = nested ? Path.Join(rootPath, "Book") : rootPath;
        Directory.CreateDirectory(parent);
        var source = Path.Join(parent, "source.m4b");
        await File.WriteAllTextAsync(source, "mounted-case-alias-source");
        var requested = Path.Join(insensitive ? alias : rootPath,
            nested ? "Book/source.m4b" : "source.m4b");

        switch (operation)
        {
            case "source":
                var capability = await _provider.GetRequiredService<IFilePublicationSourceCapability>().CheckAsync(requested);
                Assert.True(capability.IsSupported, capability.Reason);
                Assert.NotNull(capability.SourceProof);
                break;
            case "ownership":
                using (var authorization = await new Listenarr.Infrastructure.Library.Moving.LibraryDirectoryOwnershipBoundaryAuthorizer(
                    _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>())
                    .AuthorizeContainingRootAsync(requested, semantics, CancellationToken.None))
                {
                    Assert.True(authorization.ParentAnchor.VisiblePathMatches());
                }
                break;
            case "cleanup":
                var cleanup = await _provider.GetRequiredService<IMoveSourceCleanupPolicyResolver>()
                    .ResolveAsync(Path.GetDirectoryName(requested)!, Path.Join(rootPath, "Target"));
                Assert.Equal(root.Id, cleanup.SourceRootFolderId);
                break;
        }
        Assert.Equal("mounted-case-alias-source", await File.ReadAllTextAsync(source));
    }

    [DirectoryLinkTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceCapability_ConfiguredLinkedBoundary_AllowsRootButRejectsLinkedDescendant(bool linkedDescendant)
    {
        var root = FileService.GetTempDirectory("source-capability-linked-root");
        var physical = Directory.CreateDirectory(Path.Join(root, "physical")).FullName;
        var linked = Path.Join(root, "linked");
        Directory.CreateSymbolicLink(linked, physical);
        var foreign = Directory.CreateDirectory(Path.Join(root, "foreign")).FullName;
        var parent = Path.Join(linked, "Book");
        if (linkedDescendant) Directory.CreateSymbolicLink(parent, foreign);
        else Directory.CreateDirectory(parent);
        var source = Path.Join(parent, "source.m4b");
        await File.WriteAllTextAsync(source, "original-source");
        await AddAuthorizedRootAsync(linked, "Linked source capability root");

        var capability = await _provider.GetRequiredService<IFilePublicationSourceCapability>().CheckAsync(source);

        Assert.Equal(!linkedDescendant, capability.IsSupported);
        Assert.Equal("original-source", await File.ReadAllTextAsync(source));
    }
    [LinuxFact]
    public async Task RegularFileIdentityProbe_NamedPipe_ReturnsWithoutBlockingAndRejectsSpecialFile()
    {
        var root = FileService.GetTempDirectory("file-alias-named-pipe");
        var pipePath = Path.Join(root, "candidate.m4b");
        var startInfo = new ProcessStartInfo("mkfifo")
        {
            UseShellExecute = false,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(pipePath);
        using (var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start mkfifo."))
        {
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }

        var method = typeof(FileMover)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(candidate =>
            {
                if (candidate.Name != "TryGetRegularFileIdentity")
                {
                    return false;
                }

                var parameters = candidate.GetParameters();
                return parameters.Length == 2
                    && parameters[0].ParameterType == typeof(string);
            });
        var arguments = new object?[] { pipePath, null };
        var probeTask = Task.Run(() =>
            Assert.IsType<bool>(method.Invoke(null, arguments)));
        var completed = await Task.WhenAny(
            probeTask,
            Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(probeTask, completed);
        Assert.False(await probeTask);
    }

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
