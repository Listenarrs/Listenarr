using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class AudiobookContentMoveServiceTests
{
    [Fact]
    public async Task MoveContentsAsync_EmptySourceWithoutTrackedManifest_RequiresAttention()
    {
        var source = FileService.GetTempDirectory("content-move-empty-copy-src");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-empty-copy-dst-{Guid.NewGuid():N}");
        var request = await CreateLeasedMoveRequestAsync(
            source,
            target,
            deleteEmptySource: false);
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();

        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.Contains(
            "no persisted tracked-file source manifest",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(source));
        Assert.Empty(Directory.EnumerateFileSystemEntries(source));
        Assert.False(Directory.Exists(target));
        var persistedEntries = await LoadPersistedManifestAsync(request.JobId);
        Assert.Single(
            persistedEntries,
            MoveManifestIdentity.IsSourceBoundaryAuthorization);
        Assert.Single(
            persistedEntries,
            MoveManifestIdentity.IsTargetBoundaryAuthorization);
        Assert.DoesNotContain(
            persistedEntries,
            entry => !MoveManifestIdentity.IsBoundaryAuthorization(entry));
    }

    private async Task<List<MoveJobEntry>> LoadPersistedManifestAsync(Guid jobId)
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.MoveJobEntries
            .AsNoTracking()
            .Where(entry => entry.MoveJobId == jobId)
            .OrderBy(entry => entry.Id)
            .ToListAsync();
    }

    [Fact]
    public async Task VerifyFinalizedMoveAsync_PhaseOnlyMarkerlessAtomicState_RequiresAttention()
    {
        var source = FileService.GetTempDirectory("content-move-phase-only-atomic-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            Path.GetDirectoryName(source)!,
            $"content-move-phase-only-atomic-dst-{Guid.NewGuid():N}");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        await ClearPersistedManifestAsync(request.JobId);
        Directory.Move(source, target);
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var job = await db.MoveJobs.SingleAsync(candidate => candidate.Id == request.JobId);
            job.Phase = MoveJobPhase.CleaningArtifacts;
            await db.SaveChangesAsync();
        }

        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.VerifyFinalizedMoveAsync(request, CancellationToken.None));

        Assert.Contains("requires a persisted manifest", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
    }

    [Fact]
    public async Task VerifyFinalizedMoveAsync_MarkerlessAtomicTarget_RemainsVerifiable()
    {
        var source = FileService.GetTempDirectory("content-move-atomic-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            Path.GetDirectoryName(source)!,
            $"content-move-atomic-dst-{Guid.NewGuid():N}");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        await service.MoveContentsAsync(request, CancellationToken.None);

        await service.VerifyFinalizedMoveAsync(request, CancellationToken.None);

        Assert.False(Directory.Exists(source));
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
    }

    [Fact]
    public async Task VerifyFinalizedMoveAsync_MarkerlessAtomicTargetWithUnownedContent_RequiresAttention()
    {
        var source = FileService.GetTempDirectory("content-move-atomic-tampered-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            Path.GetDirectoryName(source)!,
            $"content-move-atomic-tampered-dst-{Guid.NewGuid():N}");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        await service.MoveContentsAsync(request, CancellationToken.None);
        var unrelated = await FileService.GetFileAsync(
            target,
            "operator-note.txt",
            "preserve me");

        await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.VerifyFinalizedMoveAsync(request, CancellationToken.None));

        Assert.Equal("preserve me", await File.ReadAllTextAsync(unrelated));
    }
}
