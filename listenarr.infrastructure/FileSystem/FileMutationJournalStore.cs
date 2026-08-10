using Listenarr.Domain.Audiobooks.Enumerations;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed record FileMutationJournalClaim(
    Guid OperationId,
    FileAction Action,
    string SourcePath,
    string DestinationPath,
    string SourcePhysicalObjectIdentity,
    long SourceLength,
    string? SourceSha256,
    int? AudiobookId = null,
    int? AudiobookFileId = null);

internal interface IFileMutationJournalStore
{
    Task<FileMutationJournal> GetOrCreateAsync(
        FileMutationJournalClaim claim,
        CancellationToken cancellationToken);

    Task<FileMutationJournal?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken);

    Task<FileMutationJournal> SetSourceSha256Async(
        Guid operationId,
        string expectedSourcePhysicalObjectIdentity,
        long expectedSourceLength,
        string sourceSha256,
        CancellationToken cancellationToken);

    FileMutationJournal? Get(Guid operationId);

    Task<FileMutationJournal> AdvanceAsync(
        Guid operationId,
        FileMutationJournalState state,
        string? targetPhysicalObjectIdentity,
        int? audiobookId,
        string? error,
        CancellationToken cancellationToken);

    FileMutationJournal Advance(
        Guid operationId,
        FileMutationJournalState state,
        string? targetPhysicalObjectIdentity,
        int? audiobookId,
        string? error);
}

internal sealed partial class EfFileMutationJournalStore(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    TimeProvider timeProvider,
    IFileSystemSemanticsResolver? semanticsResolver = null) : IFileMutationJournalStore
{
    private readonly IFileSystemSemanticsResolver _semanticsResolver =
        semanticsResolver ?? new FileSystemSemanticsResolver();

    internal Func<Task>? AfterAdvanceLoadedForTestAsync { get; set; }

    public async Task<FileMutationJournal> GetOrCreateAsync(
        FileMutationJournalClaim claim,
        CancellationToken cancellationToken)
    {
        ValidateClaim(claim);
        var canonicalSource = Path.GetFullPath(claim.SourcePath);
        var canonicalDestination = Path.GetFullPath(claim.DestinationPath);
        await using var db =
            await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.FileMutationJournals
            .SingleOrDefaultAsync(
                journal => journal.OperationId == claim.OperationId,
                cancellationToken);
        if (existing != null)
        {
            await ValidateIdentityAsync(
                existing,
                claim,
                canonicalSource,
                canonicalDestination,
                cancellationToken);
            return existing;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var journal = new FileMutationJournal
        {
            OperationId = claim.OperationId,
            ProtocolVersion = FileMutationProtocol.MarkerlessDatabaseState,
            Action = claim.Action,
            SourcePath = canonicalSource,
            DestinationPath = canonicalDestination,
            SourcePhysicalObjectIdentity = claim.SourcePhysicalObjectIdentity,
            SourceLength = claim.SourceLength,
            SourceSha256 = claim.SourceSha256,
            AudiobookId = claim.AudiobookId,
            AudiobookFileId = claim.AudiobookFileId,
            State = FileMutationJournalState.Planned,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.FileMutationJournals.Add(journal);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return journal;
        }
        catch (UniqueConstraintViolationException)
        {
            db.Entry(journal).State = EntityState.Detached;
            existing = await db.FileMutationJournals
                .SingleAsync(
                    candidate => candidate.OperationId == claim.OperationId,
                    cancellationToken);
            await ValidateIdentityAsync(
                existing,
                claim,
                canonicalSource,
                canonicalDestination,
                cancellationToken);
            return existing;
        }
    }

    public async Task<FileMutationJournal?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A file-mutation operation ID must not be empty.",
                nameof(operationId));
        }

        await using var db =
            await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.FileMutationJournals
            .AsNoTracking()
            .SingleOrDefaultAsync(
                journal => journal.OperationId == operationId,
                cancellationToken);
    }

    public FileMutationJournal? Get(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A file-mutation operation ID must not be empty.",
                nameof(operationId));
        }

        using var db = dbContextFactory.CreateDbContext();
        return db.FileMutationJournals
            .AsNoTracking()
            .SingleOrDefault(journal => journal.OperationId == operationId);
    }

    public async Task<FileMutationJournal> AdvanceAsync(
        Guid operationId,
        FileMutationJournalState state,
        string? targetPhysicalObjectIdentity,
        int? audiobookId,
        string? error,
        CancellationToken cancellationToken)
    {
        ValidateAdvanceRequest(
            operationId,
            state,
            targetPhysicalObjectIdentity,
            audiobookId);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var db =
                await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var journal = await db.FileMutationJournals
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.OperationId == operationId,
                    cancellationToken)
                ?? throw new InvalidOperationException(
                    "The durable file-mutation journal does not exist.");
            var expected = CaptureMutableState(journal);
            ApplyAdvance(
                journal,
                state,
                targetPhysicalObjectIdentity,
                audiobookId,
                error);
            if (AfterAdvanceLoadedForTestAsync != null)
            {
                await AfterAdvanceLoadedForTestAsync();
            }

            if (await TryPersistAdvanceAsync(
                    db,
                    journal,
                    expected,
                    cancellationToken))
            {
                return journal;
            }
        }

        throw new InvalidOperationException(
            "The file-mutation journal changed concurrently too many times.");
    }

    public FileMutationJournal Advance(
        Guid operationId,
        FileMutationJournalState state,
        string? targetPhysicalObjectIdentity,
        int? audiobookId,
        string? error)
    {
        ValidateAdvanceRequest(
            operationId,
            state,
            targetPhysicalObjectIdentity,
            audiobookId);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var db = dbContextFactory.CreateDbContext();
            var journal = db.FileMutationJournals
                .AsNoTracking()
                .SingleOrDefault(candidate => candidate.OperationId == operationId)
                ?? throw new InvalidOperationException(
                    "The durable file-mutation journal does not exist.");
            var expected = CaptureMutableState(journal);
            ApplyAdvance(
                journal,
                state,
                targetPhysicalObjectIdentity,
                audiobookId,
                error);
            if (TryPersistAdvance(db, journal, expected))
            {
                return journal;
            }
        }

        throw new InvalidOperationException(
            "The file-mutation journal changed concurrently too many times.");
    }

    private static FileMutationJournalMutableState CaptureMutableState(
        FileMutationJournal journal) =>
        new(
            journal.State,
            journal.TargetPhysicalObjectIdentity,
            journal.AudiobookId,
            journal.Error);

    private static async Task<bool> TryPersistAdvanceAsync(
        ListenArrDbContext db,
        FileMutationJournal journal,
        FileMutationJournalMutableState expected,
        CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational())
        {
            db.FileMutationJournals.Update(journal);
            return await db.SaveChangesAsync(cancellationToken) == 1;
        }

        var affected = await db.FileMutationJournals
            .Where(candidate => candidate.OperationId == journal.OperationId
                && candidate.State == expected.State
                && candidate.TargetPhysicalObjectIdentity
                    == expected.TargetPhysicalObjectIdentity
                && candidate.AudiobookId == expected.AudiobookId
                && candidate.Error == expected.Error)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(
                        candidate => candidate.State,
                        journal.State)
                    .SetProperty(
                        candidate => candidate.TargetPhysicalObjectIdentity,
                        journal.TargetPhysicalObjectIdentity)
                    .SetProperty(
                        candidate => candidate.AudiobookId,
                        journal.AudiobookId)
                    .SetProperty(
                        candidate => candidate.Error,
                        journal.Error)
                    .SetProperty(
                        candidate => candidate.UpdatedAt,
                        journal.UpdatedAt),
                cancellationToken);
        return affected == 1;
    }

    private static bool TryPersistAdvance(
        ListenArrDbContext db,
        FileMutationJournal journal,
        FileMutationJournalMutableState expected)
    {
        if (!db.Database.IsRelational())
        {
            db.FileMutationJournals.Update(journal);
            return db.SaveChanges() == 1;
        }

        var affected = db.FileMutationJournals
            .Where(candidate => candidate.OperationId == journal.OperationId
                && candidate.State == expected.State
                && candidate.TargetPhysicalObjectIdentity
                    == expected.TargetPhysicalObjectIdentity
                && candidate.AudiobookId == expected.AudiobookId
                && candidate.Error == expected.Error)
            .ExecuteUpdate(setters => setters
                .SetProperty(
                    candidate => candidate.State,
                    journal.State)
                .SetProperty(
                    candidate => candidate.TargetPhysicalObjectIdentity,
                    journal.TargetPhysicalObjectIdentity)
                .SetProperty(
                    candidate => candidate.AudiobookId,
                    journal.AudiobookId)
                .SetProperty(
                    candidate => candidate.Error,
                    journal.Error)
                .SetProperty(
                    candidate => candidate.UpdatedAt,
                    journal.UpdatedAt));
        return affected == 1;
    }

    private sealed record FileMutationJournalMutableState(
        FileMutationJournalState State,
        string? TargetPhysicalObjectIdentity,
        int? AudiobookId,
        string? Error);

    private static void ValidateAdvanceRequest(
        Guid operationId,
        FileMutationJournalState state,
        string? targetPhysicalObjectIdentity,
        int? audiobookId)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A file-mutation operation ID must not be empty.",
                nameof(operationId));
        }
        if (state == FileMutationJournalState.OwnerMetadataReconciled)
        {
            throw new InvalidOperationException(
                "Owner metadata reconciliation must be committed atomically with the owning audiobook metadata, not through the filesystem journal store.");
        }
        if (state >= FileMutationJournalState.TargetIdentityPersisted
            && state != FileMutationJournalState.NeedsAttention
            && string.IsNullOrWhiteSpace(targetPhysicalObjectIdentity))
        {
            throw new ArgumentException(
                "A persisted target generation is required for this file-mutation state.",
                nameof(targetPhysicalObjectIdentity));
        }
        if (audiobookId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audiobookId));
        }
    }

    private void ApplyAdvance(
        FileMutationJournal journal,
        FileMutationJournalState state,
        string? targetPhysicalObjectIdentity,
        int? audiobookId,
        string? error)
    {
        if (journal.ProtocolVersion
            != FileMutationProtocol.MarkerlessDatabaseState)
        {
            throw new InvalidOperationException(
                "The durable file-mutation journal uses an unsupported protocol.");
        }
        if (journal.State == FileMutationJournalState.OwnerMetadataReconciled)
        {
            throw new InvalidOperationException(
                "A file mutation whose owner metadata is reconciled is terminal and cannot be advanced.");
        }
        if (journal.State == FileMutationJournalState.NeedsAttention
            && state != FileMutationJournalState.NeedsAttention)
        {
            throw new InvalidOperationException(
                "A file mutation requiring attention cannot resume automatically.");
        }
        if (state != FileMutationJournalState.NeedsAttention
            && state < journal.State)
        {
            throw new InvalidOperationException(
                "A file-mutation state transition would regress durable state.");
        }
        if (!string.IsNullOrWhiteSpace(journal.TargetPhysicalObjectIdentity)
            && !string.IsNullOrWhiteSpace(targetPhysicalObjectIdentity)
            && !string.Equals(
                journal.TargetPhysicalObjectIdentity,
                targetPhysicalObjectIdentity,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The file-mutation target changed physical generation.");
        }
        if (journal.AudiobookId.HasValue
            && audiobookId.HasValue
            && journal.AudiobookId != audiobookId)
        {
            throw new InvalidOperationException(
                "The file-mutation registration owner changed.");
        }

        journal.TargetPhysicalObjectIdentity ??=
            targetPhysicalObjectIdentity;
        journal.AudiobookId ??= audiobookId;
        if (state > journal.State
            || state == FileMutationJournalState.NeedsAttention)
        {
            journal.State = state;
        }
        journal.Error = error;
        journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
    }

}
