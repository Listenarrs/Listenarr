using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task<AudiobookContentMoveRequest> WithValidatedTargetDirectoryOwnershipAsync(
        AudiobookContentMoveRequest request,
        CancellationToken cancellationToken)
    {
        if (request.TargetDirectoryOwnership != null)
        {
            RevalidateTargetDirectoryOwnership(
                request,
                request.TargetDirectoryOwnership);
            return request;
        }
        if (!TryGetMarkerlessPathAttributes(
                request.Target,
                out var targetAttributes))
        {
            return request;
        }
        if ((targetAttributes & FileAttributes.Directory) == 0
            || (targetAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new MoveNeedsAttentionException(
                "The move target changed type or became a link before durable ownership could be loaded.");
        }

        var ownership = await LoadValidatedTargetDirectoryOwnershipAsync(
            request,
            cancellationToken);
        return request with { TargetDirectoryOwnership = ownership };
    }

    private async Task<LibraryDirectoryOwnership?> LoadValidatedTargetDirectoryOwnershipAsync(
        AudiobookContentMoveRequest request,
        CancellationToken cancellationToken)
    {
        var resolution = await directoryOwnershipStore.ResolveOwnedAsync(
            request.Target,
            request.TargetSemantics,
            cancellationToken);
        if (resolution.State == LibraryDirectoryOwnershipResolutionState.Unowned)
        {
            return null;
        }
        if (resolution.State == LibraryDirectoryOwnershipResolutionState.Unavailable
            && resolution.IsTransient)
        {
            throw new IOException(
                resolution.Reason
                    ?? "Durable target-directory ownership proof is temporarily unavailable.");
        }
        if (resolution.State != LibraryDirectoryOwnershipResolutionState.Owned
            || resolution.Ownership == null)
        {
            throw new MoveNeedsAttentionException(
                resolution.Reason
                    ?? "Durable target-directory ownership is conflicting or unavailable.");
        }

        var ownership = resolution.Ownership;
        if (!FileSystemPathIdentity.AreEquivalent(
                ownership.CanonicalPath,
                request.Target,
                request.TargetSemantics)
            || ownership.State == LibraryDirectoryOwnershipState.Removing)
        {
            throw new MoveNeedsAttentionException(
                "Durable target-directory ownership does not match the exact move target.");
        }

        RevalidateTargetDirectoryOwnership(request, ownership);
        return ownership;
    }

    private static void RevalidateTargetDirectoryOwnership(
        AudiobookContentMoveRequest request,
        LibraryDirectoryOwnership? ownership)
    {
        if (ownership == null)
        {
            return;
        }

        try
        {
            var parentPath = Path.GetDirectoryName(ownership.CanonicalPath)
                ?? throw new InvalidOperationException(
                    "The target ownership path has no parent directory.");
            using var parent = OpenPinnedMoveBoundaryDescendant(
                request,
                parentPath,
                request.TargetSemantics,
                sourceBoundary: false);
            using var directory = parent.OpenExistingChild(
                Path.GetFileName(ownership.CanonicalPath));
            // Stored ownership identity is diagnostic. Path ownership and the live
            // no-follow pin authorize additive publication; the invocation retains
            // target verification leases to reject substitution before owner commit.
            if (!PinnedDirectoryVisibleOrThrowUnavailable(
                    directory,
                    "The target directory is temporarily unavailable while its ownership generation is being verified.")
                || !PinnedDirectoryVisibleOrThrowUnavailable(
                    parent,
                    "The target directory parent is temporarily unavailable while ownership is being verified."))
            {
                throw new InvalidOperationException(
                    "The target directory changed while its current ownership path was being verified.");
            }
        }
        catch (Exception exception) when (
            FileSystemSafety.IsProvenMissingPathException(exception))
        {
            throw new MoveNeedsAttentionException(
                $"The target-directory ownership disappeared: {exception.Message}");
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException)
        {
            throw new MoveNeedsAttentionException(
                $"The target-directory ownership changed: {exception.Message}");
        }
    }

    private async Task<IReadOnlyList<LibraryDirectoryOwnership>> LoadValidatedOwnedSourceDirectoriesAsync(
        string source,
        FileSystemPathSemantics sourceSemantics,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<LibraryDirectoryOwnership> ownerships;
        try
        {
            ownerships = await directoryOwnershipStore.GetOwnedWithinAsync(
                source,
                sourceSemantics,
                cancellationToken);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new MoveNeedsAttentionException(
                $"Durable source-directory ownership could not be validated: {exception.Message}");
        }

        foreach (var ownership in ownerships)
        {
            if (ownership.State == LibraryDirectoryOwnershipState.Removing)
            {
                throw new MoveNeedsAttentionException(
                    "A source directory has an interrupted ownership cleanup and cannot be moved.");
            }
        }

        return ownerships;
    }

    private async Task<LibraryDirectoryOwnership?>
        ResolveMarkerlessSourceDirectoryOwnershipAsync(
            string path,
            FileSystemPathSemantics semantics,
            CancellationToken cancellationToken)
    {
        var resolution = await directoryOwnershipStore.ResolveOwnedAsync(
            path,
            semantics,
            cancellationToken);
        if (resolution.State == LibraryDirectoryOwnershipResolutionState.Unowned)
        {
            return null;
        }
        if (resolution.State == LibraryDirectoryOwnershipResolutionState.Unavailable
            && resolution.IsTransient)
        {
            throw new IOException(
                resolution.Reason
                    ?? "Durable source-directory ownership proof is temporarily unavailable.");
        }
        if (resolution.State != LibraryDirectoryOwnershipResolutionState.Owned
            || resolution.Ownership == null)
        {
            throw new MoveNeedsAttentionException(
                resolution.Reason
                    ?? "Durable source-directory ownership is conflicting or unavailable.");
        }

        return resolution.Ownership;
    }

    private async Task<bool> RemoveMarkerlessOwnedDirectoryAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        LibraryDirectoryOwnership ownership,
        CancellationToken cancellationToken,
        PinnedDirectoryCreation.PinnedDirectoryAnchor? originalDirectory = null)
    {
        var ownershipKey = ownership.PathOwnershipKey
            ?? throw new MoveNeedsAttentionException(
                "The markerless source-directory ownership key is unavailable.");
        if (ownership.State != LibraryDirectoryOwnershipState.Removing)
        {
            await directoryOwnershipStore.BeginRemovalAsync(
                ownership.Id,
                ownershipKey,
                cancellationToken);
            ownership.State = LibraryDirectoryOwnershipState.Removing;
        }

        return await ResumeOwnedDirectoryRemovalAsync(
            request,
            source,
            target,
            ownership,
            cancellationToken,
            originalDirectory);
    }

    private async Task RetainMarkerlessOwnedDirectoryIfRemovingAsync(
        LibraryDirectoryOwnership? ownership,
        string reason,
        CancellationToken cancellationToken)
    {
        if (ownership?.State != LibraryDirectoryOwnershipState.Removing)
        {
            return;
        }

        var ownershipKey = ownership.PathOwnershipKey
            ?? throw new MoveNeedsAttentionException(
                "The markerless source-directory ownership key is unavailable while retaining the directory.");
        await directoryOwnershipStore.RetainAsync(
            ownership.Id,
            ownershipKey,
            reason,
            cancellationToken);
        ownership.State = LibraryDirectoryOwnershipState.Retained;
    }

    private async Task<bool> ResumeOwnedDirectoryRemovalAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        LibraryDirectoryOwnership ownership,
        CancellationToken cancellationToken,
        PinnedDirectoryCreation.PinnedDirectoryAnchor? originalDirectory = null)
    {
        var ownershipKey = ownership.PathOwnershipKey
            ?? throw new MoveNeedsAttentionException(
                "The interrupted source-directory cleanup no longer has an ownership key.");
        await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);

        LibraryDirectoryRemovalOutcome outcome;
        try
        {
            using var authorization = await ownershipAuthorizer.AuthorizeOwnershipAsync(
                ownership,
                cancellationToken);
            outcome = LibraryDirectoryOwnershipRemoval.RemoveEmptyDirectory(
                ownership,
                authorization.ParentAnchor,
                cancellationToken,
                originalDirectory);
        }
        catch (InvalidOperationException exception)
        {
            throw new MoveNeedsAttentionException(
                $"The interrupted source-directory cleanup could not be proven safe: {exception.Message}");
        }

        if (outcome == LibraryDirectoryRemovalOutcome.Retained)
        {
            await directoryOwnershipStore.RetainAsync(
                ownership.Id,
                ownershipKey,
                "The source directory gained content before deletion.",
                cancellationToken);
            ownership.State = LibraryDirectoryOwnershipState.Retained;
            return false;
        }

        await directoryOwnershipStore.MarkRemovedAsync(
            ownership.Id,
            ownershipKey,
            cancellationToken);
        return true;
    }

}
