using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    public Task<IAudiobookFileRegistrationLease?> PrepareActionForRegistrationAsync(
        FileAction action,
        string source,
        string destination,
        Guid? operationId = null)
    {
        return PrepareActionForRegistrationCoreAsync(
            action,
            source,
            destination,
            operationId,
            expectedRegisteredPhysicalObjectIdentity: null);
    }

    public Task<IAudiobookFileRegistrationLease?> PrepareActionForRegistrationAsync(
        FileAction action,
        string source,
        string destination,
        Guid? operationId,
        string expectedRegisteredPhysicalObjectIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            expectedRegisteredPhysicalObjectIdentity);
        return PrepareActionForRegistrationCoreAsync(
            action,
            source,
            destination,
            operationId,
            expectedRegisteredPhysicalObjectIdentity);
    }

    private async Task<IAudiobookFileRegistrationLease?>
        PrepareActionForRegistrationCoreAsync(
            FileAction action,
            string source,
            string destination,
            Guid? operationId,
            string? expectedRegisteredPhysicalObjectIdentity)
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

        var markerless = await TryPrepareActionForRegistrationMarkerlessAsync(
            action,
            source,
            destination,
            operationId,
            expectedRegisteredPhysicalObjectIdentity);
        if (markerless.Handled)
        {
            return markerless.Lease;
        }

        if (action == FileAction.HardlinkCopy)
        {
            if (!operationId.HasValue)
            {
                LogMutation(
                    FileMutationOutcome.Blocked,
                    action,
                    source,
                    destination,
                    "Retryable hardlink registration requires a stable operation identifier");
                return null;
            }

            var recovery = await TryRecoverHardlinkRegistrationPublicationAsync(
                source,
                destination,
                operationId.Value);
            if (recovery.Lease != null)
            {
                return recovery.Lease;
            }

            if (!string.IsNullOrWhiteSpace(
                    expectedRegisteredPhysicalObjectIdentity))
            {
                var registeredLease =
                    await TryOpenRegisteredHardlinkPublicationAsync(
                        source,
                        destination,
                        operationId.Value,
                        expectedRegisteredPhysicalObjectIdentity);
                return registeredLease;
            }

            if (recovery.StateFound)
            {
                return null;
            }
        }

        IAudiobookFileRegistrationLease? registrationLease = null;
        var publicationAction = action == FileAction.Move
            ? FileAction.Copy
            : action;
        var published = await CopyOrHardlinkPinnedFileAsync(
            publicationAction,
            source,
            destination,
            preferHardlink: action == FileAction.HardlinkCopy,
            capturePublication: lease =>
            {
                if (registrationLease != null)
                {
                    throw new InvalidOperationException(
                        "A file publication returned more than one registration lease.");
                }

                registrationLease = lease;
            },
            registrationOperationId: operationId);
        if (!published)
        {
            registrationLease?.Dispose();
            return null;
        }

        if (registrationLease == null)
        {
            throw new InvalidOperationException(
                "The file publication completed without a registration lease.");
        }

        return registrationLease;
    }

    public async Task<bool> PerformActionOn(
        FileAction action,
        string source,
        string? destination = null,
        Guid? operationId = null)
    {
        if (action == FileAction.None || destination == null) return true;
        if (await IsFilesystemAliasAsync(source, destination))
        {
            LogMutation(
                FileMutationOutcome.Blocked,
                action,
                source,
                destination,
                "Source and destination are linked aliases of the same file");
            return false;
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

        try
        {
            switch (action)
            {
                case FileAction.Move:
                    return await MoveFileAsync(source, destination, operationId);
                case FileAction.HardlinkCopy:
                    return await HardlinkFileAsync(source, destination);
                case FileAction.Copy:
                    return await CopyFileAsync(source, destination);
            }

            return false;
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            LogMutation(FileMutationOutcome.Failed, action, source, destination, exception.Message);
            throw new InvalidOperationException($"Unable to perform {action} on {source} to {destination}", exception);
        }
    }

    private async Task<IdempotentFileMoveOutcome> TryCompleteIdempotentFileMoveAsync(
        FileMoveGateLease lease,
        Guid? operationId)
    {
        var sourceFile = lease.SourcePath;
        var destFile = lease.DestinationPath;
        var equivalence = await TryDetermineFilesystemPathEquivalenceAsync(
            sourceFile,
            destFile);
        if (equivalence == true)
        {
            return IdempotentFileMoveOutcome.Completed;
        }

        bool endpointsExist;
        using (var sourceEntry = lease.SourceParent.TryOpenExistingFile(
            lease.SourceName,
            requireDeleteAccess: false))
        using (var destinationEntry = lease.DestinationParent.TryOpenExistingFile(
            lease.DestinationName,
            requireDeleteAccess: false))
        {
            endpointsExist = sourceEntry != null && destinationEntry != null;
        }

        if (equivalence == null || !endpointsExist)
        {
            return IdempotentFileMoveOutcome.NotApplicable;
        }

        // Release the observation handles before opening the same entries with
        // delete access. Keeping them alive can self-block the pinned claim on
        // Windows and bypass the serialized move protocol.
        var removalOutcome = await TryRemoveVerifiedFileMoveSourceWithClaimsAsync(
            lease,
            operationId);
        if (removalOutcome == VerifiedFileMoveRemovalOutcome.NotRemoved)
        {
            return IdempotentFileMoveOutcome.SourcePathRecreated;
        }

        if (removalOutcome == VerifiedFileMoveRemovalOutcome.PathRecreated)
        {
            return IdempotentFileMoveOutcome.SourcePathRecreated;
        }

        LogMutation(
            FileMutationOutcome.Skipped,
            FileAction.Move,
            sourceFile,
            destFile,
            "Destination already has identical content; source removed");
        return IdempotentFileMoveOutcome.Completed;
    }

    private async Task<SameContentShortcutOutcome> TrySkipSameContentAsync(
        FileAction action,
        string sourceFile,
        string destFile)
    {
        if (!File.Exists(sourceFile) || !File.Exists(destFile))
        {
            return SameContentShortcutOutcome.NotApplicable;
        }

        if (await IsFilesystemAliasAsync(sourceFile, destFile))
        {
            LogMutation(
                FileMutationOutcome.Blocked,
                action,
                sourceFile,
                destFile,
                "Source and destination became linked aliases before the same-content shortcut");
            return SameContentShortcutOutcome.Blocked;
        }

        if (!await FileSystemSafety.FilesHaveSameContentAsync(sourceFile, destFile))
        {
            return SameContentShortcutOutcome.NotApplicable;
        }

        if (await IsFilesystemAliasAsync(sourceFile, destFile))
        {
            LogMutation(
                FileMutationOutcome.Blocked,
                action,
                sourceFile,
                destFile,
                "Source and destination became linked aliases before the same-content shortcut");
            return SameContentShortcutOutcome.Blocked;
        }

        if (!await FileSystemSafety.FilesHaveSameContentAsync(sourceFile, destFile))
        {
            return SameContentShortcutOutcome.NotApplicable;
        }

        LogMutation(
            FileMutationOutcome.Skipped,
            action,
            sourceFile,
            destFile,
            "Destination already has identical content");
        return SameContentShortcutOutcome.Completed;
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
