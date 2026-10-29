using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence;

public sealed partial class FileRegistrationRecoveryService
{
    private async Task EnsureCurrentRecoveryProtocolAsync(
        CancellationToken cancellationToken,
        Guid? operationId = null)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var unsupportedQuery = db.FileMutationJournals
            .AsNoTracking()
            .Where(RegistrationPublicationPredicate)
            .Where(journal =>
                (journal.ProtocolVersion <= 0
                    || journal.ProtocolVersion > FileMutationProtocol.Current)
                && journal.State != FileMutationJournalState.Completed
                && journal.State
                    != FileMutationJournalState.CompletedSourceRetained
                && journal.State != FileMutationJournalState.RolledBack
                && journal.State != FileMutationJournalState.OwnerMetadataReconciled
                && journal.State != FileMutationJournalState.NeedsAttention);
        if (operationId is Guid scopedOperationId)
        {
            unsupportedQuery = unsupportedQuery.Where(
                journal => journal.OperationId == scopedOperationId);
        }
        var unsupported = await unsupportedQuery
            .OrderBy(journal => journal.CreatedAt)
            .ThenBy(journal => journal.OperationId)
            .Select(journal => new
            {
                journal.OperationId,
                journal.State
            })
            .ToListAsync(cancellationToken);
        if (unsupported.Count == 0)
        {
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        const string reason =
            "This file publication uses an unsupported operation-evidence protocol. Its artifacts were preserved and only this operation requires repair.";
        if (!db.Database.IsRelational())
        {
            var trackedQuery = db.FileMutationJournals
                .Where(RegistrationPublicationPredicate)
                .Where(journal =>
                    (journal.ProtocolVersion <= 0
                        || journal.ProtocolVersion > FileMutationProtocol.Current)
                    && journal.State != FileMutationJournalState.Completed
                    && journal.State
                        != FileMutationJournalState.CompletedSourceRetained
                    && journal.State != FileMutationJournalState.RolledBack
                    && journal.State != FileMutationJournalState.OwnerMetadataReconciled
                    && journal.State != FileMutationJournalState.NeedsAttention);
            if (operationId is Guid trackedOperationId)
            {
                trackedQuery = trackedQuery.Where(
                    journal => journal.OperationId == trackedOperationId);
            }
            var tracked = await trackedQuery
                .ToListAsync(cancellationToken);
            var parked = tracked.Select(journal => (journal.OperationId, journal.State)).ToList();
            foreach (var journal in tracked)
            {
                journal.State = FileMutationJournalState.NeedsAttention;
                journal.Error = reason;
                journal.UpdatedAt = now;
            }
            await db.SaveChangesAsync(cancellationToken);
            foreach (var (parkedOperationId, reachedState) in parked)
            {
                LogParked(parkedOperationId, reachedState, reason);
            }
        }
        else
        {
            var trackedQuery = db.FileMutationJournals
                .Where(RegistrationPublicationPredicate)
                .Where(journal =>
                    (journal.ProtocolVersion <= 0
                        || journal.ProtocolVersion > FileMutationProtocol.Current)
                    && journal.State != FileMutationJournalState.Completed
                    && journal.State
                        != FileMutationJournalState.CompletedSourceRetained
                    && journal.State != FileMutationJournalState.RolledBack
                    && journal.State != FileMutationJournalState.OwnerMetadataReconciled
                    && journal.State != FileMutationJournalState.NeedsAttention);
            if (operationId is Guid relationalOperationId)
            {
                trackedQuery = trackedQuery.Where(
                    journal => journal.OperationId == relationalOperationId);
            }
            foreach (var candidate in unsupported)
            {
                var affected = await trackedQuery
                    .Where(journal => journal.OperationId == candidate.OperationId
                        && journal.State == candidate.State)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(
                                journal => journal.State,
                                FileMutationJournalState.NeedsAttention)
                            .SetProperty(journal => journal.Error, reason)
                            .SetProperty(journal => journal.UpdatedAt, now),
                        cancellationToken);
                if (affected == 1)
                {
                    LogParked(candidate.OperationId, candidate.State, reason);
                }
            }
        }

        // Protocol ambiguity is operation-scoped. Startup recovery records
        // NeedsAttention for those rows and continues reconciling unrelated work.
    }
}
