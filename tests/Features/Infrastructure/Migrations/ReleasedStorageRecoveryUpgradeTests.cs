using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Migrations;

[Trait("Name", "ReleasedStorageRecoveryUpgradeTests")]
[Trait("Category", "Integration")]
public sealed class ReleasedStorageRecoveryUpgradeTests : BaseTests
{
    public static IEnumerable<object[]> ReleasedStorageStates()
    {
        foreach (var protocol in new[] { 1, 2 })
            foreach (var status in new[] { MoveJobStatus.Queued, MoveJobStatus.Running,
            MoveJobStatus.RetryScheduled, MoveJobStatus.NeedsAttention,
            MoveJobStatus.Completed, MoveJobStatus.Failed })
                foreach (var ownership in new[] { LibraryDirectoryOwnershipState.Owned,
            LibraryDirectoryOwnershipState.Unavailable, LibraryDirectoryOwnershipState.Removing })
                {
                    yield return [protocol, status, ownership];
                }
    }

    [Theory]
    [MemberData(nameof(ReleasedStorageStates))]
    public async Task Released134Storage_UpgradeAndRepeatedReconciliation_PreservesArtifactsAndScopesLegacyJob(
        int protocol, MoveJobStatus status, LibraryDirectoryOwnershipState ownershipState)
    {
        // Seed before the actual released migration frontier; this bypasses the
        // current-model test host deliberately, rather than simulating an upgrade.
        var path = Path.Join(Path.GetTempPath(), $"released-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(path, "source"));
        Directory.CreateDirectory(Path.Join(path, "target"));
        var scenario = new ReleasedStorageRecoveryScenarioBuilder(
            path, protocol, status, ownershipState).Build();
        var source = scenario.Audiobook.FilePath!;
        var target = Path.Join(path, "target", "book.m4b");
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={Path.Join(path, "listenarr.db")};Pooling=False").Options;
        var factory = new UpgradeFactory(options);
        try
        {
            await File.WriteAllTextAsync(source, "audio");
            await File.WriteAllTextAsync(target, "audio");
            await using (var legacy = await factory.CreateDbContextAsync())
            {
                var migrator = legacy.GetService<IMigrator>();
                await migrator.MigrateAsync("20260825021432_AddWeakStorageVerifiedCleanup");
                legacy.RootFolders.Add(scenario.Root);
                legacy.Audiobooks.Add(scenario.Audiobook);
                legacy.LibraryDirectoryOwnerships.Add(scenario.Ownership);
                legacy.MoveJobs.Add(scenario.Job);
                await legacy.SaveChangesAsync();
                await migrator.MigrateAsync();
            }
            using var coordinator = new FilesystemMutationCoordinator();
            var semantics = new FileSystemSemanticsResolver();
            for (var restart = 0; restart < 2; restart++)
            {
                await new EfMoveQueuePersistence(factory, semantics).ReconcileIdentityKeysAsync();
                await new LibraryDirectoryOwnershipReconciler(factory,
                    new LibraryDirectoryOwnershipBoundaryAuthorizer(factory), coordinator,
                    NullLogger<LibraryDirectoryOwnershipReconciler>.Instance).ReconcileAsync();

                await using var db = await factory.CreateDbContextAsync();
                var job = await db.MoveJobs.AsNoTracking().Include(candidate => candidate.Entries)
                    .SingleAsync();
                Assert.Equal(status.IsActive() ? MoveJobStatus.NeedsAttention : status, job.Status);
                Assert.Equal(protocol, job.ExecutionProtocolVersion);
                Assert.Equal(scenario.Job.SourcePath, job.SourcePath);
                Assert.Equal(scenario.Job.RequestedPath, job.RequestedPath);
                Assert.Equal("released-other-client-source", job.SourceDirectoryObjectIdentity);
                Assert.Equal(MoveJobEntryCleanupState.DeleteAuthorized, Assert.Single(job.Entries).CleanupState);
                if (status.IsActive())
                {
                    Assert.Null(job.ActiveDeduplicationKey);
                    Assert.Null(job.LeaseOwner);
                    Assert.Null(job.LeaseExpiresAt);
                }
                var ownership = await db.LibraryDirectoryOwnerships.AsNoTracking().SingleAsync();
                Assert.Equal(ownershipState == LibraryDirectoryOwnershipState.Unavailable
                    ? LibraryDirectoryOwnershipState.Owned : ownershipState, ownership.State);
                Assert.Null(ownership.DirectoryObjectIdentityUnavailableReason);
                Assert.Equal("released-other-client-directory", ownership.DirectoryObjectIdentity);
                Assert.Equal(scenario.Ownership.PathOwnershipKey, ownership.PathOwnershipKey);
                var root = await db.RootFolders.AsNoTracking().SingleAsync();
                Assert.Equal("released-other-client-root", root.DirectoryObjectIdentity);
                var health = await new RootFolderStorageHealthResolver(
                    new DirectoryObjectIdentityResolver(), semantics,
                    readOnlyFileSystemProbe: _ => false).ResolveAsync(root);
                Assert.True(health.CanReadFilesystem);
                Assert.True(health.CanMutateFilesystem);
                Assert.False(health.CanConfirmCurrentFolder);
                Assert.Equal("audio", await File.ReadAllTextAsync(source));
                Assert.Equal("audio", await File.ReadAllTextAsync(target));
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            }
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class UpgradeFactory(DbContextOptions<ListenArrDbContext> options)
        : IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() => new(options);
        public Task<ListenArrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new ListenArrDbContext(options));
    }
}
