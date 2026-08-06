using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task<AudiobookContentMoveRequest> WithValidatedTargetDirectoryOwnershipAsync(
        AudiobookContentMoveRequest request,
        CancellationToken cancellationToken)
    {
        if (request.TargetDirectoryOwnership != null || !Directory.Exists(request.Target))
        {
            return request;
        }

        var ownership = await LoadValidatedTargetDirectoryOwnershipAsync(
            request.Target,
            request.TargetSemantics,
            cancellationToken);
        return request with { TargetDirectoryOwnership = ownership };
    }

    private async Task<LibraryDirectoryOwnership?> LoadValidatedTargetDirectoryOwnershipAsync(
        string target,
        FileSystemPathSemantics targetSemantics,
        CancellationToken cancellationToken)
    {
        var resolution = await directoryOwnershipStore.ResolveOwnedAsync(
            target,
            targetSemantics,
            cancellationToken);
        if (resolution.State == LibraryDirectoryOwnershipResolutionState.Unowned)
        {
            return null;
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
                target,
                targetSemantics)
            || ownership.State == LibraryDirectoryOwnershipState.Removing)
        {
            throw new MoveNeedsAttentionException(
                "Durable target-directory ownership does not match the exact move target.");
        }

        RevalidateTargetDirectoryOwnership(ownership);
        return ownership;
    }

    private static void RevalidateTargetDirectoryOwnership(
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
            using var parent = PinnedDirectoryCreation.OpenPinnedBoundary(parentPath);
            using var directory = parent.OpenExistingChild(
                Path.GetFileName(ownership.CanonicalPath));
            if (!ManagedDirectoryIdentity.Matches(
                    ownership.DirectoryObjectIdentityVersion,
                    ownership.DirectoryObjectIdentity,
                    ownership.OwnershipToken,
                    directory.GetDirectoryObjectIdentity())
                || !directory.VisiblePathMatches()
                || !parent.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The target directory no longer matches its persisted physical ownership generation.");
            }
        }
        catch (Exception exception) when (exception is
            ArgumentException or IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException
                or PathTooLongException or System.ComponentModel.Win32Exception)
        {
            throw new MoveNeedsAttentionException(
                $"The target-directory ownership changed: {exception.Message}");
        }
    }

    private static bool IsValidatedTargetOwnershipMarker(
        string path,
        LibraryDirectoryOwnership? ownership,
        FileSystemPathSemantics semantics) =>
        ownership != null
        && LibraryDirectoryOwnershipMarker.GetMarkerPaths(ownership)
            .Any(marker => FileSystemPathIdentity.AreEquivalent(marker, path, semantics));

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
            if (!Directory.Exists(ownership.CanonicalPath))
            {
                throw new MoveNeedsAttentionException(
                    "A durably owned source directory is missing.");
            }
        }

        return ownerships;
    }

    private async Task<IReadOnlyList<LibraryDirectoryOwnership>> LoadOwnedSourceDirectoriesForCleanupAsync(
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
                $"Durable source-directory ownership could not be loaded for cleanup: {exception.Message}");
        }

        foreach (var ownership in ownerships)
        {
            try
            {
                if (ownership.State == LibraryDirectoryOwnershipState.Removing)
                {
                    LibraryDirectoryOwnershipRemoval.ValidateRecoverableState(ownership);
                    continue;
                }

                if (!Directory.Exists(ownership.CanonicalPath))
                {
                    throw new InvalidOperationException(
                        "A durably owned source directory is missing without a removal intent.");
                }
            }
            catch (InvalidOperationException exception)
            {
                throw new MoveNeedsAttentionException(
                    $"A source-directory ownership marker is invalid during cleanup: {exception.Message}");
            }
        }

        return ownerships;
    }

    private static IReadOnlyCollection<string> GetOwnedSourceMarkerPaths(
        string source,
        IReadOnlyCollection<LibraryDirectoryOwnership> ownerships,
        FileSystemPathSemantics sourceSemantics) =>
        ownerships
            .SelectMany(LibraryDirectoryOwnershipMarker.GetMarkerPaths)
            .Where(path => FileSystemPathIdentity.IsSameOrInside(
                path,
                source,
                sourceSemantics))
            .Distinct(sourceSemantics.Comparer)
            .ToList();

    private void TryDeleteRetiredOwnershipMarker(
        LibraryDirectoryOwnership ownership)
    {
        if (LibraryDirectoryOwnershipMarker.TryDeleteRetiredSiblingMarker(
                ownership,
                out var reason))
        {
            return;
        }

        logger.LogWarning(
            "The retired directory ownership marker for {DirectoryPath} could not be deleted: {Reason}",
            LogRedaction.SanitizeFilePath(ownership.CanonicalPath),
            LogRedaction.SanitizeText(reason));
    }

    private async Task<bool> ResumeOwnedDirectoryRemovalAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        LibraryDirectoryOwnership ownership,
        CancellationToken cancellationToken)
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
                cancellationToken);
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
        TryDeleteRetiredOwnershipMarker(ownership);
        return true;
    }

    private async Task CleanupOwnedSourceDirectoriesAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        IReadOnlyCollection<LibraryDirectoryOwnership> ownerships,
        FileSystemPathSemantics sourceSemantics,
        CancellationToken cancellationToken)
    {
        foreach (var ownership in ownerships
            .OrderByDescending(item => item.CanonicalPath.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(ownership.CanonicalPath))
            {
                if (ownership.State != LibraryDirectoryOwnershipState.Removing)
                {
                    throw new MoveNeedsAttentionException(
                        "A durably owned source directory disappeared without a cleanup intent.");
                }

                await ResumeOwnedDirectoryRemovalAsync(
                    request,
                    source,
                    target,
                    ownership,
                    cancellationToken);
                continue;
            }

            var resolution = await directoryOwnershipStore.ResolveOwnedAsync(
                ownership.CanonicalPath,
                sourceSemantics,
                cancellationToken);
            var current = resolution.State == LibraryDirectoryOwnershipResolutionState.Owned
                ? resolution.Ownership
                : null;
            if (current == null || current.Id != ownership.Id)
            {
                throw new MoveNeedsAttentionException(
                    resolution.Reason
                        ?? "Durable source-directory ownership changed before cleanup.");
            }

            if (current.State == LibraryDirectoryOwnershipState.Removing)
            {
                await ResumeOwnedDirectoryRemovalAsync(
                    request,
                    source,
                    target,
                    current,
                    cancellationToken);
                continue;
            }

            var remainingEntries = Directory.EnumerateFileSystemEntries(current.CanonicalPath)
                .Take(1)
                .ToList();
            if (remainingEntries.Count != 0)
            {
                continue;
            }

            await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
            var ownershipKey = current.PathOwnershipKey
                ?? throw new MoveNeedsAttentionException(
                    "The source-directory ownership key is unavailable.");
            await directoryOwnershipStore.BeginRemovalAsync(
                current.Id,
                ownershipKey,
                cancellationToken);
            await ResumeOwnedDirectoryRemovalAsync(
                request,
                source,
                target,
                current,
                cancellationToken);
        }
    }
}
