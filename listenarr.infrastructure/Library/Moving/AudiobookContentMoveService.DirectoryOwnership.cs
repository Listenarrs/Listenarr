using Listenarr.Domain.Common;

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

        await TryRetireReplacedMarkerlessTargetOwnershipAsync(
            request,
            request.Target,
            cancellationToken);

        var ownership = await LoadValidatedTargetDirectoryOwnershipAsync(
            request.Target,
            request.TargetSemantics,
            cancellationToken);
        return request with { TargetDirectoryOwnership = ownership };
    }

    private async Task TryRetireReplacedMarkerlessTargetOwnershipAsync(
        AudiobookContentMoveRequest request,
        string target,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(target))
        {
            return;
        }

        var endpoints = await GetEndpointObjectIdentitiesAsync(
            request.JobId,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(endpoints.TargetDirectoryObjectIdentity))
        {
            return;
        }

        try
        {
            _ = await directoryOwnershipStore
                .TryRetireReplacedByMarkerlessMoveAsync(
                    target,
                    request.TargetSemantics,
                    request.JobId,
                    endpoints.TargetDirectoryObjectIdentity,
                    cancellationToken);
        }
        catch (Exception exception) when (exception is
            ArgumentException or IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException
                or PathTooLongException or System.ComponentModel.Win32Exception)
        {
            throw new MoveNeedsAttentionException(
                $"The markerless target ownership replacement could not be reconciled safely: {exception.Message}");
        }
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
        CancellationToken cancellationToken)
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
            cancellationToken);
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
        return true;
    }

}
