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
            .Where(journal =>
                journal.ProtocolVersion != FileMutationProtocol.Current
                && journal.State != FileMutationJournalState.Completed
                && journal.State != FileMutationJournalState.RolledBack
                && journal.State != FileMutationJournalState.OwnerMetadataReconciled);
        if (operationId.HasValue)
        {
            unsupportedQuery = unsupportedQuery.Where(
                journal => journal.OperationId == operationId.Value);
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
            "This interrupted file mutation predates durable parent-directory generation fencing and cannot be resumed automatically.";
        if (!db.Database.IsRelational())
        {
            var trackedQuery = db.FileMutationJournals
                .Where(journal =>
                    journal.ProtocolVersion != FileMutationProtocol.Current
                    && journal.State != FileMutationJournalState.Completed
                    && journal.State != FileMutationJournalState.RolledBack
                    && journal.State != FileMutationJournalState.OwnerMetadataReconciled
                    && journal.State != FileMutationJournalState.NeedsAttention);
            if (operationId.HasValue)
            {
                trackedQuery = trackedQuery.Where(
                    journal => journal.OperationId == operationId.Value);
            }
            var tracked = await trackedQuery
                .ToListAsync(cancellationToken);
            foreach (var journal in tracked)
            {
                journal.State = FileMutationJournalState.NeedsAttention;
                journal.Error = reason;
                journal.UpdatedAt = now;
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            var trackedQuery = db.FileMutationJournals
                .Where(journal =>
                    journal.ProtocolVersion != FileMutationProtocol.Current
                    && journal.State != FileMutationJournalState.Completed
                    && journal.State != FileMutationJournalState.RolledBack
                    && journal.State != FileMutationJournalState.OwnerMetadataReconciled
                    && journal.State != FileMutationJournalState.NeedsAttention);
            if (operationId.HasValue)
            {
                trackedQuery = trackedQuery.Where(
                    journal => journal.OperationId == operationId.Value);
            }
            await trackedQuery
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(
                            journal => journal.State,
                            FileMutationJournalState.NeedsAttention)
                        .SetProperty(journal => journal.Error, reason)
                        .SetProperty(journal => journal.UpdatedAt, now),
                    cancellationToken);
        }

        throw new InvalidOperationException(
            $"File-mutation journal {unsupported[0].OperationId} uses legacy recovery protocol state {unsupported[0].State} and requires operator repair before filesystem mutations can resume.");
    }
}
