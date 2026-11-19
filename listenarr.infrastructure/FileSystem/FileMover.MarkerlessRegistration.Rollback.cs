using Listenarr.Domain.Audiobooks.Enumerations;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    public async Task<UncommittedPublicationRollbackOutcome>
        RollbackUncommittedRegistrationAsync(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A registration rollback requires a non-empty operation ID.",
                nameof(operationId));
        }
        if (_fileMutationJournalStore == null)
        {
            return UncommittedPublicationRollbackOutcome.Pending;
        }

        var cancellationToken = CancellationToken.None;
        var journal = await _fileMutationJournalStore.GetAsync(
            operationId,
            cancellationToken);
        if (journal == null)
        {
            return UncommittedPublicationRollbackOutcome.Pending;
        }
        if (FileMutationJournalLifecycle.IsRegistrationPublicationTerminal(
                journal.State))
        {
            return UncommittedPublicationRollbackOutcome.AlreadyTerminal;
        }
        if (journal.AudiobookId.HasValue
            || journal.AudiobookFileId.HasValue
            || journal.Action is not (
                FileAction.Move or FileAction.Copy or FileAction.HardlinkCopy)
            || journal.State is not (
                FileMutationJournalState.Planned
                or FileMutationJournalState.TargetIdentityPersisted
                or FileMutationJournalState.TargetVerified
                or FileMutationJournalState.RollbackAuthorized))
        {
            return journal.AudiobookId.HasValue
                ? UncommittedPublicationRollbackOutcome.OwnershipCommitted
                : UncommittedPublicationRollbackOutcome.Pending;
        }

        using var gate = await TryAcquireFileMoveGateAsync(
            journal.SourcePath,
            journal.DestinationPath,
            allowExistingAliasForRecovery: true);
        if (gate == null)
        {
            return UncommittedPublicationRollbackOutcome.Pending;
        }
        if (!await JournalPathsMatchGateAsync(journal, gate))
        {
            await MarkMarkerlessRegistrationNeedsAttentionAsync(
                journal,
                "The uncommitted registration paths no longer match their durable journal.",
                cancellationToken);
            return UncommittedPublicationRollbackOutcome.NeedsAttention;
        }
        var targetOutcome =
            gate.DestinationParent.TryOpenExistingFileWithOutcome(
                gate.DestinationName,
                requireDeleteAccess: false,
                out var targetEntry);
        using (targetEntry)
        {
            if (targetOutcome == PinnedFileOpenOutcome.Unavailable)
            {
                return UncommittedPublicationRollbackOutcome.Pending;
            }

            if (targetOutcome == PinnedFileOpenOutcome.NotFound)
            {
                try
                {
                    await _fileMutationJournalStore.AdvanceAsync(
                        journal.OperationId,
                        FileMutationJournalState.RolledBack,
                        journal.TargetPhysicalObjectIdentity,
                        audiobookId: null,
                        error: "The uncommitted publication target is already absent; restart recovery performed no destructive cleanup.",
                        cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    var current = await _fileMutationJournalStore.GetAsync(
                        journal.OperationId,
                        cancellationToken);
                    if (current?.AudiobookId.HasValue == true)
                    {
                        return UncommittedPublicationRollbackOutcome.OwnershipCommitted;
                    }
                    if (current != null
                        && FileMutationJournalLifecycle
                            .ClearsRegistrationRecoveryBoundary(current.State))
                    {
                        return UncommittedPublicationRollbackOutcome.AlreadyTerminal;
                    }
                    if (current?.State == FileMutationJournalState.NeedsAttention)
                    {
                        return UncommittedPublicationRollbackOutcome.NeedsAttention;
                    }
                    throw;
                }

                return UncommittedPublicationRollbackOutcome.RolledBack;
            }

            // This API is invoked only after a process boundary. Persisted path,
            // hash, parent-generation, or physical-identity evidence can describe
            // the interrupted publication, but none of it recreates authority to
            // unlink a surviving target. Preserve the target for scoped repair.
            await MarkMarkerlessRegistrationNeedsAttentionAsync(
                journal,
                "An unowned publication target still exists after restart. It was preserved because restart recovery cannot recreate delete authority.",
                cancellationToken);
            return UncommittedPublicationRollbackOutcome.NeedsAttention;
        }
    }
}
