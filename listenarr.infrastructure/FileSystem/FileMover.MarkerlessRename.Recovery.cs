namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private async Task<bool> ReconcileInterruptedMarkerlessRenameAsync(
        FileMutationJournal journal,
        FileMoveGateLease pathLock,
        CancellationToken cancellationToken)
    {
        if (journal.State is FileMutationJournalState.NeedsAttention
            or FileMutationJournalState.RolledBack
            or FileMutationJournalState.CompletedSourceRetained)
        {
            return false;
        }
        if (journal.State == FileMutationJournalState.OwnerMetadataReconciled)
        {
            return await OwnerMetadataReconciledTargetMatchesAsync(
                pathLock, journal, cancellationToken);
        }

        // Restart has lost the original rename pin. Only observe an already
        // published target; never rename or remove a surviving source from a journal.
        var outcome = await ProbeMarkerlessMoveCompletionAsync(
            pathLock, journal, cancellationToken,
            requirePersistedParentIdentity: false,
            requireTargetPhysicalIdentity: false);
        if (outcome == RegistrationPublicationMatchOutcome.Unavailable)
        {
            return false;
        }
        if (outcome != RegistrationPublicationMatchOutcome.Match)
        {
            await MarkMarkerlessRenameNeedsAttentionAsync(
                journal,
                "Interrupted rename retained its artifacts because a surviving source or unmatched target cannot restore live rename authority.",
                cancellationToken);
            return false;
        }
        if (journal.State == FileMutationJournalState.Completed)
        {
            return true;
        }

        outcome = await _fileMutationJournalStore!.AdvanceWithCommitValidationAsync(
            journal.OperationId,
            FileMutationJournalState.Completed,
            journal.TargetPhysicalObjectIdentity ?? journal.SourcePhysicalObjectIdentity,
            audiobookId: null,
            error: null,
            token => ProbeMarkerlessMoveCompletionAsync(
                pathLock, journal, token,
                requirePersistedParentIdentity: false,
                requireTargetPhysicalIdentity: false),
            cancellationToken);
        return outcome == RegistrationPublicationMatchOutcome.Match;
    }
}
