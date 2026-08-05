using System.Data.Common;
using Microsoft.EntityFrameworkCore;

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

[Trait("Name", "EfMoveExecutionStoreTests")]
[Trait("Category", "Infrastructure")]
public sealed class EfMoveExecutionStoreTests : BaseTests
{
    [Fact]
    public async Task SourceManifestOperations_ExcludeTargetBoundaryAuthorization()
    {
        var jobId = Guid.NewGuid();
        var lease = new MoveLeaseToken("worker", 1);
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.MoveJobs.Add(new MoveJob
            {
                Id = jobId,
                AudiobookId = 1,
                RequestedPath = Path.Join(FileService.GetTempPath(), "target"),
                SourcePath = Path.Join(FileService.GetTempPath(), "source"),
                Status = MoveJobStatus.Running,
                LeaseOwner = lease.Owner,
                LeaseGeneration = lease.Generation,
                LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
                ActiveDeduplicationKey = $"test:{jobId:N}",
                Entries =
                [
                    new MoveJobEntry
                    {
                        RelativePath = "book.m4b",
                        EntryType = MoveJobEntryType.File,
                        Length = 5,
                        Sha256 = new string('A', 64)
                    },
                    MoveManifestIdentity.CreateTargetBoundaryAuthorization(
                        2,
                        "test-target-generation")
                ]
            });
            await db.SaveChangesAsync();
        }

        var store = new EfMoveExecutionStore(factory, TimeProvider.System);
        var manifest = await store.LoadManifestAsync(jobId, CancellationToken.None);

        var sourceEntry = Assert.Single(manifest);
        Assert.Equal("book.m4b", sourceEntry.RelativePath);

        await store.UpdateCopyStateAsync(jobId, lease, CancellationToken.None);

        await using var verification = await factory.CreateDbContextAsync();
        var entries = await verification.MoveJobEntries
            .AsNoTracking()
            .Where(entry => entry.MoveJobId == jobId)
            .ToListAsync();
        Assert.Equal(
            MoveJobEntryCopyState.Verified,
            entries.Single(entry => entry.RelativePath == "book.m4b").CopyState);
        Assert.Equal(
            MoveJobEntryCopyState.Pending,
            entries.Single(MoveManifestIdentity.IsTargetBoundaryAuthorization).CopyState);
    }

    [Fact]
    public async Task ProviderFailures_AreTranslatedAcrossMoveExecutionBoundary()
    {
        var store = new EfMoveExecutionStore(
            new ThrowingDbContextFactory(),
            TimeProvider.System);
        var jobId = Guid.NewGuid();
        var lease = new MoveLeaseToken("worker", 1);
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        var source = Path.GetFullPath(Path.Join(Path.GetTempPath(), "move-store-source"));
        var target = Path.GetFullPath(Path.Join(Path.GetTempPath(), "move-store-target"));
        var operations = new Func<Task>[]
        {
            () => store.EnsureLeaseOwnedAsync(jobId, lease, CancellationToken.None),
            () => store.ValidateOrAdoptIdentityAsync(
                jobId,
                source,
                target,
                semantics,
                semantics,
                lease,
                hasFilesystemRecoveryArtifacts: false,
                CancellationToken.None),
            () => store.EnsureMutationAuthorizedAsync(
                jobId,
                lease,
                source,
                target,
                semantics,
                semantics,
                CancellationToken.None),
            async () => _ = await store.LoadManifestAsync(jobId, CancellationToken.None),
            () => store.UpdateCleanupStateAsync(
                jobId,
                lease,
                "book.m4b",
                MoveJobEntryCleanupState.Deleted,
                CancellationToken.None),
            () => store.UpdateCopyStateAsync(jobId, lease, CancellationToken.None),
            () => store.UpdateJobPhaseAsync(
                jobId,
                lease,
                MoveJobPhase.Published,
                CancellationToken.None),
            async () => _ = await store.GetCreatedDirectoriesAsync(jobId, CancellationToken.None),
            () => store.PersistCreatedDirectoriesAsync(
                jobId,
                lease,
                [Path.Join(target, "parent")],
                CancellationToken.None),
            () => store.UpdateCreatedDirectoryStateAsync(
                jobId,
                lease,
                Path.Join(target, "parent"),
                MoveCreatedDirectoryState.Created,
                CancellationToken.None)
        };

        foreach (var operation in operations)
        {
            var exception = await Assert.ThrowsAsync<PersistenceException>(operation);
            Assert.IsType<SimulatedProviderException>(exception.InnerException);
        }
    }

    private sealed class ThrowingDbContextFactory : IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() =>
            throw new SimulatedProviderException();

        public Task<ListenArrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromException<ListenArrDbContext>(new SimulatedProviderException());
    }

    private sealed class SimulatedProviderException : DbException;
}
