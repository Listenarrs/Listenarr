using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Listenarr.Infrastructure.Persistence;

/// <summary>
/// Commits tracked audiobook path changes and the terminal state of their
/// owner-bound rename journals through the same scoped DbContext.
/// </summary>
public sealed class FileRenameCommitStore(
    ListenArrDbContext dbContext,
    TimeProvider timeProvider) : IFileRenameCommitStore
{
    internal Action? AfterSaveBeforeTargetRevalidationForTest { get; set; }

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
        var targetLeases = new List<PinnedAudiobookFileRegistrationLease>();
        var originalJournalState = new Dictionary<Guid, (FileMutationJournalState State, string? Error, DateTime UpdatedAt)>();
        IDbContextTransaction? ownedTransaction = null;
        try
        {
            if (dbContext.Database.IsRelational())
            {
                if (dbContext.Database.CurrentTransaction != null)
                {
                    throw new InvalidOperationException(
                        "Rename owner-metadata commit must own its database transaction so filesystem generation proof cannot outlive the commit boundary.");
                }

                ownedTransaction = await dbContext.Database.BeginTransactionAsync(
                    cancellationToken);
            }

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
                    var targetLease = PinnedAudiobookFileRegistrationLease.Open(
                        journal.DestinationPath,
                        journal.TargetPhysicalObjectIdentity);
                    targetLeases.Add(targetLease);
                    if (targetLease.ProbeCurrentPublication()
                        != RegistrationPublicationMatchOutcome.Match)
                    {
                        throw new InvalidOperationException(
                            "A completed rename target is not currently the journaled physical generation.");
                    }

                    journal.State = FileMutationJournalState.OwnerMetadataReconciled;
                    journal.Error = null;
                    journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
                }
            }

            EnsureTargetsStillMatch(targetLeases);
            await dbContext.SaveChangesAsync(cancellationToken);
            AfterSaveBeforeTargetRevalidationForTest?.Invoke();
            EnsureTargetsStillMatch(targetLeases);
            if (ownedTransaction != null)
            {
                await ownedTransaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            if (ownedTransaction != null)
            {
                await ownedTransaction.RollbackAsync(CancellationToken.None);
            }
            foreach (var journal in journals)
            {
                if (!originalJournalState.TryGetValue(journal.OperationId, out var original))
                {
                    continue;
                }

                journal.State = original.State;
                journal.Error = original.Error;
                journal.UpdatedAt = original.UpdatedAt;
            }
            throw;
        }
        finally
        {
            if (ownedTransaction != null)
            {
                await ownedTransaction.DisposeAsync();
            }
            foreach (var targetLease in targetLeases)
            {
                targetLease.Dispose();
            }
        }
    }

    private static void EnsureTargetsStillMatch(
        IReadOnlyCollection<PinnedAudiobookFileRegistrationLease> targetLeases)
    {
        foreach (var targetLease in targetLeases)
        {
            var match = targetLease.ProbeCurrentPublication();
            if (match == RegistrationPublicationMatchOutcome.Unavailable)
            {
                throw new IOException(
                    "A completed rename target is temporarily unavailable during owner-metadata commit.");
            }
            if (match != RegistrationPublicationMatchOutcome.Match)
            {
                throw new InvalidOperationException(
                    "A completed rename target changed before owner metadata could be committed.");
            }
        }
    }
}
