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

        var cancellationToken = CancellationToken.None;
        var journal = await _fileMutationJournalStore.GetAsync(
            operationId,
            cancellationToken);
        if (journal == null)
        {
            using var initialSource = pathLock.SourceParent.TryOpenExistingFile(
                pathLock.SourceName,
                requireDeleteAccess: true);
            using var initialDestination =
                pathLock.DestinationParent.TryOpenExistingFile(
                    pathLock.DestinationName,
                    requireDeleteAccess: false);
            if (initialSource == null
                || initialDestination != null
                || !initialSource.VisiblePathMatches()
                || (expectedSourceProof.HasValue
                    && !initialSource.MatchesObjectIdentity(
                        expectedSourceProof.Value.PhysicalObjectIdentity)))
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
                includeSha256: expectedSourceProof.HasValue);
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
            if (!JournalParentGenerationsMatchGate(journal, pathLock))
            {
                await MarkMarkerlessMoveNeedsAttentionAsync(
                    journal,
                    "A markerless move parent directory changed physical generation while the operation was interrupted.",
                    cancellationToken);
                return false;
            }
            if (expectedSourceProof.HasValue
                && !JournalMatchesExpectedSourceProof(
                    journal,
                    expectedSourceProof.Value))
            {
                throw new InvalidOperationException(
                    "The durable file-move operation is bound to another source generation or content proof.");
            }
        }

        if (journal.State == FileMutationJournalState.NeedsAttention)
        {
            return false;
        }
        if (journal.State == FileMutationJournalState.OwnerMetadataReconciled)
        {
            return OwnerMetadataReconciledTargetMatches(pathLock, journal);
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
                        || !observedTarget.MatchesObjectIdentity(
                            journal.SourcePhysicalObjectIdentity)
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
                        journal.SourcePhysicalObjectIdentity,
                        audiobookId: null,
                        error: null,
                        cancellationToken);
                }
            }
        }

        if (journal.State == FileMutationJournalState.Planned)
        {
            using var sourceEntry = pathLock.SourceParent.TryOpenExistingFile(
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

            var canUseNativeRename = !DisableNativeFileRenameForTest
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
                    || !sourceEntry.MatchesObjectIdentity(
                        journal.SourcePhysicalObjectIdentity))
                {
                    throw new IOException(
                        "The markerless native move target could not be verified.");
                }
                targetIdentity = journal.SourcePhysicalObjectIdentity;
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
                || !TargetMatchesMarkerlessJournal(targetEntry, journal))
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
                using var sourceEntry =
                    pathLock.SourceParent.TryOpenExistingFile(
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
                if (!TargetMatchesMarkerlessJournal(targetEntry, journal)
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
                || !TargetMatchesMarkerlessJournal(targetEntry, journal)
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

        if (journal.State >= FileMutationJournalState.SourceDeleted)
        {
            var sourceOpenOutcome = pathLock.SourceParent.TryOpenExistingFileWithOutcome(
                pathLock.SourceName,
                requireDeleteAccess: false,
                out var recreatedSource);
            using (recreatedSource)
            {
                if (sourceOpenOutcome == PinnedFileOpenOutcome.Unavailable)
                {
                    return false;
                }
                if (sourceOpenOutcome == PinnedFileOpenOutcome.Opened)
                {
                    await MarkMarkerlessMoveNeedsAttentionAsync(
                        journal,
                        "A source path was recreated after markerless deletion completed.",
                        cancellationToken);
                    return false;
                }
            }
        }

        if (journal.State < FileMutationJournalState.SourceDeletionAuthorized)
        {
            journal = await _fileMutationJournalStore.AdvanceAsync(
                journal.OperationId,
                FileMutationJournalState.SourceDeletionAuthorized,
                journal.TargetPhysicalObjectIdentity,
                audiobookId: null,
                error: null,
                cancellationToken);
        }

        if (journal.State == FileMutationJournalState.SourceDeletionAuthorized)
        {
            var sourceOpenOutcome =
                pathLock.SourceParent.TryOpenExistingFileForStableDeleteWithOutcome(
                    pathLock.SourceName,
                    out var sourceEntry);
            using (sourceEntry)
            {
                if (sourceOpenOutcome == PinnedFileOpenOutcome.Unavailable)
                {
                    return false;
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
                        return false;
                    }

                    sourceEntry!.Delete(immediateWindows: true);
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
                return false;
            }

            journal = await _fileMutationJournalStore.AdvanceAsync(
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
                        validationToken);
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
