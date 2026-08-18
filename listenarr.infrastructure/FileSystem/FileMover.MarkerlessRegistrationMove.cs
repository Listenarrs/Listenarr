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

        if (journal.ProtocolVersion != FileMutationProtocol.Current
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
        if (string.IsNullOrWhiteSpace(journal.TargetPhysicalObjectIdentity)
            || !registrationLease.MatchesPhysicalObjectIdentity(
                journal.TargetPhysicalObjectIdentity)
            || !string.Equals(
                journal.SourcePhysicalObjectIdentity,
                registrationLease.SourcePhysicalObjectIdentity,
                StringComparison.Ordinal))
        {
            await MarkMarkerlessRegistrationNeedsAttentionAsync(
                journal,
                "The registration lease no longer identifies the journaled source and destination generations.",
                cancellationToken);
            return false;
        }
        var publicationMatch = ProbeCurrentPublication(registrationLease);
        if (publicationMatch == RegistrationPublicationMatchOutcome.Unavailable)
        {
            return false;
        }
        if (publicationMatch == RegistrationPublicationMatchOutcome.Mismatch)
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
        if (!JournalParentGenerationsMatchGate(journal, gate))
        {
            await MarkMarkerlessRegistrationNeedsAttentionAsync(
                journal,
                "A markerless registration move parent directory changed physical generation while source retirement was interrupted.",
                cancellationToken);
            return false;
        }

        if (!await MarkerlessRegistrationTargetMatchesAsync(
                gate,
                journal,
                cancellationToken))
        {
            await MarkMarkerlessRegistrationNeedsAttentionAsync(
                journal,
                "The registered destination changed before source retirement.",
                cancellationToken);
            return false;
        }
        publicationMatch = ProbeCurrentPublication(registrationLease);
        if (publicationMatch == RegistrationPublicationMatchOutcome.Unavailable)
        {
            return false;
        }
        if (publicationMatch == RegistrationPublicationMatchOutcome.Mismatch)
        {
            await MarkMarkerlessRegistrationNeedsAttentionAsync(
                journal,
                "The registered destination changed before source retirement.",
                cancellationToken);
            return false;
        }

        if (journal.State >= FileMutationJournalState.SourceDeleted)
        {
            var sourceOpenOutcome = gate.SourceParent.TryOpenExistingFileWithOutcome(
                gate.SourceName,
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
                    await MarkMarkerlessRegistrationNeedsAttentionAsync(
                        journal,
                        "A source path was recreated after the registered source generation was deleted.",
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
                journal.AudiobookId,
                error: null,
                cancellationToken);
        }

        if (journal.State == FileMutationJournalState.SourceDeletionAuthorized)
        {
            var sourceOpenOutcome =
                gate.SourceParent.TryOpenExistingFileForStableDeleteWithOutcome(
                    gate.SourceName,
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
                        await MarkMarkerlessRegistrationNeedsAttentionAsync(
                            journal,
                            "The registered move source was replaced before authorized deletion.",
                            cancellationToken);
                        return false;
                    }
                    if (BeforeMarkerlessRegistrationSourceDeleteForTestAsync != null)
                    {
                        await BeforeMarkerlessRegistrationSourceDeleteForTestAsync();
                    }

                    sourceEntry!.Delete(immediateWindows: true);
                    gate.SourceParent.FlushDirectoryEntry();
                    if (AfterMarkerlessMoveSourceDeletedBeforeStateForTestAsync != null)
                    {
                        await AfterMarkerlessMoveSourceDeletedBeforeStateForTestAsync();
                    }
                }
            }

            if (!VisiblePathMatchesOrThrowUnavailable(
                    gate.SourceParent,
                    "The registered move source parent is temporarily unavailable before deletion can be recorded durably."))
            {
                await MarkMarkerlessRegistrationNeedsAttentionAsync(
                    journal,
                    "The registered move source parent changed before deletion could be recorded durably.",
                    cancellationToken);
                return false;
            }

            journal = await _fileMutationJournalStore.AdvanceAsync(
                journal.OperationId,
                FileMutationJournalState.SourceDeleted,
                journal.TargetPhysicalObjectIdentity,
                journal.AudiobookId,
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
                        validationToken);
                    if (moveValidation != RegistrationPublicationMatchOutcome.Match)
                    {
                        return moveValidation;
                    }

                    return ProbeCurrentPublication(registrationLease);
                },
                cancellationToken);
        if (completionValidation == RegistrationPublicationMatchOutcome.Unavailable)
        {
            return false;
        }
        if (completionValidation != RegistrationPublicationMatchOutcome.Match)
        {
            await MarkMarkerlessRegistrationNeedsAttentionAsync(
                journal,
                "The registered move source, destination, or parent generation changed before completion could be committed.",
                cancellationToken);
            return false;
        }
        LogMutation(
            FileMutationOutcome.Success,
            FileAction.Move,
            source,
            destination,
            "Retired the database-authorized markerless registration source");
        return true;
    }
}
