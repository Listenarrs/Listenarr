using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence;

public sealed partial class FileRenameRecoveryReconciler
{
    private async Task EnsureCurrentOwnerRecoveryProtocolAsync(
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var unsupported = await db.FileMutationJournals
            .AsNoTracking()
            .Where(journal =>
                journal.Action == FileAction.Move
                && journal.AudiobookId != null
                && journal.AudiobookFileId != null
                && journal.ProtocolVersion != FileMutationProtocol.Current
                && journal.State != FileMutationJournalState.Completed
                && journal.State != FileMutationJournalState.OwnerMetadataReconciled)
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
            var tracked = await db.FileMutationJournals
                .Where(journal =>
                    journal.Action == FileAction.Move
                    && journal.AudiobookId != null
                    && journal.AudiobookFileId != null
                    && journal.ProtocolVersion != FileMutationProtocol.Current
                    && journal.State != FileMutationJournalState.Completed
                    && journal.State != FileMutationJournalState.OwnerMetadataReconciled
                    && journal.State != FileMutationJournalState.NeedsAttention)
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
            await db.FileMutationJournals
                .Where(journal =>
                    journal.Action == FileAction.Move
                    && journal.AudiobookId != null
                    && journal.AudiobookFileId != null
                    && journal.ProtocolVersion != FileMutationProtocol.Current
                    && journal.State != FileMutationJournalState.Completed
                    && journal.State != FileMutationJournalState.OwnerMetadataReconciled
                    && journal.State != FileMutationJournalState.NeedsAttention)
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
            $"Owner-bound file-mutation journal {unsupported[0].OperationId} uses legacy recovery protocol state {unsupported[0].State} and requires operator repair before filesystem mutations can resume.");
    }
}
