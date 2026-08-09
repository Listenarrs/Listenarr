using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

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
    public async Task UpdateTargetEntryStateAsync_LeaseReplacedAfterLoad_CannotPersistStaleState()
    {
        var databasePath = Path.Join(
            FileService.GetTempPath(),
            $"move-execution-lease-race-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={databasePath};Foreign Keys=False")
            .Options;
        var factory = new TestDbContextFactory(options);
        var jobId = Guid.NewGuid();
        var originalLease = new MoveLeaseToken("worker-1", 1);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.MoveJobs.Add(new MoveJob
            {
                Id = jobId,
                AudiobookId = 1,
                RequestedPath = Path.Join(FileService.GetTempPath(), "target"),
                SourcePath = Path.Join(FileService.GetTempPath(), "source"),
                Status = MoveJobStatus.Running,
                LeaseOwner = originalLease.Owner,
                LeaseGeneration = originalLease.Generation,
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
                    }
                ]
            });
            await db.SaveChangesAsync();
        }

        var stateLoaded = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new EfMoveExecutionStore(factory, TimeProvider.System)
        {
            AfterMarkerlessStateLoadedForTestAsync = async () =>
            {
                stateLoaded.TrySetResult();
                await releaseWriter.Task;
            }
        };
        var staleUpdate = store.UpdateTargetEntryStateAsync(
            jobId,
            originalLease,
            "book.m4b",
            MoveJobEntryCopyState.Staged,
            "target-generation",
            CancellationToken.None);
        await stateLoaded.Task;

        await using (var replacement = await factory.CreateDbContextAsync())
        {
            var job = await replacement.MoveJobs.SingleAsync(
                candidate => candidate.Id == jobId);
            job.LeaseOwner = "worker-2";
            job.LeaseGeneration = 2;
            job.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5);
            await replacement.SaveChangesAsync();
        }

        releaseWriter.TrySetResult();
        await Assert.ThrowsAsync<MoveLeaseLostException>(async () =>
            await staleUpdate);

        await using var verification = await factory.CreateDbContextAsync();
        var entry = await verification.MoveJobEntries
            .AsNoTracking()
            .SingleAsync(candidate => candidate.MoveJobId == jobId);
        Assert.Equal(MoveJobEntryCopyState.Pending, entry.CopyState);
        Assert.Null(entry.TargetPhysicalObjectIdentity);
    }

    [Fact]
    public async Task EnsureMutationAuthorizedAsync_ConfiguredRootReturnedAfterTransientOutage_UsesAuthorizedNativeGeneration()
    {
        var databasePath = Path.Join(
            FileService.GetTempPath(),
            $"move-execution-returned-root-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={databasePath};Foreign Keys=False")
            .Options;
        var factory = new TestDbContextFactory(options);
        var jobId = Guid.NewGuid();
        var lease = new MoveLeaseToken("worker", 1);
        var source = FileService.GetTempDirectory("move-execution-returned-source");
        var target = FileService.GetTempDirectory("move-execution-returned-target");
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        string rootAuthorizedIdentity;
        using (var boundary = PinnedDirectoryCreation.OpenPinnedBoundary(target))
        {
            rootAuthorizedIdentity = ManagedDirectoryIdentity.Create(
                Guid.NewGuid().ToString("N"),
                boundary.GetDirectoryObjectIdentity());
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.RootFolders.Add(new RootFolder
            {
                Name = "Returned Root",
                Path = target,
                CaseSensitivityMode = semantics.CaseSensitivity
                    == FileSystemCaseSensitivity.Sensitive
                        ? FileSystemCaseSensitivityMode.Sensitive
                        : FileSystemCaseSensitivityMode.Insensitive,
                ResolvedCaseSensitivity = semantics.CaseSensitivity,
                PathIdentityState = PathIdentityState.Valid,
                PathIdentityKey = FileSystemPathIdentity.CreateKey("root", target, semantics),
                DirectoryObjectIdentityVersion = ManagedDirectoryIdentity.CurrentVersion,
                DirectoryObjectIdentity = rootAuthorizedIdentity,
                DirectoryObjectIdentityUnavailableReason =
                    "The directory was unavailable during startup."
            });
            db.MoveJobs.Add(new MoveJob
            {
                Id = jobId,
                AudiobookId = 1,
                RequestedPath = target,
                SourcePath = source,
                SourcePathSyntax = semantics.Syntax,
                SourceCaseSensitivity = semantics.CaseSensitivity,
                SourceCaseSensitivityMode = semantics.CaseSensitivity
                    == FileSystemCaseSensitivity.Sensitive
                        ? FileSystemCaseSensitivityMode.Sensitive
                        : FileSystemCaseSensitivityMode.Insensitive,
                SourceIdentityBoundary = source,
                TargetPathSyntax = semantics.Syntax,
                TargetCaseSensitivity = semantics.CaseSensitivity,
                TargetCaseSensitivityMode = semantics.CaseSensitivity
                    == FileSystemCaseSensitivity.Sensitive
                        ? FileSystemCaseSensitivityMode.Sensitive
                        : FileSystemCaseSensitivityMode.Insensitive,
                TargetIdentityBoundary = target,
                Status = MoveJobStatus.Running,
                LeaseOwner = lease.Owner,
                LeaseGeneration = lease.Generation,
                LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
                ActiveDeduplicationKey = $"test:{jobId:N}",
                Entries =
                [
                    MoveManifestIdentity.CreateTargetBoundaryAuthorization(
                        ManagedDirectoryIdentity.CurrentVersion,
                        rootAuthorizedIdentity)
                ]
            });
            await db.SaveChangesAsync();
        }

        var store = new EfMoveExecutionStore(factory, TimeProvider.System);

        await store.EnsureMutationAuthorizedAsync(
            jobId,
            lease,
            source,
            target,
            semantics,
            semantics,
            CancellationToken.None);
    }

    [Fact]
    public async Task EnsureMutationAuthorizedAsync_TargetFilesystemSemanticsChangedAfterEnqueue_FailsClosed()
    {
        var databasePath = Path.Join(
            FileService.GetTempPath(),
            $"move-execution-semantics-changed-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={databasePath};Foreign Keys=False")
            .Options;
        var factory = new TestDbContextFactory(options);
        var jobId = Guid.NewGuid();
        var lease = new MoveLeaseToken("worker", 1);
        var source = FileService.GetTempDirectory("move-execution-semantics-source");
        var target = FileService.GetTempDirectory("move-execution-semantics-target");
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        var opposite = new FileSystemPathSemantics(
            semantics.Syntax,
            semantics.CaseSensitivity == FileSystemCaseSensitivity.Sensitive
                ? FileSystemCaseSensitivity.Insensitive
                : FileSystemCaseSensitivity.Sensitive);
        string targetBoundaryIdentity;
        using (var boundary = PinnedDirectoryCreation.OpenPinnedBoundary(target))
        {
            targetBoundaryIdentity = ManagedDirectoryIdentity.CreateMarkerless(
                boundary.GetDirectoryObjectIdentity());
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.MoveJobs.Add(new MoveJob
            {
                Id = jobId,
                AudiobookId = 1,
                RequestedPath = target,
                SourcePath = source,
                SourcePathSyntax = semantics.Syntax,
                SourceCaseSensitivity = semantics.CaseSensitivity,
                SourceCaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                SourceIdentityBoundary = source,
                TargetPathSyntax = semantics.Syntax,
                TargetCaseSensitivity = semantics.CaseSensitivity,
                TargetCaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                TargetIdentityBoundary = target,
                Status = MoveJobStatus.Running,
                LeaseOwner = lease.Owner,
                LeaseGeneration = lease.Generation,
                LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
                ActiveDeduplicationKey = $"test:{jobId:N}",
                Entries =
                [
                    MoveManifestIdentity.CreateTargetBoundaryAuthorization(
                        ManagedDirectoryIdentity.CurrentVersion,
                        targetBoundaryIdentity)
                ]
            });
            await db.SaveChangesAsync();
        }

        var semanticsResolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        semanticsResolver
            .Setup(resolver => resolver.ResolveAsync(
                source,
                FileSystemCaseSensitivityMode.Auto,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileSystemSemanticsResolution(
                semantics,
                PathIdentityState.Valid,
                source));
        semanticsResolver
            .Setup(resolver => resolver.ResolveAsync(
                target,
                FileSystemCaseSensitivityMode.Auto,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileSystemSemanticsResolution(
                opposite,
                PathIdentityState.Valid,
                target));
        var store = new EfMoveExecutionStore(
            factory,
            TimeProvider.System,
            semanticsResolver.Object);

        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            store.EnsureMutationAuthorizedAsync(
                jobId,
                lease,
                source,
                target,
                semantics,
                semantics,
                CancellationToken.None));

        Assert.Contains("target filesystem semantics changed", exception.Message, StringComparison.OrdinalIgnoreCase);
        semanticsResolver.VerifyAll();
    }

    [Fact]
    public async Task EnsureMutationAuthorizedAsync_RowLimitedBoundaryProofQuery_IsDeterministicallyOrdered()
    {
        var databasePath = Path.Join(
            FileService.GetTempPath(),
            $"move-execution-ordered-boundary-proof-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={databasePath};Foreign Keys=False")
            .ConfigureWarnings(warnings => warnings.Throw(
                CoreEventId.RowLimitingOperationWithoutOrderByWarning))
            .Options;
        var factory = new TestDbContextFactory(options);
        var jobId = Guid.NewGuid();
        var lease = new MoveLeaseToken("worker", 1);
        var source = FileService.GetTempDirectory("move-execution-ordered-source");
        var target = FileService.GetTempDirectory("move-execution-ordered-target");
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        string targetBoundaryIdentity;
        using (var boundary = PinnedDirectoryCreation.OpenPinnedBoundary(target))
        {
            targetBoundaryIdentity = ManagedDirectoryIdentity.CreateMarkerless(
                boundary.GetDirectoryObjectIdentity());
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.MoveJobs.Add(new MoveJob
            {
                Id = jobId,
                AudiobookId = 1,
                RequestedPath = target,
                SourcePath = source,
                SourcePathSyntax = semantics.Syntax,
                SourceCaseSensitivity = semantics.CaseSensitivity,
                SourceCaseSensitivityMode = semantics.CaseSensitivity
                    == FileSystemCaseSensitivity.Sensitive
                        ? FileSystemCaseSensitivityMode.Sensitive
                        : FileSystemCaseSensitivityMode.Insensitive,
                SourceIdentityBoundary = source,
                TargetPathSyntax = semantics.Syntax,
                TargetCaseSensitivity = semantics.CaseSensitivity,
                TargetCaseSensitivityMode = semantics.CaseSensitivity
                    == FileSystemCaseSensitivity.Sensitive
                        ? FileSystemCaseSensitivityMode.Sensitive
                        : FileSystemCaseSensitivityMode.Insensitive,
                TargetIdentityBoundary = target,
                Status = MoveJobStatus.Running,
                LeaseOwner = lease.Owner,
                LeaseGeneration = lease.Generation,
                LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
                ActiveDeduplicationKey = $"test:{jobId:N}",
                Entries =
                [
                    MoveManifestIdentity.CreateTargetBoundaryAuthorization(
                        ManagedDirectoryIdentity.CurrentVersion,
                        targetBoundaryIdentity)
                ]
            });
            await db.SaveChangesAsync();
        }

        var store = new EfMoveExecutionStore(factory, TimeProvider.System);

        await store.EnsureMutationAuthorizedAsync(
            jobId,
            lease,
            source,
            target,
            semantics,
            semantics,
            CancellationToken.None);
    }

    [Fact]
    public async Task CleanupStateTransitions_AreMonotonicAndTerminal()
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
                RequestedPath = Path.Join(FileService.GetTempPath(), "cleanup-target"),
                SourcePath = Path.Join(FileService.GetTempPath(), "cleanup-source"),
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
                    }
                ]
            });
            await db.SaveChangesAsync();
        }

        var store = new EfMoveExecutionStore(factory, TimeProvider.System);
        await store.UpdateCleanupStateAsync(
            jobId,
            lease,
            "book.m4b",
            MoveJobEntryCleanupState.DeleteAuthorized,
            CancellationToken.None);
        await store.UpdateCleanupStateAsync(
            jobId,
            lease,
            "book.m4b",
            MoveJobEntryCleanupState.Deleted,
            CancellationToken.None);
        await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            store.UpdateCleanupStateAsync(
                jobId,
                lease,
                "book.m4b",
                MoveJobEntryCleanupState.Retained,
                CancellationToken.None));

        await store.UpdateSourceDirectoryCleanupStateAsync(
            jobId,
            lease,
            MoveJobEntryCleanupState.Retained,
            CancellationToken.None);
        await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            store.UpdateSourceDirectoryCleanupStateAsync(
                jobId,
                lease,
                MoveJobEntryCleanupState.DeleteAuthorized,
                CancellationToken.None));

        await using var verification = await factory.CreateDbContextAsync();
        var persisted = await verification.MoveJobs
            .Include(candidate => candidate.Entries)
            .SingleAsync(candidate => candidate.Id == jobId);
        Assert.Equal(
            MoveJobEntryCleanupState.Retained,
            persisted.SourceDirectoryCleanupState);
        Assert.Equal(
            MoveJobEntryCleanupState.Deleted,
            Assert.Single(persisted.Entries).CleanupState);
    }

    [Fact]
    public async Task CreatedDirectoryStateTransitions_PreserveTerminalRetainedState()
    {
        var jobId = Guid.NewGuid();
        var lease = new MoveLeaseToken("worker", 1);
        var path = Path.Join(FileService.GetTempPath(), $"created-directory-{Guid.NewGuid():N}");
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.MoveJobs.Add(new MoveJob
            {
                Id = jobId,
                AudiobookId = 1,
                RequestedPath = Path.Join(FileService.GetTempPath(), "created-target"),
                SourcePath = Path.Join(FileService.GetTempPath(), "created-source"),
                Status = MoveJobStatus.Running,
                LeaseOwner = lease.Owner,
                LeaseGeneration = lease.Generation,
                LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
                ActiveDeduplicationKey = $"test:{jobId:N}",
                CreatedDirectories =
                [
                    new MoveJobCreatedDirectory
                    {
                        Path = path,
                        State = MoveCreatedDirectoryState.Planned
                    }
                ]
            });
            await db.SaveChangesAsync();
        }

        var store = new EfMoveExecutionStore(factory, TimeProvider.System);
        await store.UpdateCreatedDirectoryPublicationAsync(
            jobId,
            lease,
            path,
            MoveCreatedDirectoryState.Created,
            "directory-generation",
            CancellationToken.None);
        await store.UpdateCreatedDirectoryStateAsync(
            jobId,
            lease,
            path,
            MoveCreatedDirectoryState.Retained,
            CancellationToken.None);
        await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            store.UpdateCreatedDirectoryStateAsync(
                jobId,
                lease,
                path,
                MoveCreatedDirectoryState.Removed,
                CancellationToken.None));

        await using var verification = await factory.CreateDbContextAsync();
        var persisted = await verification.MoveJobCreatedDirectories
            .SingleAsync(candidate => candidate.MoveJobId == jobId);
        Assert.Equal(MoveCreatedDirectoryState.Retained, persisted.State);
        Assert.Equal("directory-generation", persisted.DirectoryObjectIdentity);
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
            () => store.ValidateIdentityAsync(
                jobId,
                source,
                target,
                semantics,
                semantics,
                lease,
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

    private sealed class TestDbContextFactory(
        DbContextOptions<ListenArrDbContext> options) :
        IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() => new(options);

        public Task<ListenArrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
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
