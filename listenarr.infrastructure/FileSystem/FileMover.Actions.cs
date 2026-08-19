using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    public Task<IAudiobookFileRegistrationLease?> PrepareActionForRegistrationAsync(
        FileAction action,
        string source,
        string destination,
        Guid operationId)
    {
        return PrepareActionForRegistrationCoreAsync(
            action,
            source,
            destination,
            operationId,
            expectedRegisteredPhysicalObjectIdentity: null,
            expectedSourceProof: null);
    }

    public Task<IAudiobookFileRegistrationLease?> PrepareActionForRegistrationAsync(
        FileAction action,
        string source,
        string destination,
        Guid operationId,
        string expectedRegisteredPhysicalObjectIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            expectedRegisteredPhysicalObjectIdentity);
        return PrepareActionForRegistrationCoreAsync(
            action,
            source,
            destination,
            operationId,
            expectedRegisteredPhysicalObjectIdentity,
            expectedSourceProof: null);
    }

    public Task<IAudiobookFileRegistrationLease?> PrepareActionForRegistrationAsync(
        FileAction action,
        string source,
        string destination,
        Guid operationId,
        string? expectedRegisteredPhysicalObjectIdentity,
        FilePublicationSourceProof expectedSourceProof)
    {
        expectedSourceProof.Validate();
        return PrepareActionForRegistrationCoreAsync(
            action,
            source,
            destination,
            operationId,
            expectedRegisteredPhysicalObjectIdentity,
            expectedSourceProof);
    }

    private async Task<IAudiobookFileRegistrationLease?>
        PrepareActionForRegistrationCoreAsync(
            FileAction action,
            string source,
            string destination,
            Guid operationId,
            string? expectedRegisteredPhysicalObjectIdentity,
            FilePublicationSourceProof? expectedSourceProof)
    {
        if (action is not (
                FileAction.Move or
                FileAction.Copy or
                FileAction.HardlinkCopy))
        {
            LogMutation(
                FileMutationOutcome.Blocked,
                action,
                source,
                destination,
                "The requested action cannot publish a registration candidate");
            return null;
        }
        if (operationId == Guid.Empty)
        {
            LogMutation(
                FileMutationOutcome.Blocked,
                action,
                source,
                destination,
                "A durable registration publication requires a non-empty operation ID");
            return null;
        }
        if (await IsNewMutationBlockedByCapabilityAsync(
                action,
                source,
                destination,
                operationId))
        {
            return null;
        }

        var markerless = await TryPrepareActionForRegistrationMarkerlessAsync(
            action,
            source,
            destination,
            operationId,
            expectedRegisteredPhysicalObjectIdentity,
            expectedSourceProof);
        if (markerless.Handled)
        {
            return markerless.Lease;
        }

        LogMutation(
            FileMutationOutcome.Blocked,
            action,
            source,
            destination,
            "Durable markerless registration state is unavailable");
        return null;
    }

    public Task<bool> PerformActionOn(
        FileAction action,
        string source,
        string? destination,
        Guid operationId) =>
        PerformActionOnCore(
            action,
            source,
            destination,
            operationId,
            audiobookId: null,
            audiobookFileId: null,
            expectedSourceProof: null);

    public Task<bool> PerformActionOn(
        FileAction action,
        string source,
        string? destination,
        Guid operationId,
        FilePublicationSourceProof expectedSourceProof)
    {
        expectedSourceProof.Validate();
        return PerformActionOnCore(
            action,
            source,
            destination,
            operationId,
            audiobookId: null,
            audiobookFileId: null,
            expectedSourceProof);
    }

    public Task<bool> PerformActionOn(
        FileAction action,
        string source,
        string? destination,
        Guid operationId,
        int audiobookId,
        int audiobookFileId)
    {
        if (audiobookId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audiobookId));
        }
        if (audiobookFileId < 0
            && !FileMutationOwner.IsCompanionFile(audiobookFileId))
        {
            throw new ArgumentOutOfRangeException(nameof(audiobookFileId));
        }

        return PerformActionOnCore(
            action,
            source,
            destination,
            operationId,
            audiobookId,
            audiobookFileId,
            expectedSourceProof: null);
    }

    public Task<bool> PerformActionOn(
        FileAction action,
        string source,
        string? destination,
        Guid operationId,
        int audiobookId,
        int audiobookFileId,
        FilePublicationSourceProof expectedSourceProof)
    {
        if (audiobookId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audiobookId));
        }
        if (audiobookFileId < 0
            && !FileMutationOwner.IsCompanionFile(audiobookFileId))
        {
            throw new ArgumentOutOfRangeException(nameof(audiobookFileId));
        }
        expectedSourceProof.Validate();

        return PerformActionOnCore(
            action,
            source,
            destination,
            operationId,
            audiobookId,
            audiobookFileId,
            expectedSourceProof);
    }

    private async Task<bool> PerformActionOnCore(
        FileAction action,
        string source,
        string? destination,
        Guid operationId,
        int? audiobookId,
        int? audiobookFileId,
        FilePublicationSourceProof? expectedSourceProof)
    {
        if (action == FileAction.None || destination == null) return true;
        if (operationId == Guid.Empty)
        {
            LogMutation(
                FileMutationOutcome.Blocked,
                action,
                source,
                destination,
                "A durable file mutation requires a non-empty operation ID");
            return false;
        }
        if (await IsFilesystemAliasAsync(source, destination))
        {
            var canResumeHardlinkPublication = action == FileAction.HardlinkCopy
                && _fileMutationJournalStore != null
                && await _fileMutationJournalStore.GetAsync(
                    operationId,
                    CancellationToken.None) != null;
            if (!canResumeHardlinkPublication)
            {
                LogMutation(
                    FileMutationOutcome.Blocked,
                    action,
                    source,
                    destination,
                    "Source and destination are linked aliases of the same file");
                return false;
            }
        }
        if (await IsSameFilesystemPathAsync(source, destination))
        {
            LogMutation(
                FileMutationOutcome.Skipped,
                action,
                source,
                destination,
                "Source and destination identify the same filesystem path");
            return true;
        }
        if (await IsNewMutationBlockedByCapabilityAsync(
                action,
                source,
                destination,
                operationId))
        {
            return false;
        }

        try
        {
            switch (action)
            {
                case FileAction.Move:
                    return await MoveFileAsync(
                        source,
                        destination,
                        operationId,
                        audiobookId,
                        audiobookFileId,
                        expectedSourceProof);
                case FileAction.HardlinkCopy:
                case FileAction.Copy:
                    if (audiobookId.HasValue || audiobookFileId.HasValue)
                    {
                        throw new InvalidOperationException(
                            "Owned mutation recovery binding is supported only for moves.");
                    }
                    return await PerformMarkerlessCopyOrHardlinkAsync(
                        action,
                        source,
                        destination,
                        operationId,
                        expectedSourceProof);
            }

            return false;
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            LogMutation(FileMutationOutcome.Failed, action, source, destination, exception.Message);
            throw new InvalidOperationException($"Unable to perform {action} on {source} to {destination}", exception);
        }
    }

    private async Task<bool> PerformMarkerlessCopyOrHardlinkAsync(
        FileAction action,
        string source,
        string destination,
        Guid operationId,
        FilePublicationSourceProof? expectedSourceProof)
    {
        var markerless = await TryPrepareActionForRegistrationMarkerlessAsync(
            action,
            source,
            destination,
            operationId,
            expectedRegisteredPhysicalObjectIdentity: null,
            expectedSourceProof);
        if (!markerless.Handled)
        {
            LogMutation(
                FileMutationOutcome.Blocked,
                action,
                source,
                destination,
                "Durable markerless file-publication state is unavailable");
            return false;
        }

        using var lease = markerless.Lease;
        if (lease == null || !lease.MatchesCurrentPublication())
        {
            return false;
        }

        var cancellationToken = CancellationToken.None;
        var journal = await _fileMutationJournalStore!.GetAsync(
            operationId,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "The markerless file-publication journal disappeared.");
        if (journal.AudiobookId.HasValue
            || journal.State == FileMutationJournalState.NeedsAttention
            || journal.State < FileMutationJournalState.TargetVerified
            || string.IsNullOrWhiteSpace(journal.TargetPhysicalObjectIdentity)
            || !lease.MatchesPhysicalObjectIdentity(
                journal.TargetPhysicalObjectIdentity))
        {
            return false;
        }

        if (journal.State < FileMutationJournalState.Completed)
        {
            var completionValidation =
                await _fileMutationJournalStore.AdvanceWithCommitValidationAsync(
                    operationId,
                    FileMutationJournalState.Completed,
                    journal.TargetPhysicalObjectIdentity,
                    audiobookId: null,
                    error: null,
                    async _ =>
                    {
                        if (BeforeMarkerlessCompletedJournalCommitForTestAsync != null)
                        {
                            await BeforeMarkerlessCompletedJournalCommitForTestAsync();
                        }

                        var validation = ProbeCurrentPublication(lease);
                        return validation == RegistrationPublicationMatchOutcome.Unavailable
                            ? RegistrationPublicationMatchOutcome.Match
                            : validation;
                    },
                    cancellationToken);
            if (completionValidation == RegistrationPublicationMatchOutcome.Mismatch)
            {
                await MarkMarkerlessRegistrationNeedsAttentionAsync(
                    journal,
                    "The markerless file publication changed before completion could be committed.",
                    cancellationToken);
                return false;
            }
        }

        var publicationMatch = ProbeCurrentPublication(lease);
        if (publicationMatch == RegistrationPublicationMatchOutcome.Mismatch)
        {
            await MarkMarkerlessRegistrationNeedsAttentionAsync(
                journal,
                "The markerless file publication changed while completion was committed.",
                cancellationToken);
            return false;
        }
        if (publicationMatch == RegistrationPublicationMatchOutcome.Unavailable)
        {
            // Completion and physical generation were already durably verified. A
            // temporary namespace outage is not evidence that publication changed.
            return true;
        }

        LogMutation(
            FileMutationOutcome.Success,
            action,
            source,
            destination,
            "Markerless database-backed file publication");
        return true;
    }

    private void LogMutation(FileMutationOutcome outcome, FileAction action, string source, string? destination, string? reason = null)
    {
        var result = new FileMutationResult(outcome, action, source, destination, reason);
        _logger.LogInformation(
            "File mutation {Outcome}: {Action} {Source} -> {Destination}. Reason: {Reason}",
            result.Outcome,
            result.Action,
            LogRedaction.SanitizeFilePath(result.SourcePath),
            LogRedaction.SanitizeFilePath(result.DestinationPath ?? string.Empty),
            LogRedaction.SanitizeText(result.Reason ?? string.Empty));
    }
}
