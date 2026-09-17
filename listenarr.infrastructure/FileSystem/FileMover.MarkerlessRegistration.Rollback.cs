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
        if (!JournalParentGenerationsMatchGate(journal, gate))
        {
            await MarkMarkerlessRegistrationNeedsAttentionAsync(
                journal,
                "An uncommitted registration parent directory changed physical generation.",
                cancellationToken);
            return UncommittedPublicationRollbackOutcome.NeedsAttention;
        }

        var sourceOutcome = gate.SourceParent.TryOpenExistingFileWithOutcome(
            gate.SourceName,
            requireDeleteAccess: false,
            out var sourceEntry);
        using (sourceEntry)
        {
            if (sourceOutcome == PinnedFileOpenOutcome.Unavailable)
            {
                return UncommittedPublicationRollbackOutcome.Pending;
            }

            var targetOutcome =
                gate.DestinationParent.TryOpenExistingFileForStableDeleteWithOutcome(
                    gate.DestinationName,
                    out var targetEntry);
            using (targetEntry)
            {
                if (targetOutcome == PinnedFileOpenOutcome.Unavailable)
                {
                    return UncommittedPublicationRollbackOutcome.Pending;
                }
                if (sourceOutcome != PinnedFileOpenOutcome.Opened
                    || !await MatchesMarkerlessSourceProofAsync(
                        sourceEntry!,
                        journal,
                        cancellationToken))
                {
                    await MarkMarkerlessRegistrationNeedsAttentionAsync(
                        journal,
                        "The uncommitted registration source is missing or no longer matches its durable proof; the target was preserved.",
                        cancellationToken);
                    return UncommittedPublicationRollbackOutcome.NeedsAttention;
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
                            error: "The uncommitted registration target was already absent; the exact source remains intact.",
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
                if (journal.State == FileMutationJournalState.Planned)
                {
                    await MarkMarkerlessRegistrationNeedsAttentionAsync(
                        journal,
                        "A target exists but this planned registration never persisted authority over its generation; it was preserved.",
                        cancellationToken);
                    return UncommittedPublicationRollbackOutcome.NeedsAttention;
                }
                if (!TargetMatchesMarkerlessJournal(targetEntry!, journal)
                    || (journal.State == FileMutationJournalState.TargetVerified
                        && !await MatchesMarkerlessTargetContentAsync(
                            targetEntry!,
                            journal,
                            cancellationToken)))
                {
                    await MarkMarkerlessRegistrationNeedsAttentionAsync(
                        journal,
                        "The uncommitted registration target changed generation or content; it was preserved.",
                        cancellationToken);
                    return UncommittedPublicationRollbackOutcome.NeedsAttention;
                }

                if (journal.State is FileMutationJournalState.TargetIdentityPersisted
                    or FileMutationJournalState.TargetVerified)
                {
                    try
                    {
                        journal = await _fileMutationJournalStore.AdvanceAsync(
                            journal.OperationId,
                            FileMutationJournalState.RollbackAuthorized,
                            journal.TargetPhysicalObjectIdentity,
                            audiobookId: null,
                            error: "Startup recovery proved an exact source and unowned target and authorized compensation.",
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
                }
                if (journal.State != FileMutationJournalState.RollbackAuthorized
                    || journal.AudiobookId.HasValue)
                {
                    return journal.AudiobookId.HasValue
                        ? UncommittedPublicationRollbackOutcome.OwnershipCommitted
                        : UncommittedPublicationRollbackOutcome.Pending;
                }

                targetEntry!.Delete(immediateWindows: true);
                gate.DestinationParent.FlushDirectoryEntry();
                var sourceVisibility = sourceEntry!.ProbeVisiblePathMatch();
                if (sourceVisibility == RegistrationPublicationMatchOutcome.Unavailable)
                {
                    return UncommittedPublicationRollbackOutcome.Pending;
                }
                if (sourceVisibility == RegistrationPublicationMatchOutcome.Mismatch)
                {
                    await MarkMarkerlessRegistrationNeedsAttentionAsync(
                        journal,
                        "The registration source changed after exact target compensation.",
                        cancellationToken);
                    return UncommittedPublicationRollbackOutcome.NeedsAttention;
                }

                await _fileMutationJournalStore.AdvanceAsync(
                    journal.OperationId,
                    FileMutationJournalState.RolledBack,
                    journal.TargetPhysicalObjectIdentity,
                    audiobookId: null,
                    error: "Startup recovery removed the exact unowned publication target and retained its source.",
                    cancellationToken);
                return UncommittedPublicationRollbackOutcome.RolledBack;
            }
        }
    }
}
