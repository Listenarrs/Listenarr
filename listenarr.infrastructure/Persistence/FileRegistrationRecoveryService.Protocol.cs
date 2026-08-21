using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence;

public sealed partial class FileRegistrationRecoveryService
{
    private async Task EnsureCurrentRecoveryProtocolAsync(
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var unsupported = await db.FileMutationJournals
            .AsNoTracking()
            .Where(journal =>
                journal.ProtocolVersion != FileMutationProtocol.Current
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
                    journal.ProtocolVersion != FileMutationProtocol.Current
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
                    journal.ProtocolVersion != FileMutationProtocol.Current
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

        // Name every journal, not just the first. This disables filesystem mutations for the whole
        // application until an operator resolves them, and there is no in-app route to do that, so
        // this message is the entire brief they get. Reporting one at a time turns a single repair
        // into one restart per affected journal, with no way to know how many are left.
        const int listed = 10;
        var identifiers = string.Join(
            ", ",
            unsupported
                .Take(listed)
                .Select(journal => $"{journal.OperationId} ({journal.State})"));
        var remainder = unsupported.Count > listed
            ? $", and {unsupported.Count - listed} more"
            : string.Empty;

        throw new InvalidOperationException(
            $"{unsupported.Count} file-mutation journal(s) use a legacy recovery protocol and require "
            + $"operator repair before filesystem mutations can resume: {identifiers}{remainder}. "
            + "Each was interrupted before this build's durable parent-directory generation fencing "
            + "existed and cannot be resumed automatically.");
    }
}
