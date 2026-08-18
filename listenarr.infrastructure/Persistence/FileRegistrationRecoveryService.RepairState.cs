using Listenarr.Application.Common.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence;

public sealed partial class FileRegistrationRecoveryService
{
    private async Task<bool> TryMarkNeedsAttentionAsync(
        Guid operationId,
        FileMutationJournalState expectedState,
        string reason,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var observedState = await db.FileMutationJournals
            .AsNoTracking()
            .Where(candidate => candidate.OperationId == operationId)
            .Select(candidate => candidate.State)
            .SingleAsync(cancellationToken);
        if (observedState == FileMutationJournalState.Completed)
        {
            return false;
        }
        if (observedState == FileMutationJournalState.NeedsAttention)
        {
            return true;
        }
        if (observedState != expectedState)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (db.Database.IsRelational())
        {
            var affected = await db.FileMutationJournals
                .Where(candidate => candidate.OperationId == operationId
                    && candidate.State == expectedState)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(
                            candidate => candidate.State,
                            FileMutationJournalState.NeedsAttention)
                        .SetProperty(candidate => candidate.Error, reason)
                        .SetProperty(candidate => candidate.UpdatedAt, now),
                    cancellationToken);
            if (affected == 1)
            {
                return true;
            }

            var currentState = await db.FileMutationJournals
                .AsNoTracking()
                .Where(candidate => candidate.OperationId == operationId)
                .Select(candidate => candidate.State)
                .SingleAsync(cancellationToken);
            return currentState == FileMutationJournalState.NeedsAttention;
        }

        var journal = await db.FileMutationJournals
            .SingleAsync(candidate => candidate.OperationId == operationId, cancellationToken);
        if (journal.State != expectedState)
        {
            return journal.State == FileMutationJournalState.NeedsAttention;
        }

        journal.State = FileMutationJournalState.NeedsAttention;
        journal.Error = reason;
        journal.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task ThrowIfNeedsAttentionAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var state = await db.FileMutationJournals
            .AsNoTracking()
            .Where(journal => journal.OperationId == operationId)
            .Select(journal => journal.State)
            .SingleAsync(cancellationToken);
        if (state == FileMutationJournalState.NeedsAttention)
        {
            throw RepairRequired(operationId);
        }
    }

    private static ApplicationConflictException RecoveryPending(Guid operationId) =>
        new(
            "registration_recovery_pending",
            $"A previously committed file import ({operationId}) is still retiring its original source file. Retry after the source file is no longer in use.");

    private static ApplicationConflictException RepairRequired(Guid operationId) =>
        new(
            "registration_recovery_repair_required",
            $"A previously committed file import ({operationId}) requires repair before this audiobook's files can be changed.");
}
