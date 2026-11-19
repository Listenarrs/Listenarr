namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private readonly record struct MarkerlessSourceRetirementResult(
        FileMutationJournal Journal,
        bool CanComplete);

    private async Task<MarkerlessSourceRetirementResult>
        RetireMarkerlessMoveSourceAsync(
            FileMoveGateLease pathLock,
            FileMutationJournal journal,
            PinnedDirectoryCreation.PinnedFileEntry? liveSource,
            PinnedDirectoryCreation.PinnedFileEntry? liveTarget,
            CancellationToken cancellationToken)
    {
        if (journal.State >= FileMutationJournalState.SourceDeleted)
        {
            var completedSourceOutcome =
                pathLock.SourceParent.TryOpenExistingFileWithOutcome(
                    pathLock.SourceName,
                    requireDeleteAccess: false,
                    out var recreatedSource);
            using (recreatedSource)
            {
                if (completedSourceOutcome == PinnedFileOpenOutcome.Unavailable)
                {
                    return new(journal, CanComplete: false);
                }
                if (completedSourceOutcome == PinnedFileOpenOutcome.Opened)
                {
                    await MarkMarkerlessMoveNeedsAttentionAsync(
                        journal,
                        "A source path was recreated after markerless deletion completed.",
                        cancellationToken);
                    return new(journal, CanComplete: false);
                }
            }
        }

        if (journal.State < FileMutationJournalState.SourceDeletionAuthorized)
        {
            journal = await _fileMutationJournalStore!.AdvanceAsync(
                journal.OperationId,
                FileMutationJournalState.SourceDeletionAuthorized,
                journal.TargetPhysicalObjectIdentity,
                audiobookId: null,
                error: null,
                cancellationToken);
        }

        if (journal.State != FileMutationJournalState.SourceDeletionAuthorized)
        {
            return new(journal, CanComplete: true);
        }

        PinnedDirectoryCreation.PinnedFileEntry? sourceEntry;
        PinnedFileOpenOutcome sourceOpenOutcome;
        if (liveSource != null && liveSource.VisiblePathMatches())
        {
            sourceEntry = liveSource.DuplicateForOperation();
            sourceOpenOutcome = PinnedFileOpenOutcome.Opened;
        }
        else
        {
            sourceOpenOutcome =
                pathLock.SourceParent.TryOpenExistingFileWithOutcome(
                    pathLock.SourceName,
                    requireDeleteAccess: false,
                    out sourceEntry);
        }
        using (sourceEntry)
        {
            if (sourceOpenOutcome == PinnedFileOpenOutcome.Unavailable)
            {
                return new(journal, CanComplete: false);
            }
            if (sourceOpenOutcome == PinnedFileOpenOutcome.Opened)
            {
                if (!await MatchesMarkerlessSourceProofAsync(
                        sourceEntry!,
                        journal,
                        cancellationToken))
                {
                    await MarkMarkerlessMoveNeedsAttentionAsync(
                        journal,
                        "The markerless source was replaced before authorized deletion.",
                        cancellationToken);
                    return new(journal, CanComplete: false);
                }

                if (liveSource == null)
                {
                    var retainedValidation =
                        await _fileMutationJournalStore!
                            .AdvanceWithCommitValidationAsync(
                                journal.OperationId,
                                FileMutationJournalState.CompletedSourceRetained,
                                journal.TargetPhysicalObjectIdentity,
                                audiobookId: null,
                                error:
                                    "Source retained because live delete authority was lost across the operation boundary.",
                                validationToken =>
                                    ProbeMarkerlessMoveRetainedCompletionAsync(
                                        pathLock,
                                        journal,
                                        validationToken,
                                        requireTargetPhysicalIdentity: false),
                                cancellationToken);
                    if (retainedValidation
                        == RegistrationPublicationMatchOutcome.Unavailable)
                    {
                        return new(journal, CanComplete: false);
                    }
                    if (retainedValidation
                        != RegistrationPublicationMatchOutcome.Match)
                    {
                        await MarkMarkerlessMoveNeedsAttentionAsync(
                            journal,
                            "The markerless source or destination changed while restart recovery retained the source.",
                            cancellationToken);
                    }

                    return new(journal, CanComplete: false);
                }

                using var targetEntry = pathLock.DestinationParent.TryOpenExistingFile(
                    pathLock.DestinationName,
                    requireDeleteAccess: false);
                if (!pathLock.SourceParent.VisiblePathMatches()
                    || !pathLock.DestinationParent.VisiblePathMatches()
                    || targetEntry == null
                    || liveTarget == null
                    || !liveTarget.VisiblePathMatches()
                    || !liveTarget.IdentifiesSameEntry(targetEntry)
                    || !TargetMatchesMarkerlessJournal(targetEntry, journal)
                    || !await MatchesMarkerlessTargetContentAsync(
                        targetEntry, journal, cancellationToken)
                    || !await MatchesMarkerlessSourceProofAsync(
                        liveSource, journal, cancellationToken))
                {
                    await MarkMarkerlessMoveNeedsAttentionAsync(
                        journal,
                        "The live source or verified destination changed immediately before source retirement.",
                        cancellationToken);
                    return new(journal, CanComplete: false);
                }

                // Retire only the exact source captured before publication.
                // A journal retry has no such handle and takes source retention above.
                liveSource.Delete(immediateWindows: true);
                liveSource.Dispose();
                pathLock.SourceParent.FlushDirectoryEntry();
                if (AfterMarkerlessMoveSourceDeletedBeforeStateForTestAsync != null)
                {
                    await AfterMarkerlessMoveSourceDeletedBeforeStateForTestAsync();
                }
            }
        }

        if (!VisiblePathMatchesOrThrowUnavailable(
                pathLock.SourceParent,
                "The markerless source parent is temporarily unavailable before deletion can be recorded durably."))
        {
            await MarkMarkerlessMoveNeedsAttentionAsync(
                journal,
                "The markerless source parent changed before deletion could be recorded durably.",
                cancellationToken);
            return new(journal, CanComplete: false);
        }

        journal = await _fileMutationJournalStore!.AdvanceAsync(
            journal.OperationId,
            FileMutationJournalState.SourceDeleted,
            journal.TargetPhysicalObjectIdentity,
            audiobookId: null,
            error: null,
            cancellationToken);
        if (AfterMarkerlessMoveSourceDeletedStateForTestAsync != null)
        {
            await AfterMarkerlessMoveSourceDeletedStateForTestAsync();
        }

        return new(journal, CanComplete: true);
    }
}
