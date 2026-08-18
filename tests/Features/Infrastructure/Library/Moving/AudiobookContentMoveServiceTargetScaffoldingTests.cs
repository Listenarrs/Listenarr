using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class AudiobookContentMoveServiceTests
{
    [Fact]
    public async Task MoveContentsAsync_MissingNestedTargetAncestors_AreNotCopiedAsContent()
    {
        var source = FileService.GetTempDirectory("content-move-nested-scaffold-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(source, "container", "nested", "target");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();

        var result = await service.MoveContentsAsync(request, CancellationToken.None);

        Assert.True(result.SourceCleanupCompleted);
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        Assert.False(Directory.Exists(Path.Join(target, "container")));
        Assert.False(Directory.Exists(Path.Join(target, "nested")));
        await using var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync();
        var scaffolding = await db.MoveJobCreatedDirectories
            .AsNoTracking()
            .Where(directory => directory.MoveJobId == request.JobId)
            .OrderBy(directory => directory.Path)
            .ToListAsync();
        Assert.Equal(3, scaffolding.Count);
        var expectedState = OperatingSystem.IsWindows()
            ? MoveCreatedDirectoryState.Created
            : MoveCreatedDirectoryState.Retained;
        Assert.All(scaffolding, directory =>
            Assert.Equal(expectedState, directory.State));
    }

    [Fact]
    public async Task MoveContentsAsync_TargetDirectoryCreationIoFailure_RemainsTransientAndPlanned()
    {
        var source = FileService.GetTempDirectory("content-move-scaffold-transient-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var targetParent = FileService.GetTempDirectory("content-move-scaffold-transient-dst");
        var target = Path.Join(targetParent, "Book");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var hookRan = false;
        using var hook = ExclusiveDirectoryCreator.PushBeforeCreateHook(path =>
        {
            if (hookRan || !string.Equals(path, target, StringComparison.Ordinal))
            {
                return;
            }

            hookRan = true;
            throw new IOException("Injected transient target-directory outage.");
        });
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.Contains("Injected transient target-directory outage", exception.Message, StringComparison.Ordinal);
        Assert.True(hookRan);
        Assert.True(File.Exists(sourceFile));
        Assert.False(Directory.Exists(target));
        await using var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync();
        var planned = await db.MoveJobCreatedDirectories
            .AsNoTracking()
            .SingleAsync(directory =>
                directory.MoveJobId == request.JobId
                && directory.Path == target);
        Assert.Equal(MoveCreatedDirectoryState.Planned, planned.State);
        Assert.Null(planned.DirectoryObjectIdentity);
    }

    [LinuxFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task MoveContentsAsync_ExistingInaccessibleTarget_IsNotPersistedAsMissingScaffolding()
    {
        var root = FileService.GetTempDirectory("content-move-scaffold-inaccessible-root");
        var source = Path.Join(root, "source");
        Directory.CreateDirectory(source);
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var targetParent = Path.Join(root, "destination");
        var target = Path.Join(targetParent, "Book");
        Directory.CreateDirectory(target);
        var request = await CreateLeasedMoveRequestAsync(
            source,
            target,
            sourceCleanupBoundary: root,
            executionProtocolVersion: MoveExecutionProtocol.MarkerlessDatabaseState);
        var originalMode = File.GetUnixFileMode(targetParent);
        File.SetUnixFileMode(targetParent, UnixFileMode.None);
        try
        {
            // Root can bypass Unix permission checks. The unprivileged Linux
            // validation environment exercises the access-denied branch.
            if (!Directory.Exists(target))
            {
                var service = _provider.GetRequiredService<AudiobookContentMoveService>();
                var exception = await Record.ExceptionAsync(() =>
                    service.MoveContentsAsync(request, CancellationToken.None));

                Assert.NotNull(exception);
                Assert.IsNotType<MoveNeedsAttentionException>(exception);
                Assert.True(
                    exception is UnauthorizedAccessException
                        or IOException
                        or System.ComponentModel.Win32Exception,
                    exception.ToString());
                Assert.True(File.Exists(sourceFile));
                await using var db = await _provider
                    .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
                    .CreateDbContextAsync();
                Assert.False(await db.MoveJobCreatedDirectories
                    .AsNoTracking()
                    .AnyAsync(directory => directory.MoveJobId == request.JobId));
            }
        }
        finally
        {
            File.SetUnixFileMode(targetParent, originalMode);
        }
    }

    [Fact]
    public async Task MoveContentsAsync_TargetParentReplacedAfterAuthorization_DoesNotCreateInReplacement()
    {
        var source = FileService.GetTempDirectory("content-move-scaffold-parent-race-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var targetParent = FileService.GetTempDirectory("content-move-scaffold-parent-race-dst");
        var displacedParent = targetParent + ".original";
        var target = Path.Join(targetParent, "Book");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var hookRan = false;
        using var hook = ExclusiveDirectoryCreator.PushBeforeCreateHook(path =>
        {
            if (hookRan || !string.Equals(path, target, StringComparison.Ordinal))
            {
                return;
            }

            hookRan = true;
            Directory.Move(targetParent, displacedParent);
            Directory.CreateDirectory(targetParent);
            File.WriteAllText(Path.Join(targetParent, "foreign.txt"), "replacement generation");
        });
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();

        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.Contains("parent changed", exception.Message, StringComparison.OrdinalIgnoreCase);

        Assert.True(hookRan);
        Assert.True(File.Exists(sourceFile));
        Assert.False(Directory.Exists(target));
        Assert.Equal(
            "replacement generation",
            await File.ReadAllTextAsync(Path.Join(targetParent, "foreign.txt")));
        Assert.True(Directory.Exists(displacedParent));
    }

    [Fact]
    public async Task MoveContentsAsync_PersistedScaffoldWithUnexpectedContent_FailsClosed()
    {
        var source = FileService.GetTempDirectory("content-move-scaffold-content-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var scaffold = Path.Join(source, "container");
        var target = Path.Join(scaffold, "target");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        await using (var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync())
        {
            db.MoveJobCreatedDirectories.Add(new MoveJobCreatedDirectory
            {
                MoveJobId = request.JobId,
                Path = scaffold,
                State = MoveCreatedDirectoryState.Planned
            });
            await db.SaveChangesAsync();
        }
        Directory.CreateDirectory(scaffold);
        await File.WriteAllTextAsync(Path.Join(scaffold, "operator-note.txt"), "keep me");
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();

        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.Contains("unexpected content", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Join(scaffold, "operator-note.txt")));
        Assert.True(File.Exists(Path.Join(source, "book.m4b")));
    }
}
