using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{

    private static async Task<RegistrationPublicationMatchOutcome>
        ProbeMarkerlessRegistrationContentAsync(
            FileMoveGateLease gate,
            FileMutationJournal journal,
            CancellationToken cancellationToken)
    {
        try
        {
            if (!gate.DestinationParent.VisiblePathMatches())
            {
                return RegistrationPublicationMatchOutcome.Mismatch;
            }

            var outcome =
                gate.DestinationParent.TryOpenExistingFileWithOutcome(
                    gate.DestinationName,
                    requireDeleteAccess: false,
                    out var openedTarget);
            using (openedTarget)
            {
                if (outcome == PinnedFileOpenOutcome.Unavailable)
                {
                    return RegistrationPublicationMatchOutcome.Unavailable;
                }

                if (outcome != PinnedFileOpenOutcome.Opened
                    || openedTarget == null
                    || !openedTarget.VisiblePathMatches())
                {
                    return RegistrationPublicationMatchOutcome.Mismatch;
                }

                return await MatchesMarkerlessTargetContentAsync(
                    openedTarget,
                    journal,
                    cancellationToken)
                        ? RegistrationPublicationMatchOutcome.Match
                        : RegistrationPublicationMatchOutcome.Mismatch;
            }
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception)
        {
            return RegistrationPublicationMatchOutcome.Unavailable;
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException)
        {
            return RegistrationPublicationMatchOutcome.Mismatch;
        }
    }

    private async Task<bool?> TryCompletePreparedMoveMarkerlessAsync(
        string source,
        string destination,
        IAudiobookFileRegistrationLease registrationLease,
        Guid operationId)
    {
        if (_fileMutationJournalStore == null)
        {
            return null;
        }
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A markerless registration move requires a non-empty operation ID.",
                nameof(operationId));
        }

        var cancellationToken = CancellationToken.None;
        var journal = await _fileMutationJournalStore.GetAsync(
            operationId,
            cancellationToken);
        if (journal == null)
        {
            _logger.LogWarning(
                "Blocked markerless prepared-move completion for {OperationId} because its durable journal is missing.",
                operationId);
            return false;
        }

        if (journal.ProtocolVersion != FileMutationProtocol.Current
            || journal.Action != FileAction.Move)
        {
            throw new InvalidOperationException(
                "The markerless registration move identity does not match the requested completion.");
        }
        if (journal.State == FileMutationJournalState.NeedsAttention
            || journal.State == FileMutationJournalState.RollbackAuthorized
            || journal.State == FileMutationJournalState.RolledBack)
        {
            return false;
        }
        if ((!FileMutationJournalLifecycle.MayRetireSource(journal.State)
                && journal.State != FileMutationJournalState.CompletedSourceRetained)
            || !journal.AudiobookId.HasValue)
        {
            _logger.LogWarning(
                "Blocked markerless source retirement for {OperationId} because registration is not durably committed.",
                journal.OperationId);
            return false;
        }
        if (registrationLease is MarkerlessRegistrationPublicationLease liveLease
            && journal.State != FileMutationJournalState.CompletedSourceRetained)
        {
            return await CompleteLivePinnedMarkerlessMoveAsync(
                source,
                destination,
                journal,
                liveLease,
                cancellationToken);
        }

        {
            // Every other lease lacks the original process-local source handle.
            // Diagnostic strings cannot select a destructive completion path.
            // This lease was reconstructed from an existing journal. Restart/retry
            // may verify the committed target and observe whether the source still
            // exists, but persisted state cannot recreate deletion authority.
            var restartPublicationMatch =
                ProbeCurrentPublication(registrationLease);
            if (restartPublicationMatch
                != RegistrationPublicationMatchOutcome.Match)
            {
                return false;
            }

            using var recoveryGate = await TryAcquireFileMoveGateAsync(
                source,
                destination,
                allowExistingAliasForRecovery: true);
            if (recoveryGate == null
                || !await JournalPathsMatchGateAsync(journal, recoveryGate))
            {
                return false;
            }

            using var recoveryTarget =
                recoveryGate.DestinationParent.TryOpenExistingFile(
                    recoveryGate.DestinationName,
                    requireDeleteAccess: false);
            if (recoveryTarget == null
                || !recoveryTarget.VisiblePathMatches()
                || !await MatchesMarkerlessTargetContentAsync(
                    recoveryTarget,
                    journal,
                    cancellationToken))
            {
                await MarkMarkerlessRegistrationNeedsAttentionAsync(
                    journal,
                    "The registered destination content changed before restart recovery.",
                    cancellationToken);
                return false;
            }

            if (journal.State == FileMutationJournalState.CompletedSourceRetained)
            {
                return true;
            }

            var sourceOutcome =
                recoveryGate.SourceParent.TryOpenExistingFileWithOutcome(
                    recoveryGate.SourceName,
                    requireDeleteAccess: false,
                    out var observedSource);
            using (observedSource)
            {
                if (sourceOutcome == PinnedFileOpenOutcome.Unavailable)
                {
                    return false;
                }

                var terminalState =
                    sourceOutcome == PinnedFileOpenOutcome.Opened
                        ? FileMutationJournalState.CompletedSourceRetained
                        : FileMutationJournalState.Completed;
                var restartCompletionValidation =
                    await _fileMutationJournalStore.AdvanceWithCommitValidationAsync(
                        journal.OperationId,
                        terminalState,
                        journal.TargetPhysicalObjectIdentity,
                        journal.AudiobookId,
                        error: terminalState
                            == FileMutationJournalState.CompletedSourceRetained
                                ? "Source retained because live delete authority was lost across the operation boundary."
                                : null,
                        validationToken =>
                            ProbeMarkerlessRegistrationContentAsync(
                                recoveryGate,
                                journal,
                                validationToken),
                        cancellationToken);
                return restartCompletionValidation
                    == RegistrationPublicationMatchOutcome.Match;
            }
        }
    }
}
