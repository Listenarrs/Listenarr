using Listenarr.Domain.Audiobooks.Enumerations;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private async Task<bool> CompleteLivePinnedMarkerlessMoveAsync(
        string source,
        string destination,
        FileMutationJournal journal,
        MarkerlessRegistrationPublicationLease registrationLease,
        CancellationToken cancellationToken)
    {
        var publicationMatch = registrationLease.ProbeCurrentPublication();
        if (publicationMatch != RegistrationPublicationMatchOutcome.Match)
        {
            return false;
        }

        using var gate = await TryAcquireFileMoveGateAsync(
            source,
            destination,
            allowExistingAliasForRecovery: true);
        if (gate == null || !await JournalPathsMatchGateAsync(journal, gate))
        {
            return false;
        }

        if (!await MarkerlessRegistrationTargetMatchesAsync(
                gate,
                journal,
                cancellationToken,
                requirePhysicalIdentity: false))
        {
            await MarkMarkerlessRegistrationNeedsAttentionAsync(
                journal,
                "The registered destination content changed before live source retirement.",
                cancellationToken);
            return false;
        }

        if (journal.State == FileMutationJournalState.RegistrationCommitted)
        {
            journal = await _fileMutationJournalStore!.AdvanceAsync(
                journal.OperationId,
                FileMutationJournalState.SourceDeletionAuthorized,
                targetPhysicalObjectIdentity: null,
                journal.AudiobookId,
                error: null,
                cancellationToken);
        }

        if (journal.State == FileMutationJournalState.SourceDeletionAuthorized)
        {
            var liveSource = registrationLease.SourceEntry;
            if (!liveSource.VisiblePathMatches()
                || !await MatchesMarkerlessContentAsync(
                    liveSource,
                    journal.SourceLength,
                    journal.SourceSha256,
                    cancellationToken))
            {
                await MarkMarkerlessRegistrationNeedsAttentionAsync(
                    journal,
                    "The live pinned move source changed before retirement.",
                    cancellationToken);
                return false;
            }

            if (BeforeMarkerlessRegistrationSourceDeleteForTestAsync != null)
            {
                await BeforeMarkerlessRegistrationSourceDeleteForTestAsync();
            }

            if (registrationLease.ProbeCurrentPublication()
                    != RegistrationPublicationMatchOutcome.Match
                || !await MarkerlessRegistrationTargetMatchesAsync(
                    gate,
                    journal,
                    cancellationToken,
                    requirePhysicalIdentity: false))
            {
                await MarkMarkerlessRegistrationNeedsAttentionAsync(
                    journal,
                    "The registered destination changed immediately before live source retirement.",
                    cancellationToken);
                return false;
            }

            if (!liveSource.VisiblePathMatches()
                || !await MatchesMarkerlessContentAsync(
                    liveSource,
                    journal.SourceLength,
                    journal.SourceSha256,
                    cancellationToken))
            {
                await MarkMarkerlessRegistrationNeedsAttentionAsync(
                    journal,
                    "The live pinned move source changed immediately before retirement.",
                    cancellationToken);
                return false;
            }

            // This is the only destructive step. It is authorized by the exact
            // process-local source handle captured before publication, never by
            // persisted object identity.
            liveSource.Delete(immediateWindows: true);
            gate.SourceParent.FlushDirectoryEntry();

            if (AfterMarkerlessMoveSourceDeletedBeforeStateForTestAsync != null)
            {
                await AfterMarkerlessMoveSourceDeletedBeforeStateForTestAsync();
            }

            // On Windows the directory entry may remain delete-pending while the
            // exact source handle is open. The destructive action has already been
            // performed, so release only that process-local authority before
            // proving the source pathname absent and committing completion.
            registrationLease.ReleaseSourceAuthority();

            journal = await _fileMutationJournalStore!.AdvanceAsync(
                journal.OperationId,
                FileMutationJournalState.SourceDeleted,
                targetPhysicalObjectIdentity: null,
                journal.AudiobookId,
                error: null,
                cancellationToken);

            if (AfterMarkerlessMoveSourceDeletedStateForTestAsync != null)
            {
                await AfterMarkerlessMoveSourceDeletedStateForTestAsync();
            }
        }

        if (journal.State is not (
                FileMutationJournalState.SourceDeleted
                or FileMutationJournalState.Completed))
        {
            return false;
        }

        var completionValidation =
            await _fileMutationJournalStore!.AdvanceWithCommitValidationAsync(
                journal.OperationId,
                FileMutationJournalState.Completed,
                targetPhysicalObjectIdentity: null,
                journal.AudiobookId,
                error: null,
                async validationToken =>
                {
                    if (BeforeMarkerlessCompletedJournalCommitForTestAsync != null)
                    {
                        await BeforeMarkerlessCompletedJournalCommitForTestAsync();
                    }

                    var moveValidation = await ProbeMarkerlessMoveCompletionAsync(
                        gate,
                        journal,
                        validationToken,
                        requirePersistedParentIdentity: false,
                        requireTargetPhysicalIdentity: false);
                    if (moveValidation
                        != RegistrationPublicationMatchOutcome.Match)
                    {
                        return moveValidation;
                    }

                    return registrationLease.ProbeCurrentPublication();
                },
                cancellationToken);
        if (completionValidation != RegistrationPublicationMatchOutcome.Match)
        {
            if (completionValidation
                == RegistrationPublicationMatchOutcome.Mismatch)
            {
                await MarkMarkerlessRegistrationNeedsAttentionAsync(
                    journal,
                    "The live move target or source namespace changed before completion committed.",
                    cancellationToken);
            }

            return false;
        }

        LogMutation(
            FileMutationOutcome.Success,
            FileAction.Move,
            source,
            destination,
            "Retired the exact live-pinned registration source");
        return true;
    }
}
