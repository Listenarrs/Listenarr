using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Listenarr.Tests.Features.Infrastructure.Migrations;

[Trait("Name", "ReleasedRootRelocationUpgradeTests")]
[Trait("Category", "Integration")]
public sealed class ReleasedRootRelocationUpgradeTests : BaseTests
{
    [Theory]
    [InlineData(1, MoveJobStatus.Queued)]
    [InlineData(1, MoveJobStatus.Running)]
    [InlineData(1, MoveJobStatus.RetryScheduled)]
    [InlineData(2, MoveJobStatus.Queued)]
    [InlineData(2, MoveJobStatus.Running)]
    [InlineData(2, MoveJobStatus.RetryScheduled)]
    public async Task Released134ActiveRelocation_UpgradeScopesLegacyMoveAndPreservesBothRoots(
        int protocol, MoveJobStatus status)
    {
        // The released SQLite schema, rather than the current-model test host,
        // is the input to this upgrade and repeated startup reconciliation.
        var path = Path.Join(Path.GetTempPath(), $"released-relocation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(path, "source"));
        Directory.CreateDirectory(Path.Join(path, "target"));
        var scenario = new ReleasedStorageRecoveryScenarioBuilder(
            path, protocol, status, LibraryDirectoryOwnershipState.Owned).Build();
        var relocation = new ReleasedRootRelocationScenarioBuilder(scenario).Build();
        var source = scenario.Audiobook.FilePath!;
        var target = Path.Join(relocation.TargetPath, "book.m4b");
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
                legacy.RootFolderRelocations.Add(relocation);
                await legacy.SaveChangesAsync();
                await migrator.MigrateAsync();
            }
            using var mutationCoordinator = new FilesystemMutationCoordinator();
            using var audiobookCoordinator = new AudiobookOperationCoordinator();
            var manifest = new Mock<IMoveSourceManifestService>(MockBehavior.Strict);
            using var services = new ServiceCollection().AddScoped(_ => manifest.Object)
                .BuildServiceProvider(new ServiceProviderOptions
                {
                    ValidateScopes = true,
                    ValidateOnBuild = true
                });
            for (var restart = 0; restart < 2; restart++)
            {
                var semantics = new FileSystemSemanticsResolver();
                await new EfMoveQueuePersistence(factory, semantics).ReconcileIdentityKeysAsync();
                var service = new RootFolderRelocationService(factory, semantics,
                    new NoopHubBroadcaster(), TimeProvider.System, mutationCoordinator,
                    audiobookCoordinator, services.GetRequiredService<IServiceScopeFactory>(),
                    TestLibraryFilesystemReadiness.Ready());
                await service.ReconcileActiveAsync();

                await using var db = await factory.CreateDbContextAsync();
                var recovered = await db.RootFolderRelocations.AsNoTracking().SingleAsync();
                Assert.Equal(RootFolderRelocationStatus.NeedsAttention, recovered.Status);
                Assert.Equal(scenario.Root.Id, recovered.ActiveRootFolderId);
                Assert.Equal("released-other-client-relocation-target",
                    recovered.TargetDirectoryObjectIdentity);
                var job = await db.MoveJobs.AsNoTracking().SingleAsync();
                Assert.Equal(MoveJobStatus.NeedsAttention, job.Status);
                Assert.Equal(protocol, job.ExecutionProtocolVersion);
                Assert.Null(job.ActiveDeduplicationKey);
                Assert.Equal(path, (await db.RootFolders.AsNoTracking().SingleAsync()).Path);
                Assert.Equal(source, (await db.AudiobookFiles.AsNoTracking().SingleAsync()).Path);
                Assert.Equal("audio", await File.ReadAllTextAsync(source));
                Assert.Equal("audio", await File.ReadAllTextAsync(target));
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            }
            manifest.VerifyNoOtherCalls();
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
