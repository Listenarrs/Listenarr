using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads.Import;

public partial class DownloadImportService
{
    private async Task<bool> PerformOwnedFileActionAsync(
        FileAction action,
        string source,
        string destination,
        string managedBoundary,
        FileSystemPathSemantics semantics,
        Guid operationId,
        int audiobookId,
        CancellationToken cancellationToken)
    {
        if (!await EnsureOwnedImportDestinationAsync(
                source,
                destination,
                managedBoundary,
                semantics,
                operationId,
                audiobookId,
                cancellationToken))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await fileMover.PerformActionOn(
            action,
            source,
            destination,
            operationId);
    }

    private async Task<IAudiobookFileRegistrationLease?> PrepareOwnedFileActionForRegistrationAsync(
        FileAction action,
        string source,
        string destination,
        string managedBoundary,
        FileSystemPathSemantics semantics,
        Guid operationId,
        string? expectedRegisteredPhysicalObjectIdentity,
        int audiobookId,
        CancellationToken cancellationToken)
    {
        if (!await EnsureOwnedImportDestinationAsync(
                source,
                destination,
                managedBoundary,
                semantics,
                operationId,
                audiobookId,
                cancellationToken))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (action == FileAction.HardlinkCopy
            && !string.IsNullOrWhiteSpace(
                expectedRegisteredPhysicalObjectIdentity))
        {
            return await fileMover.PrepareActionForRegistrationAsync(
                action,
                source,
                destination,
                operationId,
                expectedRegisteredPhysicalObjectIdentity);
        }

        return await fileMover.PrepareActionForRegistrationAsync(
            action,
            source,
            destination,
            operationId);
    }

    private async Task<bool> EnsureOwnedImportDestinationAsync(
        string source,
        string destination,
        string managedBoundary,
        FileSystemPathSemantics semantics,
        Guid operationId,
        int audiobookId,
        CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException(
                "The import destination has no parent directory.");
        var audiobook = await audiobookRepository.GetByIdSnapshotAsync(
            audiobookId,
            cancellationToken);
        if (audiobook == null)
        {
            logger.LogWarning(
                "Blocked download import because audiobook {AudiobookId} disappeared before destination ownership could be verified",
                audiobookId);
            return false;
        }

        var ownership = await audiobookFileService.CheckAudiobookFileOwnershipAsync(
            audiobook,
            destination,
            destinationDirectory,
            cancellationToken);
        if (ownership.Outcome is not (
                AudiobookFileOwnershipCheckOutcome.Available or
                AudiobookFileOwnershipCheckOutcome.AlreadyOwnedByAudiobook))
        {
            logger.LogWarning(
                "Blocked download import because destination ownership is unavailable. Audiobook {AudiobookId}, Source {Source}, Destination {Destination}, Outcome {Outcome}, Reason {Reason}",
                audiobookId,
                source,
                destination,
                ownership.Outcome,
                ownership.Reason);
            return false;
        }

        await directoryOwnershipStore.EnsureCreatedHierarchyAsync(
            destinationDirectory,
            managedBoundary,
            semantics,
            "download-import",
            operationId,
            audiobookId,
            cancellationToken);
        return true;
    }
}
