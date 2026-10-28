using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class AudiobookContentMoveServiceTests
{
    [Fact]
    public async Task MoveContentsAsync_WithoutOwnerCommit_RetainsCopiedSource()
    {
        var source = FileService.GetTempDirectory("move-no-owner-commit-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(FileService.GetTempPath(), $"move-no-owner-commit-dst-{Guid.NewGuid():N}");
        var request = (await CreateLeasedMoveRequestAsync(source, target)) with
        {
            CommitOwnerMetadataAsync = null
        };
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();

        var result = await service.MoveContentsAsync(request, CancellationToken.None);
        using var verification = result.TargetVerificationLease;

        Assert.True(result.SourceRetained);
        Assert.True(result.SourceCleanupCompleted);
        Assert.Equal("audio", await File.ReadAllTextAsync(sourceFile));
        Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
    }

    [Fact]
    public async Task MoveContentsAsync_OwnerCommitFails_PreservesCopiedSource()
    {
        var source = FileService.GetTempDirectory("move-failed-owner-commit-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(FileService.GetTempPath(), $"move-failed-owner-commit-dst-{Guid.NewGuid():N}");
        var request = (await CreateLeasedMoveRequestAsync(source, target)) with
        {
            CommitOwnerMetadataAsync = (_, _) => throw new InvalidOperationException("Owner commit failed")
        };
        var service = new AudiobookContentMoveService(
            NullLogger<AudiobookContentMoveService>.Instance,
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new DisableNativeRenameForPublicationTest());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.Equal("Owner commit failed", error.Message);
        Assert.Equal("audio", await File.ReadAllTextAsync(sourceFile));
        Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
    }

    [LinuxFact]
    public async Task MoveContentsAsync_TargetReplacedDuringOwnerCommit_PreservesSource()
    {
        var source = FileService.GetTempDirectory("move-target-substitution-at-commit-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(FileService.GetTempPath(), $"move-target-substitution-at-commit-dst-{Guid.NewGuid():N}");
        var targetFile = Path.Join(target, "book.m4b");
        var request = (await CreateLeasedMoveRequestAsync(source, target)) with
        {
            CommitOwnerMetadataAsync = async (_, token) =>
            {
                File.Move(targetFile, targetFile + ".original");
                await File.WriteAllTextAsync(targetFile, "audio", token);
            }
        };
        var service = new AudiobookContentMoveService(
            NullLogger<AudiobookContentMoveService>.Instance,
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new DisableNativeRenameForPublicationTest());

        await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.Equal("audio", await File.ReadAllTextAsync(sourceFile));
        Assert.Equal("audio", await File.ReadAllTextAsync(targetFile));
        Assert.Equal("audio", await File.ReadAllTextAsync(targetFile + ".original"));
    }

    [Fact]
    public async Task MoveContentsAsync_OwnedTarget_StoredDiagnosticIdentityDoesNotAuthorizePublication()
    {
        var source = FileService.GetTempDirectory("move-owned-target-diagnostic-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = FileService.GetTempDirectory("move-owned-target-diagnostic-dst");
        var ownershipStore = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
        var ownership = await ownershipStore.RecordCreatedAsync(new LibraryDirectoryOwnershipClaim(
            target, FileSystemPathSemantics.CurrentHostDefault, "test"));
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var persisted = await db.LibraryDirectoryOwnerships.SingleAsync(row => row.Id == ownership.Id);
            persisted.DirectoryObjectIdentity = "legacy-mount-observation";
            await db.SaveChangesAsync();
        }
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();

        var result = await service.MoveContentsAsync(request, CancellationToken.None);
        using var verification = result.TargetVerificationLease;

        Assert.True(result.SourceCleanupCompleted);
        Assert.False(File.Exists(sourceFile));
        Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
        await using var current = await factory.CreateDbContextAsync();
        Assert.Equal("legacy-mount-observation",
            (await current.LibraryDirectoryOwnerships.SingleAsync(row => row.Id == ownership.Id)).DirectoryObjectIdentity);
    }

    [Fact]
    public async Task MoveContentsAsync_OwnedSource_StoredDiagnosticIdentityDoesNotAuthorizeRetirement()
    {
        var source = FileService.GetTempDirectory("move-owned-source-diagnostic-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(FileService.GetTempPath(), $"move-owned-source-diagnostic-dst-{Guid.NewGuid():N}");
        var ownershipStore = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
        var ownership = await ownershipStore.RecordCreatedAsync(new LibraryDirectoryOwnershipClaim(
            source, FileSystemPathSemantics.CurrentHostDefault, "test"));
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var persisted = await db.LibraryDirectoryOwnerships.SingleAsync(row => row.Id == ownership.Id);
            persisted.DirectoryObjectIdentity = "legacy-mount-observation";
            await db.SaveChangesAsync();
        }
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var service = new AudiobookContentMoveService(
            NullLogger<AudiobookContentMoveService>.Instance,
            factory, TimeProvider.System, new DisableNativeRenameForPublicationTest());

        var result = await service.MoveContentsAsync(request, CancellationToken.None);
        using var verification = result.TargetVerificationLease;

        Assert.True(result.SourceCleanupCompleted);
        Assert.False(File.Exists(sourceFile));
        Assert.False(Directory.Exists(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
        await using var current = await factory.CreateDbContextAsync();
        var retired = await current.LibraryDirectoryOwnerships.SingleAsync(row => row.Id == ownership.Id);
        Assert.Equal(LibraryDirectoryOwnershipState.Removed, retired.State);
        Assert.Equal("legacy-mount-observation", retired.DirectoryObjectIdentity);
    }

    [LinuxFact]
    public async Task MoveContentsAsync_EmptySourceDirectoryReplacedAtOwnerCommit_PreservesBothDirectories()
    {
        var source = FileService.GetTempDirectory("move-source-directory-substitution");
        var child = Path.Join(source, "empty");
        Directory.CreateDirectory(child);
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(FileService.GetTempPath(), $"move-directory-substitution-dst-{Guid.NewGuid():N}");
        var displaced = child + ".original";
        var request = (await CreateLeasedMoveRequestAsync(source, target)) with
        {
            CommitOwnerMetadataAsync = (_, _) =>
            {
                Directory.Move(child, displaced);
                Directory.CreateDirectory(child);
                return Task.CompletedTask;
            }
        };
        var service = new AudiobookContentMoveService(
            NullLogger<AudiobookContentMoveService>.Instance,
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System, new DisableNativeRenameForPublicationTest());

        var result = await service.MoveContentsAsync(request, CancellationToken.None);
        using var verification = result.TargetVerificationLease;
        using var ancestors = result.SourceAncestorRetirementLease;

        Assert.True(result.SourceRetained);
        Assert.True(Directory.Exists(child));
        Assert.True(Directory.Exists(displaced));
        Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
    }

    [LinuxFact]
    public async Task FinalizeMove_OwnedAncestorReplacedAfterPublication_PreservesBothDirectories()
    {
        var boundary = FileService.GetTempDirectory("move-owned-ancestor-substitution");
        var ancestor = Path.Join(boundary, "Author");
        var source = Path.Join(ancestor, "Book");
        Directory.CreateDirectory(source);
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        await ClaimOwnedDirectoriesAsync(ancestor);
        var target = Path.Join(FileService.GetTempPath(), $"move-ancestor-substitution-dst-{Guid.NewGuid():N}");
        var request = await CreateLeasedMoveRequestAsync(source, target, sourceCleanupBoundary: boundary);
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var result = await service.MoveContentsAsync(request, CancellationToken.None);
        using var verification = result.TargetVerificationLease;
        using var sourceProof = result.SourceAncestorRetirementLease;
        var displaced = ancestor + ".original";
        Directory.Move(ancestor, displaced);
        Directory.CreateDirectory(ancestor);

        await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.FinalizeMoveAsync(request, result, CancellationToken.None));

        Assert.True(Directory.Exists(ancestor));
        Assert.True(Directory.Exists(displaced));
        Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
    }

    [Fact]
    public async Task FinalizeMove_WithoutOriginalAncestorProof_RetainsOwnedAncestor()
    {
        var boundary = FileService.GetTempDirectory("move-no-live-ancestor-proof");
        var ancestor = Path.Join(boundary, "Author");
        var source = Path.Join(ancestor, "Book");
        Directory.CreateDirectory(source);
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        await ClaimOwnedDirectoriesAsync(ancestor);
        var target = Path.Join(FileService.GetTempPath(), $"move-no-ancestor-proof-dst-{Guid.NewGuid():N}");
        var request = await CreateLeasedMoveRequestAsync(source, target, sourceCleanupBoundary: boundary);
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var result = await service.MoveContentsAsync(request, CancellationToken.None);
        using var verification = result.TargetVerificationLease;
        result.SourceAncestorRetirementLease?.Dispose();

        await service.FinalizeMoveAsync(
            request, result with { SourceAncestorRetirementLease = null }, CancellationToken.None);

        Assert.True(Directory.Exists(ancestor));
        Assert.False(Directory.Exists(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
    }

    private sealed class DisableNativeRenameForPublicationTest : IMoveFaultInjector;
}
