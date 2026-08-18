using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class EfFileMutationJournalStore
{
    private static FileMutationJournalMutableState CaptureMutableState(
        FileMutationJournal journal) =>
        new(
            journal.State,
            journal.TargetPhysicalObjectIdentity,
            journal.AudiobookId,
            journal.Error,
            journal.UpdatedAt);

    private static async Task RestoreMutableStateAsync(
        ListenArrDbContext db,
        Guid operationId,
        FileMutationJournalMutableState expected,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var journal = await db.FileMutationJournals.SingleAsync(
            candidate => candidate.OperationId == operationId,
            cancellationToken);
        RestoreMutableState(journal, expected);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void RestoreMutableState(
        ListenArrDbContext db,
        Guid operationId,
        FileMutationJournalMutableState expected)
    {
        db.ChangeTracker.Clear();
        var journal = db.FileMutationJournals.Single(
            candidate => candidate.OperationId == operationId);
        RestoreMutableState(journal, expected);
        db.SaveChanges();
    }

    private static void RestoreMutableState(
        FileMutationJournal journal,
        FileMutationJournalMutableState expected)
    {
        journal.State = expected.State;
        journal.TargetPhysicalObjectIdentity = expected.TargetPhysicalObjectIdentity;
        journal.AudiobookId = expected.AudiobookId;
        journal.Error = expected.Error;
        journal.UpdatedAt = expected.UpdatedAt;
    }

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
        string? Error,
        DateTime UpdatedAt);

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
        if (journal.ProtocolVersion != FileMutationProtocol.Current)
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
