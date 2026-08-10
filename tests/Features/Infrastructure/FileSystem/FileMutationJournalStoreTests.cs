using Microsoft.EntityFrameworkCore;

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "FileMutationJournalStoreTests")]
[Trait("Category", "Infrastructure")]
public sealed class FileMutationJournalStoreTests : BaseTests
{
    [Fact]
    public async Task GetOrCreateAsync_ExactRetryReturnsExistingJournal()
    {
        var operationId = Guid.NewGuid();
        var claim = CreateClaim(operationId);
        var store = CreateStore();

        var created = await store.GetOrCreateAsync(
            claim,
            CancellationToken.None);
        var retried = await store.GetOrCreateAsync(
            claim,
            CancellationToken.None);

        Assert.Equal(operationId, created.OperationId);
        Assert.Equal(FileMutationJournalState.Planned, created.State);
        Assert.Equal(created.OperationId, retried.OperationId);
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(1, await db.FileMutationJournals.CountAsync());
    }

    [WindowsFact]
    public async Task GetOrCreateAsync_CaseAliasRetryReturnsExistingJournal()
    {
        var operationId = Guid.NewGuid();
        var claim = CreateClaim(operationId);
        var store = CreateStore();
        var created = await store.GetOrCreateAsync(
            claim,
            CancellationToken.None);

        var retried = await store.GetOrCreateAsync(
            claim with
            {
                SourcePath = claim.SourcePath.ToUpperInvariant(),
                DestinationPath = claim.DestinationPath.ToUpperInvariant()
            },
            CancellationToken.None);

        Assert.Equal(created.OperationId, retried.OperationId);
        Assert.Equal(created.SourcePath, retried.SourcePath);
        Assert.Equal(created.DestinationPath, retried.DestinationPath);
    }

    [LinuxFact]
    public async Task GetOrCreateAsync_CaseDistinctRetryFailsClosed()
    {
        var operationId = Guid.NewGuid();
        var claim = CreateClaim(operationId);
        var store = CreateStore();
        await store.GetOrCreateAsync(claim, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.GetOrCreateAsync(
                claim with
                {
                    SourcePath = claim.SourcePath.ToUpperInvariant()
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task GetOrCreateAsync_ReusedOperationForDifferentIdentityFailsClosed()
    {
        var operationId = Guid.NewGuid();
        var claim = CreateClaim(operationId);
        var store = CreateStore();
        await store.GetOrCreateAsync(claim, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.GetOrCreateAsync(
                claim with
                {
                    DestinationPath = Path.Join(
                        FileService.GetTempPath(),
                        "different-destination.m4b")
                },
                CancellationToken.None));

        Assert.Contains(
            "another file-mutation identity",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdvanceAsync_IsMonotonicAndGenerationBound()
    {
        var operationId = Guid.NewGuid();
        var claim = CreateClaim(operationId);
        var store = CreateStore();
        await store.GetOrCreateAsync(claim, CancellationToken.None);

        var persisted = await store.AdvanceAsync(
            operationId,
            FileMutationJournalState.TargetIdentityPersisted,
            "target-generation",
            audiobookId: null,
            error: null,
            CancellationToken.None);
        Assert.Equal(
            FileMutationJournalState.TargetIdentityPersisted,
            persisted.State);
        Assert.Equal(
            "target-generation",
            persisted.TargetPhysicalObjectIdentity);

        persisted = await store.AdvanceAsync(
            operationId,
            FileMutationJournalState.TargetVerified,
            "target-generation",
            audiobookId: 42,
            error: null,
            CancellationToken.None);
        Assert.Equal(FileMutationJournalState.TargetVerified, persisted.State);
        Assert.Equal(42, persisted.AudiobookId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AdvanceAsync(
                operationId,
                FileMutationJournalState.Planned,
                "target-generation",
                audiobookId: 42,
                error: null,
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AdvanceAsync(
                operationId,
                FileMutationJournalState.TargetVerified,
                "replacement-generation",
                audiobookId: 42,
                error: null,
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AdvanceAsync(
                operationId,
                FileMutationJournalState.TargetVerified,
                "target-generation",
                audiobookId: 43,
                error: null,
                CancellationToken.None));
    }

    [Fact]
    public async Task AdvanceAsync_StaleWriterCannotRegressConcurrentHigherState()
    {
        var databasePath = Path.Join(
            FileService.GetTempPath(),
            $"file-mutation-cas-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        var factory = new TestDbContextFactory(options);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var operationId = Guid.NewGuid();
        var claim = CreateClaim(operationId);
        var setupStore = CreateStore(factory);
        await setupStore.GetOrCreateAsync(claim, CancellationToken.None);
        await setupStore.AdvanceAsync(
            operationId,
            FileMutationJournalState.TargetVerified,
            "target-generation",
            audiobookId: null,
            error: null,
            CancellationToken.None);

        var staleWriterLoaded = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStaleWriter = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var staleStore = CreateStore(factory);
        staleStore.AfterAdvanceLoadedForTestAsync = async () =>
        {
            staleWriterLoaded.TrySetResult();
            await releaseStaleWriter.Task;
        };
        var staleAdvance = staleStore.AdvanceAsync(
            operationId,
            FileMutationJournalState.RegistrationCommitted,
            "target-generation",
            audiobookId: 42,
            error: null,
            CancellationToken.None);
        await staleWriterLoaded.Task;

        var currentStore = CreateStore(factory);
        var current = await currentStore.AdvanceAsync(
            operationId,
            FileMutationJournalState.SourceDeletionAuthorized,
            "target-generation",
            audiobookId: 42,
            error: null,
            CancellationToken.None);
        Assert.Equal(
            FileMutationJournalState.SourceDeletionAuthorized,
            current.State);

        releaseStaleWriter.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await staleAdvance);

        var persisted = await currentStore.GetAsync(
            operationId,
            CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal(
            FileMutationJournalState.SourceDeletionAuthorized,
            persisted.State);
        Assert.Equal(42, persisted.AudiobookId);
    }

    [Fact]
    public async Task AdvanceAsync_OwnerMetadataReconciledCannotBeWrittenOrReopenedByFilesystemStore()
    {
        var operationId = Guid.NewGuid();
        var store = CreateStore();
        var claim = CreateClaim(operationId) with
        {
            AudiobookId = 42,
            AudiobookFileId = 420
        };
        await store.GetOrCreateAsync(claim, CancellationToken.None);
        await store.AdvanceAsync(
            operationId,
            FileMutationJournalState.TargetIdentityPersisted,
            "target-generation",
            audiobookId: null,
            error: null,
            CancellationToken.None);
        await store.AdvanceAsync(
            operationId,
            FileMutationJournalState.Completed,
            "target-generation",
            audiobookId: null,
            error: null,
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AdvanceAsync(
                operationId,
                FileMutationJournalState.OwnerMetadataReconciled,
                "target-generation",
                audiobookId: 42,
                error: null,
                CancellationToken.None));

        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var journal = await db.FileMutationJournals
                .SingleAsync(candidate => candidate.OperationId == operationId);
            journal.State = FileMutationJournalState.OwnerMetadataReconciled;
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AdvanceAsync(
                operationId,
                FileMutationJournalState.NeedsAttention,
                "target-generation",
                audiobookId: 42,
                error: "late source reuse",
                CancellationToken.None));
    }

    [Fact]
    public async Task AdvanceAsync_NeedsAttentionIsTerminal()
    {
        var operationId = Guid.NewGuid();
        var store = CreateStore();
        await store.GetOrCreateAsync(
            CreateClaim(operationId),
            CancellationToken.None);
        var blocked = await store.AdvanceAsync(
            operationId,
            FileMutationJournalState.NeedsAttention,
            targetPhysicalObjectIdentity: null,
            audiobookId: null,
            error: "unproven final file",
            CancellationToken.None);

        Assert.Equal(FileMutationJournalState.NeedsAttention, blocked.State);
        Assert.Equal("unproven final file", blocked.Error);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AdvanceAsync(
                operationId,
                FileMutationJournalState.TargetIdentityPersisted,
                "target-generation",
                audiobookId: null,
                error: null,
                CancellationToken.None));
    }

    private EfFileMutationJournalStore CreateStore() =>
        CreateStore(_provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>());

    private static EfFileMutationJournalStore CreateStore(
        IDbContextFactory<ListenArrDbContext> factory) =>
        new(factory, TimeProvider.System);

    private FileMutationJournalClaim CreateClaim(Guid operationId) =>
        new(
            operationId,
            FileAction.Move,
            Path.Join(FileService.GetTempPath(), "source.m4b"),
            Path.Join(FileService.GetTempPath(), "destination.m4b"),
            "source-generation",
            SourceLength: 123,
            SourceSha256: new string('A', 64));

    private sealed class TestDbContextFactory(
        DbContextOptions<ListenArrDbContext> options) :
        IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() => new(options);

        public Task<ListenArrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
