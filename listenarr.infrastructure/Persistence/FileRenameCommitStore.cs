using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence;

/// <summary>
/// Commits tracked audiobook path changes and the terminal state of their
/// owner-bound rename journals through the same scoped DbContext.
/// </summary>
public sealed class FileRenameCommitStore(
    ListenArrDbContext dbContext,
    TimeProvider timeProvider) : IFileRenameCommitStore
{
    public async Task CommitOwnerMetadataAsync(
        int audiobookId,
        IReadOnlyCollection<Guid> operationIds,
        CancellationToken cancellationToken = default)
    {
        if (audiobookId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audiobookId));
        }
        ArgumentNullException.ThrowIfNull(operationIds);
        var distinctIds = operationIds
            .Where(operationId => operationId != Guid.Empty)
            .Distinct()
            .ToArray();
        if (distinctIds.Length != operationIds.Count)
        {
            throw new InvalidOperationException(
                "A rename commit contains an empty or duplicate file-mutation operation ID.");
        }

        var journals = new List<FileMutationJournal>();
        var originalJournalState = new Dictionary<Guid, (FileMutationJournalState State, string? Error, DateTime UpdatedAt)>();
        if (distinctIds.Length > 0)
        {
            journals = await dbContext.FileMutationJournals
                .Where(journal => distinctIds.Contains(journal.OperationId))
                .ToListAsync(cancellationToken);
            if (journals.Count != distinctIds.Length)
            {
                throw new InvalidOperationException(
                    "One or more owner-bound rename journals are missing before metadata commit.");
            }

            foreach (var journal in journals)
            {
                if (journal.Action != FileAction.Move
                    || journal.AudiobookId != audiobookId
                    || !journal.AudiobookFileId.HasValue)
                {
                    throw new InvalidOperationException(
                        "A rename journal is not a move bound to the audiobook whose metadata is being committed.");
                }
                if (journal.State != FileMutationJournalState.Completed)
                {
                    throw new InvalidOperationException(
                        "A rename journal has not completed its filesystem mutation before metadata commit.");
                }
                if (string.IsNullOrWhiteSpace(journal.TargetPhysicalObjectIdentity))
                {
                    throw new InvalidOperationException(
                        "A completed rename journal has no persisted target physical generation.");
                }

                originalJournalState[journal.OperationId] =
                    (journal.State, journal.Error, journal.UpdatedAt);
                journal.State = FileMutationJournalState.OwnerMetadataReconciled;
                journal.Error = null;
                journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
            }
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            foreach (var journal in journals)
            {
                var original = originalJournalState[journal.OperationId];
                journal.State = original.State;
                journal.Error = original.Error;
                journal.UpdatedAt = original.UpdatedAt;
            }
            throw;
        }
    }
}
