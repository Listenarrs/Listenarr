using Listenarr.Domain.Audiobooks.Enumerations;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private async Task<bool?> TryMoveFileMarkerlessAsync(
        string source,
        string destination,
        Guid operationId,
        int? audiobookId = null,
        int? audiobookFileId = null,
        FilePublicationSourceProof? expectedSourceProof = null)
    {
        if (_fileMutationJournalStore == null)
        {
            return null;
        }
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A markerless file move requires a non-empty operation ID.",
                nameof(operationId));
        }

        using var pathLock = await TryAcquireFileMoveGateAsync(
            source,
            destination,
            allowExistingAliasForRecovery: true);
        if (pathLock == null)
        {
            return false;
        }

        using var livePublication = new MarkerlessCreatedTargetLease();
        var cancellationToken = CancellationToken.None;
        var journal = await _fileMutationJournalStore.GetAsync(
            operationId,
            cancellationToken);
        var resumedJournal = journal != null;
        using var liveSource = !resumedJournal
            ? pathLock.SourceParent.TryOpenExistingFile(
                pathLock.SourceName,
                requireDeleteAccess: true)
            : null;
        if (journal == null)
        {
            var initialSource = liveSource;
            using var initialDestination =
                pathLock.DestinationParent.TryOpenExistingFile(
                    pathLock.DestinationName,
                    requireDeleteAccess: false);
            if (initialSource == null
                || initialDestination != null
                || !initialSource.VisiblePathMatches())
            {
                return false;
            }

            if (!OperatingSystem.IsWindows()
                && (ForceCrossVolumeForTest
                    || !initialSource.IsOnSameVolume(pathLock.DestinationParent)))
            {
                LogMutation(
                    FileMutationOutcome.Blocked,
                    FileAction.Move,
                    source,
                    destination,
                    "Unix cross-volume moves require source retirement that cannot be generation-fenced without a library-side namespace claim");
                return false;
            }

            var proof = await CaptureMarkerlessSourceProofAsync(
                initialSource,
                cancellationToken,
                includeSha256: true);
            if (expectedSourceProof.HasValue
                && !MatchesExpectedSourceProof(
                    proof,
                    expectedSourceProof.Value))
            {
                return false;
            }
            journal = await _fileMutationJournalStore.GetOrCreateAsync(
                new FileMutationJournalClaim(
                    operationId,
                    FileAction.Move,
                    pathLock.SourcePath,
                    pathLock.DestinationPath,
                    pathLock.SourceParent.GetDirectoryObjectIdentity(),
                    pathLock.DestinationParent.GetDirectoryObjectIdentity(),
                    proof.PhysicalObjectIdentity,
                    proof.Length,
                    proof.Sha256,
                    audiobookId,
                    audiobookFileId),
                cancellationToken);
            if (AfterMarkerlessMoveJournalPlannedForTestAsync != null)
            {
                await AfterMarkerlessMoveJournalPlannedForTestAsync();
            }
        }
        else
        {
            await ValidateMarkerlessMoveJournalAsync(
                journal,
                pathLock,
                audiobookId,
                audiobookFileId);
            if (expectedSourceProof.HasValue
                && !JournalMatchesExpectedSourceProof(
                    journal,
                    expectedSourceProof.Value))
            {
                throw new InvalidOperationException(
                    "The durable file-move operation is bound to another source generation or content proof.");
            }
        }

        if (journal.State is FileMutationJournalState.NeedsAttention
            or FileMutationJournalState.CompletedSourceRetained)
        {
            return false;
        }
        if (journal.State == FileMutationJournalState.OwnerMetadataReconciled)
        {
            return await OwnerMetadataReconciledTargetMatchesAsync(pathLock, journal, cancellationToken);
        }

        using (var observedSource = pathLock.SourceParent.TryOpenExistingFile(
            pathLock.SourceName,
            requireDeleteAccess: false))
        using (var observedTarget =
            pathLock.DestinationParent.TryOpenExistingFile(
                pathLock.DestinationName,
                requireDeleteAccess: false))
        {
            if (journal.State == FileMutationJournalState.Planned)
            {
                if (observedSource != null && observedTarget != null)
                {
                    await MarkMarkerlessMoveNeedsAttentionAsync(
                        journal,
                        "Both source and destination exist before markerless publication proof was persisted.",
                        cancellationToken);
                    return false;
                }
                if (observedSource == null && observedTarget == null)
                {
                    await MarkMarkerlessMoveNeedsAttentionAsync(
                        journal,
                        "Both source and destination are missing for a markerless move.",
                        cancellationToken);
                    return false;
                }
                if (observedSource == null)
                {
                    if (observedTarget == null
                        || !VisiblePathMatchesOrThrowUnavailable(
                            observedTarget,
                            "The markerless destination is temporarily unavailable while interrupted publication is being verified.")
                        || !await MatchesMarkerlessTargetContentAsync(
                            observedTarget,
                            journal,
                            cancellationToken))
                    {
                        await MarkMarkerlessMoveNeedsAttentionAsync(
                            journal,
                            "An unproven markerless destination cannot be attributed to the original source generation.",
                            cancellationToken);
                        return false;
                    }

                    journal = await _fileMutationJournalStore.AdvanceAsync(
                        journal.OperationId,
                        FileMutationJournalState.TargetIdentityPersisted,
                        observedTarget.GetObjectIdentity(),
                        audiobookId: null,
                        error: null,
                        cancellationToken);
                }
            }
        }

        if (journal.State == FileMutationJournalState.Planned)
        {
            using var sourceEntry = liveSource?.DuplicateForOperation()
                ?? pathLock.SourceParent.TryOpenExistingFile(
                    pathLock.SourceName,
                    requireDeleteAccess: true);
            using var existingTarget =
                pathLock.DestinationParent.TryOpenExistingFile(
                    pathLock.DestinationName,
                    requireDeleteAccess: false);
            if (sourceEntry == null
                || existingTarget != null
                || !await MatchesMarkerlessSourceProofAsync(
                    sourceEntry,
                    journal,
                    cancellationToken))
            {
                await MarkMarkerlessMoveNeedsAttentionAsync(
                    journal,
                    "The markerless move source changed before publication.",
                    cancellationToken);
                return false;
            }

            var canUseNativeRename = !resumedJournal && !DisableNativeFileRenameForTest
                && sourceEntry.IsOnSameVolume(pathLock.DestinationParent);
            if (!canUseNativeRename)
            {
                journal = await EnsureMarkerlessSourceHashAsync(
                    sourceEntry,
                    journal,
                    cancellationToken);
            }

            string targetIdentity;
            if (canUseNativeRename)
            {
                sourceEntry.MoveTo(
                    pathLock.DestinationParent,
                    pathLock.DestinationName);
                pathLock.SourceParent.FlushDirectoryEntry();
                if (!string.Equals(
                        pathLock.SourceParent.FullPath,
                        pathLock.DestinationParent.FullPath,
                        StringComparison.Ordinal))
                {
                    pathLock.DestinationParent.FlushDirectoryEntry();
                }
                if (!sourceEntry.VisiblePathMatches()
                    || !await MatchesMarkerlessTargetContentAsync(
                        sourceEntry,
                        journal,
                        cancellationToken))
                {
                    throw new IOException(
                        "The markerless native move target could not be verified.");
                }
                targetIdentity = sourceEntry.GetObjectIdentity();
                livePublication.Entry = sourceEntry.DuplicateForOperation();
                if (AfterMarkerlessMovePublishedBeforeTargetStateForTestAsync != null)
                {
                    await AfterMarkerlessMovePublishedBeforeTargetStateForTestAsync();
                }
            }
            else
            {
                using var created = pathLock.DestinationParent.CreateNewFile(
                    pathLock.DestinationName);
                targetIdentity = created.GetObjectIdentity();
                livePublication.Entry = created.DuplicateForOperation();
                if (AfterMarkerlessMoveTargetCreatedBeforeStateForTestAsync != null)
                {
                    await AfterMarkerlessMoveTargetCreatedBeforeStateForTestAsync();
                }
            }

            journal = await _fileMutationJournalStore.AdvanceAsync(
                journal.OperationId,
                FileMutationJournalState.TargetIdentityPersisted,
                targetIdentity,
                audiobookId: null,
                error: null,
                cancellationToken);
            if (AfterMarkerlessMoveTargetStateForTestAsync != null)
            {
                await AfterMarkerlessMoveTargetStateForTestAsync();
            }
        }

        if (journal.State == FileMutationJournalState.TargetIdentityPersisted)
        {
            using var targetEntry =
                pathLock.DestinationParent.TryOpenExistingFile(
                    pathLock.DestinationName,
                    requireDeleteAccess: false);
            if (targetEntry == null
                || (livePublication.Entry != null
                    && (!livePublication.Entry.VisiblePathMatches()
                        || !livePublication.Entry.IdentifiesSameEntry(targetEntry)))
                || !TargetMatchesMarkerlessJournal(targetEntry, journal, requirePhysicalIdentity: !resumedJournal))
            {
                await MarkMarkerlessMoveNeedsAttentionAsync(
                    journal,
                    "The markerless destination changed before content verification.",
                    cancellationToken);
                return false;
            }

            if (!await MatchesMarkerlessTargetContentAsync(
                    targetEntry,
                    journal,
                    cancellationToken))
            {
                if (resumedJournal && livePublication.Entry == null)
                {
                    await MarkMarkerlessMoveNeedsAttentionAsync(
                        journal,
                        "The resumed markerless destination content changed after publication; it was preserved without overwrite.",
                        cancellationToken);
                    return false;
                }

                using var sourceEntry = liveSource?.DuplicateForOperation()
                    ?? pathLock.SourceParent.TryOpenExistingFile(
                        pathLock.SourceName,
                        requireDeleteAccess: false);
                if (sourceEntry == null
                    || !await MatchesMarkerlessSourceProofAsync(
                        sourceEntry,
                        journal,
                        cancellationToken))
                {
                    await MarkMarkerlessMoveNeedsAttentionAsync(
                        journal,
                        "The markerless source is unavailable before the destination content was verified.",
                        cancellationToken);
                    return false;
                }

                await CopyMarkerlessFileAsync(
                    sourceEntry,
                    targetEntry,
                    cancellationToken);
                sourceEntry.PreserveMarkerlessMetadataTo(targetEntry);
                if (!TargetMatchesMarkerlessJournal(targetEntry, journal, requirePhysicalIdentity: !resumedJournal)
                    || !await MatchesMarkerlessTargetContentAsync(
                        targetEntry,
                        journal,
                        cancellationToken))
                {
                    throw new IOException(
                        "The markerless destination failed content verification.");
                }
                if (AfterMarkerlessMoveTargetWrittenBeforeVerifiedStateForTestAsync != null)
                {
                    await AfterMarkerlessMoveTargetWrittenBeforeVerifiedStateForTestAsync();
                }
            }

            journal = await _fileMutationJournalStore.AdvanceAsync(
                journal.OperationId,
                FileMutationJournalState.TargetVerified,
                journal.TargetPhysicalObjectIdentity,
                audiobookId: null,
                error: null,
                cancellationToken);
        }
        else if (journal.State >= FileMutationJournalState.TargetVerified)
        {
            using var targetEntry =
                pathLock.DestinationParent.TryOpenExistingFile(
                    pathLock.DestinationName,
                    requireDeleteAccess: false);
            if (targetEntry == null
                || !TargetMatchesMarkerlessJournal(targetEntry, journal, requirePhysicalIdentity: !resumedJournal)
                || !await MatchesMarkerlessTargetContentAsync(
                    targetEntry,
                    journal,
                    cancellationToken))
            {
                await MarkMarkerlessMoveNeedsAttentionAsync(
                    journal,
                    "The verified markerless destination changed.",
                    cancellationToken);
                return false;
            }
        }

        var retirement = await RetireMarkerlessMoveSourceAsync(
            pathLock,
            journal,
            liveSource,
            livePublication.Entry,
            cancellationToken);
        journal = retirement.Journal;
        if (!retirement.CanComplete)
        {
            return false;
        }

        var completionValidation =
            await _fileMutationJournalStore.AdvanceWithCommitValidationAsync(
                journal.OperationId,
                FileMutationJournalState.Completed,
                journal.TargetPhysicalObjectIdentity,
                audiobookId: null,
                error: null,
                async validationToken =>
                {
                    if (BeforeMarkerlessCompletedJournalCommitForTestAsync != null)
                    {
                        await BeforeMarkerlessCompletedJournalCommitForTestAsync();
                    }

                    return await ProbeMarkerlessMoveCompletionAsync(
                        pathLock,
                        journal,
                        validationToken,
                        requirePersistedParentIdentity: !resumedJournal,
                        requireTargetPhysicalIdentity: !resumedJournal);
                },
                cancellationToken);
        if (completionValidation == RegistrationPublicationMatchOutcome.Unavailable)
        {
            return false;
        }
        if (completionValidation != RegistrationPublicationMatchOutcome.Match)
        {
            await MarkMarkerlessMoveNeedsAttentionAsync(
                journal,
                "The markerless move source, destination, or parent generation changed before completion could be committed.",
                cancellationToken);
            return false;
        }
        LogMutation(
            FileMutationOutcome.Success,
            FileAction.Move,
            source,
            destination,
            "Markerless database-backed file move");
        return true;
    }
}
