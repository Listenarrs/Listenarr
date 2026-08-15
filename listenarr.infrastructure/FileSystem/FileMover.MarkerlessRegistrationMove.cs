using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
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

        if (journal.ProtocolVersion != FileMutationProtocol.MarkerlessDatabaseState
            || journal.Action != FileAction.Move)
        {
            throw new InvalidOperationException(
                "The markerless registration move identity does not match the requested completion.");
        }
        if (journal.State == FileMutationJournalState.NeedsAttention)
        {
            return false;
        }
        if (journal.State < FileMutationJournalState.RegistrationCommitted
            || !journal.AudiobookId.HasValue)
        {
            _logger.LogWarning(
                "Blocked markerless source retirement for {OperationId} because registration is not durably committed.",
                journal.OperationId);
            return false;
        }
        if (!string.Equals(
                journal.TargetPhysicalObjectIdentity,
                registrationLease.PhysicalObjectIdentity,
                StringComparison.Ordinal)
            || !string.Equals(
                journal.SourcePhysicalObjectIdentity,
                registrationLease.SourcePhysicalObjectIdentity,
                StringComparison.Ordinal)
            || !registrationLease.MatchesCurrentPublication())
        {
            await MarkMarkerlessRegistrationNeedsAttentionAsync(
                journal,
                "The registration lease no longer identifies the journaled source and destination generations.",
                cancellationToken);
            return false;
        }

        using var gate = await TryAcquireFileMoveGateAsync(
            source,
            destination,
            allowExistingAliasForRecovery: true);
        if (gate == null)
        {
            return false;
        }
        if (!await JournalPathsMatchGateAsync(journal, gate))
        {
            throw new InvalidOperationException(
                "The markerless registration move paths do not match the requested completion.");
        }

        if (!await MarkerlessRegistrationTargetMatchesAsync(
                gate,
                journal,
                cancellationToken)
            || !registrationLease.MatchesCurrentPublication())
        {
            await MarkMarkerlessRegistrationNeedsAttentionAsync(
                journal,
                "The registered destination changed before source retirement.",
                cancellationToken);
            return false;
        }

        if (journal.State >= FileMutationJournalState.SourceDeleted)
        {
            using var recreatedSource = gate.SourceParent.TryOpenExistingFile(
                gate.SourceName,
                requireDeleteAccess: false);
            if (recreatedSource != null)
            {
                await MarkMarkerlessRegistrationNeedsAttentionAsync(
                    journal,
                    "A source path was recreated after the registered source generation was deleted.",
                    cancellationToken);
                return false;
            }
        }

        if (journal.State < FileMutationJournalState.SourceDeletionAuthorized)
        {
            journal = await _fileMutationJournalStore.AdvanceAsync(
                journal.OperationId,
                FileMutationJournalState.SourceDeletionAuthorized,
                journal.TargetPhysicalObjectIdentity,
                journal.AudiobookId,
                error: null,
                cancellationToken);
        }

        if (journal.State == FileMutationJournalState.SourceDeletionAuthorized)
        {
            using var sourceEntry = gate.SourceParent.TryOpenExistingFile(
                gate.SourceName,
                requireDeleteAccess: true);
            if (sourceEntry != null)
            {
                if (!await MatchesMarkerlessSourceProofAsync(
                        sourceEntry,
                        journal,
                        cancellationToken))
                {
                    await MarkMarkerlessRegistrationNeedsAttentionAsync(
                        journal,
                        "The registered move source was replaced before authorized deletion.",
                        cancellationToken);
                    return false;
                }

                sourceEntry.Delete(immediateWindows: true);
                gate.SourceParent.FlushDirectoryEntry();
                if (AfterMarkerlessMoveSourceDeletedBeforeStateForTestAsync != null)
                {
                    await AfterMarkerlessMoveSourceDeletedBeforeStateForTestAsync();
                }
            }

            journal = await _fileMutationJournalStore.AdvanceAsync(
                journal.OperationId,
                FileMutationJournalState.SourceDeleted,
                journal.TargetPhysicalObjectIdentity,
                journal.AudiobookId,
                error: null,
                cancellationToken);
        }

        using (var recreatedSource = gate.SourceParent.TryOpenExistingFile(
            gate.SourceName,
            requireDeleteAccess: false))
        {
            if (recreatedSource != null)
            {
                await MarkMarkerlessRegistrationNeedsAttentionAsync(
                    journal,
                    "A source path was recreated before the markerless registration move completed.",
                    cancellationToken);
                return false;
            }
        }

        _ = await _fileMutationJournalStore.AdvanceAsync(
            journal.OperationId,
            FileMutationJournalState.Completed,
            journal.TargetPhysicalObjectIdentity,
            journal.AudiobookId,
            error: null,
            cancellationToken);
        LogMutation(
            FileMutationOutcome.Success,
            FileAction.Move,
            source,
            destination,
            "Retired the database-authorized markerless registration source");
        return true;
    }
}
