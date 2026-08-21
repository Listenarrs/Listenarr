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
        var journal = await _fileMutationJournalStore.GetAsync(
            operationId,
            cancellationToken);
        if (journal == null)
        {
            using var initialSource = gate.SourceParent.TryOpenExistingFile(
                gate.SourceName,
                requireDeleteAccess: false);
            using var initialDestination = gate.DestinationParent.TryOpenExistingFile(
                gate.DestinationName,
                requireDeleteAccess: false);
            if (initialSource == null || !initialSource.VisiblePathMatches())
            {
                return new MarkerlessRegistrationPreparation(true, null);
            }
            if (expectedSourceProof.HasValue
                && !initialSource.MatchesObjectIdentity(
                    expectedSourceProof.Value.PhysicalObjectIdentity))
            {
                return new MarkerlessRegistrationPreparation(true, null);
            }

            var proof = await CaptureMarkerlessSourceProofAsync(
                initialSource,
                cancellationToken,
                includeSha256: expectedSourceProof.HasValue
                    || action != FileAction.HardlinkCopy);
            if (expectedSourceProof.HasValue
                && !MatchesExpectedSourceProof(
                    proof,
                    expectedSourceProof.Value))
            {
                return new MarkerlessRegistrationPreparation(true, null);
            }
            if (initialDestination != null)
            {
                if (string.IsNullOrWhiteSpace(proof.Sha256)
                    && !initialDestination.MatchesObjectIdentity(
                        proof.PhysicalObjectIdentity))
                {
                    proof = await CaptureMarkerlessSourceProofAsync(
                        initialSource,
                        cancellationToken,
                        includeSha256: true);
                }

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
                    gate.SourceParent.GetDirectoryObjectIdentity(),
                    gate.DestinationParent.GetDirectoryObjectIdentity(),
                    proof.PhysicalObjectIdentity,
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
                    initialDestination.GetObjectIdentity(),
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
            if (!JournalParentGenerationsMatchGate(journal, gate))
            {
                await MarkMarkerlessRegistrationNeedsAttentionAsync(
                    journal,
                    "A markerless registration parent directory changed physical generation while the operation was interrupted.",
                    cancellationToken);
                return new MarkerlessRegistrationPreparation(true, null);
            }
            if (expectedSourceProof.HasValue
                && !JournalMatchesExpectedSourceProof(
                    journal,
                    expectedSourceProof.Value))
            {
                throw new InvalidOperationException(
                    "The durable registration operation is bound to another source generation or content proof.");
            }
        }

        if (journal.State == FileMutationJournalState.NeedsAttention)
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
                cancellationToken);
            if (journal.State == FileMutationJournalState.NeedsAttention)
            {
                return new MarkerlessRegistrationPreparation(true, null);
            }
        }
        else if (journal.State >= FileMutationJournalState.TargetVerified)
        {
            if (!await MarkerlessRegistrationTargetMatchesAsync(
                    gate,
                    journal,
                    cancellationToken))
            {
                await MarkMarkerlessRegistrationNeedsAttentionAsync(
                    journal,
                    "The verified registration destination changed physical generation or content.",
                    cancellationToken);
                return new MarkerlessRegistrationPreparation(true, null);
            }
        }

        var targetEntry = gate.DestinationParent.OpenExistingFileForStableRead(
            gate.DestinationName);
        try
        {
            if (!TargetMatchesMarkerlessJournal(targetEntry, journal)
                || (!string.IsNullOrWhiteSpace(expectedRegisteredPhysicalObjectIdentity)
                    && !targetEntry.MatchesObjectIdentity(
                        expectedRegisteredPhysicalObjectIdentity))
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

            var lease = PinnedAudiobookFileRegistrationLease.Create(
                targetEntry,
                gate.DestinationPath,
                journal.TargetPhysicalObjectIdentity,
                journal.SourcePhysicalObjectIdentity,
                commitRegistration: audiobookId => CommitMarkerlessRegistration(
                    journal.OperationId,
                    action,
                    journal.TargetPhysicalObjectIdentity!,
                    audiobookId));
            targetEntry = null!;
            return new MarkerlessRegistrationPreparation(true, lease);
        }
        finally
        {
            targetEntry?.Dispose();
        }
    }

    private bool CommitMarkerlessRegistration(
        Guid operationId,
        FileAction action,
        string targetPhysicalObjectIdentity,
        int audiobookId)
    {
        var journal = _fileMutationJournalStore!.Get(operationId)
            ?? throw new InvalidOperationException(
                "The markerless registration journal no longer exists.");
        if (journal.ProtocolVersion != FileMutationProtocol.Current
            || journal.Action != action
            || !string.Equals(
                journal.TargetPhysicalObjectIdentity,
                targetPhysicalObjectIdentity,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The markerless registration identity changed before commit.");
        }
        if (journal.State == FileMutationJournalState.NeedsAttention)
        {
            throw new InvalidOperationException(
                "A markerless registration requiring attention cannot be committed.");
        }
        var publicationMatch = ProbeMarkerlessJournalTarget(
            journal,
            targetPhysicalObjectIdentity);
        if (publicationMatch == RegistrationPublicationMatchOutcome.Mismatch)
        {
            _ = _fileMutationJournalStore.Advance(
                operationId,
                FileMutationJournalState.NeedsAttention,
                targetPhysicalObjectIdentity,
                audiobookId,
                "The registration destination changed before its journal commit.");
            return false;
        }
        if (journal.State < FileMutationJournalState.TargetVerified)
        {
            throw new InvalidOperationException(
                "The markerless registration destination is not verified.");
        }

        RegistrationPublicationMatchOutcome ValidateCommitPublication()
        {
            var validation = ProbeMarkerlessJournalTarget(
                journal,
                targetPhysicalObjectIdentity);
            return validation == RegistrationPublicationMatchOutcome.Unavailable
                ? RegistrationPublicationMatchOutcome.Match
                : validation;
        }

        if (journal.State < FileMutationJournalState.RegistrationCommitted)
        {
            var commitValidation =
                _fileMutationJournalStore.AdvanceWithCommitValidation(
                    operationId,
                    FileMutationJournalState.RegistrationCommitted,
                    targetPhysicalObjectIdentity,
                    audiobookId,
                    error: null,
                    ValidateCommitPublication);
            if (commitValidation == RegistrationPublicationMatchOutcome.Mismatch)
            {
                _ = _fileMutationJournalStore.Advance(
                    operationId,
                    FileMutationJournalState.NeedsAttention,
                    targetPhysicalObjectIdentity,
                    audiobookId,
                    "The registration destination changed while its durable owner commit was being recorded.");
                return false;
            }
            journal = _fileMutationJournalStore.Get(operationId)
                ?? throw new InvalidOperationException(
                    "The markerless registration journal disappeared after owner commit.");
        }
        else if (!journal.AudiobookId.HasValue)
        {
            var ownerBindingValidation =
                _fileMutationJournalStore.AdvanceWithCommitValidation(
                    operationId,
                    journal.State,
                    targetPhysicalObjectIdentity,
                    audiobookId,
                    error: null,
                    ValidateCommitPublication);
            if (ownerBindingValidation == RegistrationPublicationMatchOutcome.Mismatch)
            {
                _ = _fileMutationJournalStore.Advance(
                    operationId,
                    FileMutationJournalState.NeedsAttention,
                    targetPhysicalObjectIdentity,
                    audiobookId,
                    "The registration destination changed while its durable owner binding was being recorded.");
                return false;
            }
            journal = _fileMutationJournalStore.Get(operationId)
                ?? throw new InvalidOperationException(
                    "The markerless registration journal disappeared after owner binding.");
        }
        else if (journal.AudiobookId.Value != audiobookId)
        {
            throw new InvalidOperationException(
                "The markerless registration journal is committed to another audiobook.");
        }

        if (action != FileAction.Move
            && journal.State < FileMutationJournalState.Completed)
        {
            var completionValidation =
                _fileMutationJournalStore.AdvanceWithCommitValidation(
                    operationId,
                    FileMutationJournalState.Completed,
                    targetPhysicalObjectIdentity,
                    audiobookId,
                    error: null,
                    ValidateCommitPublication);
            if (completionValidation == RegistrationPublicationMatchOutcome.Mismatch)
            {
                _ = _fileMutationJournalStore.Advance(
                    operationId,
                    FileMutationJournalState.NeedsAttention,
                    targetPhysicalObjectIdentity,
                    audiobookId,
                    "The registration destination changed before publication completion could be committed.");
                return false;
            }
            journal = _fileMutationJournalStore.Get(operationId)
                ?? throw new InvalidOperationException(
                    "The markerless registration journal disappeared after publication completion.");
        }

        return journal.State != FileMutationJournalState.NeedsAttention
            && (action == FileAction.Move
                ? journal.State >= FileMutationJournalState.RegistrationCommitted
                : journal.State >= FileMutationJournalState.Completed);
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
