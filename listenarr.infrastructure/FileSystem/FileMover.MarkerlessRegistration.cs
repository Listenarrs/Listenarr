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
            string? expectedRegisteredPhysicalObjectIdentity)
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

            if (action == FileAction.Move
                && !OperatingSystem.IsWindows()
                && (ForceCrossVolumeForTest
                    || !initialSource.IsOnSameVolume(gate.DestinationParent)))
            {
                LogMutation(
                    FileMutationOutcome.Blocked,
                    action,
                    source,
                    destination,
                    "Unix cross-volume registration moves require source retirement that cannot be generation-fenced without a library-side namespace claim");
                return new MarkerlessRegistrationPreparation(true, null);
            }

            var proof = await CaptureMarkerlessSourceProofAsync(
                initialSource,
                cancellationToken,
                includeSha256: action != FileAction.HardlinkCopy);
            if (initialDestination != null)
            {
                if (string.IsNullOrWhiteSpace(proof.Sha256)
                    && !string.Equals(
                        initialDestination.GetObjectIdentity(),
                        proof.PhysicalObjectIdentity,
                        StringComparison.Ordinal))
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
                    proof.PhysicalObjectIdentity,
                    proof.Length,
                    proof.Sha256),
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
            await ValidateMarkerlessRegistrationJournalAsync(journal, action, gate);
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

        if (!string.IsNullOrWhiteSpace(expectedRegisteredPhysicalObjectIdentity)
            && !string.Equals(
                journal.TargetPhysicalObjectIdentity,
                expectedRegisteredPhysicalObjectIdentity,
                StringComparison.Ordinal))
        {
            await MarkMarkerlessRegistrationNeedsAttentionAsync(
                journal,
                "Durable audiobook ownership identifies a different destination generation.",
                cancellationToken);
            return new MarkerlessRegistrationPreparation(true, null);
        }

        var targetEntry = gate.DestinationParent.OpenExistingFileForStableRead(
            gate.DestinationName);
        try
        {
            if (!TargetMatchesMarkerlessJournal(targetEntry, journal)
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
        if (journal.ProtocolVersion != FileMutationProtocol.MarkerlessDatabaseState
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
        if (journal.State < FileMutationJournalState.TargetVerified)
        {
            throw new InvalidOperationException(
                "The markerless registration destination is not verified.");
        }

        if (journal.State < FileMutationJournalState.RegistrationCommitted)
        {
            journal = _fileMutationJournalStore.Advance(
                operationId,
                FileMutationJournalState.RegistrationCommitted,
                targetPhysicalObjectIdentity,
                audiobookId,
                error: null);
        }
        else if (!journal.AudiobookId.HasValue)
        {
            journal = _fileMutationJournalStore.Advance(
                operationId,
                journal.State,
                targetPhysicalObjectIdentity,
                audiobookId,
                error: null);
        }
        else if (journal.AudiobookId.Value != audiobookId)
        {
            throw new InvalidOperationException(
                "The markerless registration journal is committed to another audiobook.");
        }

        if (action != FileAction.Move
            && journal.State < FileMutationJournalState.Completed)
        {
            journal = _fileMutationJournalStore.Advance(
                operationId,
                FileMutationJournalState.Completed,
                targetPhysicalObjectIdentity,
                audiobookId,
                error: null);
        }

        return journal.State != FileMutationJournalState.NeedsAttention
            && (action == FileAction.Move
                ? journal.State >= FileMutationJournalState.RegistrationCommitted
                : journal.State >= FileMutationJournalState.Completed);
    }

    private async Task ValidateMarkerlessRegistrationJournalAsync(
        FileMutationJournal journal,
        FileAction action,
        FileMoveGateLease gate)
    {
        if (journal.ProtocolVersion != FileMutationProtocol.MarkerlessDatabaseState
            || journal.Action != action
            || !await JournalPathsMatchGateAsync(journal, gate))
        {
            throw new InvalidOperationException(
                "The durable registration identity does not match the requested operation.");
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
