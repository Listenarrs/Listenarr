using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private readonly record struct MarkerlessRegistrationPreparation(
        bool Handled,
        IAudiobookFileRegistrationLease? Lease);

    private async Task<MarkerlessRegistrationPreparation>
        TryPrepareActionForRegistrationMarkerlessAsync(
            FileAction action,
            string source,
            string destination,
            Guid operationId,
            string? expectedRegisteredPhysicalObjectIdentity,
            FilePublicationSourceProof? expectedSourceProof,
            bool isCompanionFile,
            int? companionAudiobookId)
    {
        if (_fileMutationJournalStore == null)
        {
            return new MarkerlessRegistrationPreparation(false, null);
        }
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A markerless registration publication requires a non-empty operation ID.",
                nameof(operationId));
        }

        using var gate = await TryAcquireFileMoveGateAsync(
            source,
            destination,
            allowExistingAliasForRecovery: true);
        if (gate == null)
        {
            return new MarkerlessRegistrationPreparation(true, null);
        }

        var cancellationToken = CancellationToken.None;
        PinnedDirectoryCreation.PinnedFileEntry? liveMoveSourceEntry = null;
        PinnedDirectoryCreation.PinnedFileEntry? liveRegistrationTargetEntry = null;
        try
        {
            var journal = await _fileMutationJournalStore.GetAsync(
                operationId,
                cancellationToken);
            var resumedJournal = journal != null;
            if (!resumedJournal && action == FileAction.Move)
            {
                var sourceOutcome =
                    gate.SourceParent.TryOpenExistingFileForStableDeleteWithOutcome(
                        gate.SourceName,
                        out liveMoveSourceEntry);
                if (sourceOutcome != PinnedFileOpenOutcome.Opened
                    || liveMoveSourceEntry == null
                    || !liveMoveSourceEntry.VisiblePathMatches())
                {
                    return new MarkerlessRegistrationPreparation(true, null);
                }
            }

            if (journal == null)
            {
                using var nonMoveInitialSource = action == FileAction.Move
                    ? null
                    : gate.SourceParent.TryOpenExistingFile(
                        gate.SourceName,
                        requireDeleteAccess: false);
                var initialSource = liveMoveSourceEntry ?? nonMoveInitialSource;
                using var initialDestination = gate.DestinationParent.TryOpenExistingFile(
                    gate.DestinationName,
                    requireDeleteAccess: false);
                if (initialSource == null || !initialSource.VisiblePathMatches())
                {
                    return new MarkerlessRegistrationPreparation(true, null);
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
                    return new MarkerlessRegistrationPreparation(true, null);
                }
                if (initialDestination != null)
                {
                    if (!initialDestination.VisiblePathMatches()
                        || !await MatchesMarkerlessContentAsync(
                            initialDestination,
                            proof.Length,
                            proof.Sha256,
                            cancellationToken))
                    {
                        return new MarkerlessRegistrationPreparation(true, null);
                    }
                }

                journal = await _fileMutationJournalStore.GetOrCreateAsync(
                    new FileMutationJournalClaim(
                        operationId,
                        action,
                        gate.SourcePath,
                        gate.DestinationPath,
                        SourceParentDirectoryObjectIdentity: null,
                        DestinationParentDirectoryObjectIdentity: null,
                        SourcePhysicalObjectIdentity: null,
                        proof.Length,
                        proof.Sha256,
                        AudiobookId: companionAudiobookId,
                        AudiobookFileId: isCompanionFile
                            ? FileMutationOwner.RegistrationCompanionFile
                            : null),
                    cancellationToken);
                if (initialDestination != null)
                {
                    journal = await _fileMutationJournalStore.AdvanceAsync(
                        journal.OperationId,
                        FileMutationJournalState.TargetIdentityPersisted,
                        targetPhysicalObjectIdentity: null,
                        audiobookId: null,
                        error: null,
                        cancellationToken);
                }
            }
            else
            {
                await ValidateMarkerlessRegistrationJournalAsync(
                    journal,
                    action,
                    gate,
                    isCompanionFile,
                    companionAudiobookId);
                if (expectedSourceProof.HasValue
                    && !JournalMatchesExpectedSourceProof(
                        journal,
                        expectedSourceProof.Value))
                {
                    throw new InvalidOperationException(
                        "The durable registration operation is bound to another source generation or content proof.");
                }
            }

            if (journal.State == FileMutationJournalState.NeedsAttention
                || journal.State == FileMutationJournalState.RollbackAuthorized
                || journal.State == FileMutationJournalState.RolledBack)
            {
                return new MarkerlessRegistrationPreparation(true, null);
            }

            if (action != FileAction.Move)
            {
                using var currentSource = gate.SourceParent.TryOpenExistingFile(
                    gate.SourceName,
                    requireDeleteAccess: false);
                if (currentSource == null
                    || !await MatchesMarkerlessSourceProofAsync(
                        currentSource,
                        journal,
                        cancellationToken))
                {
                    await MarkMarkerlessRegistrationNeedsAttentionAsync(
                        journal,
                        "The file-publication source changed physical generation or content.",
                        cancellationToken);
                    return new MarkerlessRegistrationPreparation(true, null);
                }
            }

            if (journal.State == FileMutationJournalState.Planned)
            {
                journal = await PublishMarkerlessRegistrationTargetAsync(
                    action,
                    gate,
                    journal,
                    createdTarget =>
                    {
                        liveRegistrationTargetEntry?.Dispose();
                        liveRegistrationTargetEntry = createdTarget.DuplicateForOperation();
                    },
                    cancellationToken);
                if (journal.State == FileMutationJournalState.NeedsAttention)
                {
                    return new MarkerlessRegistrationPreparation(true, null);
                }
            }

            if (journal.State == FileMutationJournalState.TargetIdentityPersisted)
            {
                journal = await VerifyMarkerlessRegistrationTargetAsync(
                    gate,
                    journal,
                    cancellationToken,
                    requirePhysicalIdentity: false,
                    allowContentPublication: liveRegistrationTargetEntry != null,
                    liveTargetEntry: liveRegistrationTargetEntry);
                if (journal.State == FileMutationJournalState.NeedsAttention)
                {
                    return new MarkerlessRegistrationPreparation(true, null);
                }
            }
            else if (FileMutationJournalLifecycle.IsRegistrationPublicationRecoverable(
                journal.State))
            {
                if (!await MarkerlessRegistrationTargetMatchesAsync(
                        gate,
                        journal,
                        cancellationToken,
                        requirePhysicalIdentity: false))
                {
                    await MarkMarkerlessRegistrationNeedsAttentionAsync(
                        journal,
                        "The verified registration destination changed physical generation or content.",
                        cancellationToken);
                    return new MarkerlessRegistrationPreparation(true, null);
                }
            }

            liveRegistrationTargetEntry?.Dispose();
            liveRegistrationTargetEntry = null;
            var targetEntry = gate.DestinationParent.OpenExistingFileForStableRead(
                gate.DestinationName);
            try
            {
                if (!VisiblePathMatchesOrThrowUnavailable(
                        targetEntry,
                        "The registration destination is temporarily unavailable while its lease is opened.")
                    || !await MatchesMarkerlessTargetContentAsync(
                        targetEntry,
                        journal,
                        cancellationToken))
                {
                    targetEntry.Dispose();
                    await MarkMarkerlessRegistrationNeedsAttentionAsync(
                        journal,
                        "The registration destination changed while its lease was opened.",
                        cancellationToken);
                    return new MarkerlessRegistrationPreparation(true, null);
                }

                IAudiobookFileRegistrationLease lease;
                if (resumedJournal)
                {
                    // A new invocation may verify and adopt the published bytes, but it
                    // must not reconstruct source-delete authority from journaled kernel
                    // identity. The content-pinned lease deliberately exposes no source
                    // physical identity.
                    targetEntry.Dispose();
                    targetEntry = null!;
                    lease = PathOnlyAudiobookFileRegistrationLease.Open(
                        gate.DestinationPath,
                        journal.SourceLength,
                        journal.SourceSha256
                            ?? throw new InvalidOperationException(
                                "The v3 registration journal has no destination content proof."),
                        commitRegistration: audiobookId => CommitMarkerlessRegistration(
                            journal.OperationId,
                            action,
                            journal.TargetPhysicalObjectIdentity,
                            audiobookId,
                            requirePhysicalIdentity: false));
                }
                else
                {
                    IAudiobookFileRegistrationLease? targetLease = null;
                    try
                    {
                        if (action == FileAction.Move
                            && (liveMoveSourceEntry == null
                                || !liveMoveSourceEntry.VisiblePathMatches()
                                || !await MatchesMarkerlessSourceProofAsync(
                                    liveMoveSourceEntry,
                                    journal,
                                    cancellationToken)))
                        {
                            await MarkMarkerlessRegistrationNeedsAttentionAsync(
                                journal,
                                "The live move source changed after publication and before registration commit.",
                                cancellationToken);
                            return new MarkerlessRegistrationPreparation(true, null);
                        }

                        // The target is pinned for this live invocation, but its kernel
                        // identity is not persisted or required. On weak storage this
                        // keeps registration and metadata reads fully operational.
                        targetLease =
                            PinnedAudiobookFileRegistrationLease.CreatePinnedPathOnly(
                                targetEntry,
                                gate.DestinationPath,
                                commitRegistration: audiobookId =>
                                    CommitMarkerlessRegistration(
                                        journal.OperationId,
                                        action,
                                        targetPhysicalObjectIdentity: null,
                                        audiobookId,
                                        requirePhysicalIdentity: false),
                                // The journal verifies immutable published bytes.
                                // Optional enrichment needs its own durable target proof.
                                supportsMetadataWrite: false);
                        targetEntry = null!;

                        if (liveMoveSourceEntry != null)
                        {
                            lease = new MarkerlessRegistrationPublicationLease(
                                targetLease,
                                liveMoveSourceEntry);
                            targetLease = null;
                            liveMoveSourceEntry = null;
                        }
                        else
                        {
                            lease = targetLease;
                            targetLease = null;
                        }
                    }
                    finally
                    {
                        targetLease?.Dispose();
                    }
                }

                return new MarkerlessRegistrationPreparation(true, lease);
            }
            finally
            {
                targetEntry?.Dispose();
            }
        }
        finally
        {
            liveRegistrationTargetEntry?.Dispose();
            liveMoveSourceEntry?.Dispose();
        }
    }




    private async Task MarkMarkerlessRegistrationNeedsAttentionAsync(
        FileMutationJournal journal,
        string reason,
        CancellationToken cancellationToken)
    {
        _ = await _fileMutationJournalStore!.AdvanceAsync(
            journal.OperationId,
            FileMutationJournalState.NeedsAttention,
            journal.TargetPhysicalObjectIdentity,
            journal.AudiobookId,
            reason,
            cancellationToken);
        _logger.LogWarning(
            "Markerless registration publication {OperationId} requires attention: {Reason}",
            journal.OperationId,
            reason);
    }
}
