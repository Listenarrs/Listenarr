using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task RemoveEmptyDirectoryTreeAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        string directory,
        string boundary,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken)
    {
        var current = directory;
        while (Directory.Exists(current)
            && !FileSystemPathIdentity.AreEquivalent(
                current,
                boundary,
                semantics))
        {
            if (!FileSystemSafety.TryValidateMutationTarget(
                    current,
                    [boundary],
                    out current,
                    out var reason))
            {
                throw new MoveNeedsAttentionException(reason);
            }

            var ownership = await ResolveOwnedDirectoryForCleanupAsync(
                current,
                semantics,
                cancellationToken);
            if (ownership == null)
            {
                // A cleanup boundary is only an upper fence. Without a durable
                // ownership claim, an empty ancestor has no deletion authority.
                return;
            }
            if (ownership.State == LibraryDirectoryOwnershipState.Removing)
            {
                var interruptedRemovalCompleted = await ResumeOwnedDirectoryRemovalAsync(
                    request,
                    source,
                    target,
                    ownership,
                    cancellationToken);
                if (!interruptedRemovalCompleted)
                {
                    return;
                }

                current = Path.GetDirectoryName(current) ?? boundary;
                continue;
            }

            ValidateExistingMoveDirectory(current, "source ancestor cleanup directory");
            if (Directory.EnumerateFileSystemEntries(current).Any())
            {
                return;
            }

            faultInjector?.OnMoveFinalization(
                request.JobId,
                MoveFinalizationFaultPoint.BeforeSourceAncestorDelete);
            await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
            var finalOwnership = await ResolveOwnedDirectoryForCleanupAsync(
                current,
                semantics,
                cancellationToken);
            if (finalOwnership == null
                || finalOwnership.Id != ownership.Id
                || !string.Equals(
                    finalOwnership.PathOwnershipKey,
                    ownership.PathOwnershipKey,
                    StringComparison.Ordinal))
            {
                throw new MoveNeedsAttentionException(
                    "The durable directory ownership claim changed before source-parent cleanup.");
            }

            ValidateExistingMoveDirectory(current, "source ancestor cleanup directory");
            if (Directory.EnumerateFileSystemEntries(current).Any())
            {
                return;
            }

            var ownershipKey = finalOwnership.PathOwnershipKey
                ?? throw new MoveNeedsAttentionException(
                    "The durable directory ownership key is unavailable.");
            await directoryOwnershipStore.BeginRemovalAsync(
                finalOwnership.Id,
                ownershipKey,
                cancellationToken);
            var removalCompleted = await ResumeOwnedDirectoryRemovalAsync(
                request,
                source,
                target,
                finalOwnership,
                cancellationToken);
            if (!removalCompleted)
            {
                return;
            }

            current = Path.GetDirectoryName(current) ?? boundary;
        }
    }

    private static bool IsSourceCleanupBoundary(
        string path,
        string? boundary,
        FileSystemPathSemantics semantics)
    {
        if (string.IsNullOrWhiteSpace(boundary))
        {
            return false;
        }

        try
        {
            return FileSystemPathIdentity.AreEquivalent(path, boundary, semantics);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException)
        {
            throw new MoveNeedsAttentionException(
                $"The source cleanup boundary is invalid: {exception.Message}");
        }
    }

    private async Task RemoveEmptySourceAncestorsAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        string? boundary,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(boundary))
        {
            return;
        }

        var fullBoundary = Path.GetFullPath(boundary);
        var current = Path.GetDirectoryName(Path.GetFullPath(source));
        while (current != null
            && FileSystemPathIdentity.IsSameOrInside(current, fullBoundary, semantics))
        {
            if (FileSystemPathIdentity.AreEquivalent(current, fullBoundary, semantics))
            {
                return;
            }

            if (Directory.Exists(current))
            {
                await RemoveEmptyDirectoryTreeAsync(
                    request,
                    source,
                    target,
                    current,
                    fullBoundary,
                    semantics,
                    cancellationToken);
                return;
            }

            var ownership = await ResolveOwnedDirectoryForCleanupAsync(
                current,
                semantics,
                cancellationToken);
            if (ownership != null)
            {
                if (ownership.State != LibraryDirectoryOwnershipState.Removing)
                {
                    throw new MoveNeedsAttentionException(
                        "An owned source-parent directory disappeared without a durable cleanup intent.");
                }

                await ResumeOwnedDirectoryRemovalAsync(
                    request,
                    source,
                    target,
                    ownership,
                    cancellationToken);
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private async Task<LibraryDirectoryOwnership?> ResolveOwnedDirectoryForCleanupAsync(
        string directory,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken)
    {
        var resolution = await directoryOwnershipStore.ResolveOwnedAsync(
            directory,
            semantics,
            cancellationToken);
        return resolution.State switch
        {
            LibraryDirectoryOwnershipResolutionState.Owned
                when resolution.Ownership != null => resolution.Ownership,
            LibraryDirectoryOwnershipResolutionState.Unowned => null,
            LibraryDirectoryOwnershipResolutionState.Conflict =>
                throw new MoveNeedsAttentionException(
                    resolution.Reason
                        ?? "Conflicting durable directory ownership claims prevent cleanup."),
            LibraryDirectoryOwnershipResolutionState.Unavailable
                when resolution.IsTransient =>
                throw new IOException(
                    resolution.Reason
                        ?? "Durable directory ownership proof is temporarily unavailable for cleanup."),
            LibraryDirectoryOwnershipResolutionState.Unavailable =>
                throw new MoveNeedsAttentionException(
                    resolution.Reason
                        ?? "Durable directory ownership is unavailable for cleanup."),
            _ => throw new MoveNeedsAttentionException(
                "Durable directory ownership could not be resolved for cleanup.")
        };
    }
}
