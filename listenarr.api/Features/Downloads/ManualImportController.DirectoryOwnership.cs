using Listenarr.Domain.Common;

namespace Listenarr.Api.Features.Downloads;

public partial class ManualImportController
{
    private async Task<IAudiobookFileRegistrationLease?> PrepareOwnedManualImportActionForRegistrationAsync(
        FileAction action,
        string source,
        string destination,
        Audiobook audiobook,
        IReadOnlyCollection<RootFolder> rootFolders,
        FileSystemPathSemantics semantics,
        string fallbackBoundary,
        Guid operationId,
        string? expectedRegisteredPhysicalObjectIdentity,
        CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException(
                "The manual import destination has no parent directory.");
        var boundary = LibraryDirectoryOwnershipPlanning.SelectMostSpecificBoundary(
            destinationDirectory,
            rootFolders.Select(root => root.Path),
            semantics);
        boundary ??= fallbackBoundary;
        if (string.IsNullOrWhiteSpace(boundary))
        {
            throw new InvalidOperationException(
                "The manual import destination has no managed ownership boundary.");
        }

        await _directoryOwnershipStore.EnsureCreatedHierarchyAsync(
            destinationDirectory,
            boundary,
            semantics,
            "manual-import",
            operationId,
            audiobook.Id,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        if (action == FileAction.HardlinkCopy
            && !string.IsNullOrWhiteSpace(
                expectedRegisteredPhysicalObjectIdentity))
        {
            return await _fileMover.PrepareActionForRegistrationAsync(
                action,
                source,
                destination,
                operationId,
                expectedRegisteredPhysicalObjectIdentity);
        }

        return await _fileMover.PrepareActionForRegistrationAsync(
            action,
            source,
            destination,
            operationId);
    }
}
