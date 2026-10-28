using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Renaming;

public partial class RenameService
{
    private async Task EnsureOwnedRenameHierarchyAsync(
        string destinationDirectory,
        IReadOnlyCollection<string> allowedRoots,
        FileSystemPathSemantics semantics,
        int audiobookId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var boundary = LibraryDirectoryOwnershipPlanning.SelectMostSpecificBoundary(
            destinationDirectory,
            allowedRoots,
            semantics)
            ?? throw new InvalidOperationException(
                "The rename destination is outside the allowed library roots.");
        await _directoryOwnershipStore.EnsureCreatedHierarchyAsync(
            destinationDirectory,
            boundary,
            semantics,
            "rename",
            operationId,
            audiobookId,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }
}
