using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class AudiobookFilesystemDeleteService
{
    private async Task<bool> TryDeleteAuthorizedEmptyFolderAsync(
        DeleteFolderTarget target,
        PinnedDirectoryCreation.PinnedDirectoryAnchor originalTarget,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_ownershipAuthorizer == null
                || IsFilesystemRoot(target.FolderPath, target.Semantics)
                || target.ProtectedRoots.Any(root => PathsEqual(root, target.FolderPath, target.Semantics)))
                return false;

            using var authorization = await _ownershipAuthorizer.AuthorizeContainingRootAsync(
                target.FolderPath, target.Semantics, cancellationToken);
            var name = Path.GetFileName(target.FolderPath);
            using var publication = authorization.ParentAnchor.TryOpenExistingChildForPublication(name);
            if (publication == null)
                return authorization.ParentAnchor.VisiblePathMatches();

            using var current = publication.OpenCreatedDirectoryAnchor();
            // The explicit request permits only the exact directory that was pinned
            // before content deletion, never an empty replacement at the same path.
            if (!originalTarget.VisiblePathMatches()
                || !originalTarget.IdentifiesSameDirectory(current)
                || !current.VisiblePathMatches()
                || !authorization.ParentAnchor.VisiblePathMatches()
                || !WaitForPinnedDirectoryEmpty(current, () =>
                    originalTarget.VisiblePathMatches()
                    && authorization.ParentAnchor.VisiblePathMatches()))
                return false;

            publication.DeletePinnedEmptyDirectoryImmediately(name);
            return true;
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            _logger.LogWarning(exception,
                "Unable to remove the live authorized empty audiobook folder {FolderPath}",
                LogRedaction.SanitizeFilePath(target.FolderPath));
            return false;
        }
    }

    private async Task<PinnedDirectoryCreation.PinnedDirectoryAnchor?>
        AuthorizeDeleteTargetAsync(
            DeleteFolderTarget deleteTarget,
            AudiobookFilesystemDeleteResult result,
            CancellationToken cancellationToken)
    {
        if (_ownershipAuthorizer == null)
        {
            result.Warnings.Add(
                "Managed-root authorization is unavailable, so audiobook folder contents were not deleted.");
            return null;
        }

        try
        {
            using var rootAuthorization =
                await _ownershipAuthorizer.AuthorizeContainingRootAsync(
                    deleteTarget.FolderPath,
                    deleteTarget.Semantics,
                    cancellationToken);
            var directoryName = Path.GetFileName(deleteTarget.FolderPath);
            ExclusiveDirectoryCreator.InvokeBeforeOpenParentHook(
                deleteTarget.FolderPath);
            var target = rootAuthorization.ParentAnchor.OpenExistingChild(directoryName);
            try
            {
                // Persisted ownership describes why Listenarr may clean up this
                // canonical path; its physical identity is not cross-session delete
                // authority. The live pinned target below is the operation-local fence.
                if (!target.VisiblePathMatches())
                {
                    throw new InvalidOperationException(
                        "The audiobook folder changed while destructive access was authorized.");
                }

                return target;
            }
            catch
            {
                target.Dispose();
                throw;
            }
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException
                or StackOverflowException))
        {
            result.Warnings.Add(
                "The audiobook folder could not be bound safely to its current managed path, so its contents were not deleted.");
            _logger.LogWarning(
                exception,
                "Blocked audiobook content deletion because managed-root authorization failed for {FolderPath}",
                LogRedaction.SanitizeFilePath(deleteTarget.FolderPath));
            return null;
        }
    }

    private async Task<IReadOnlyList<LibraryDirectoryOwnership>?> ResolveOwnedDirectoriesForDeleteAsync(
        string folderPath,
        FileSystemPathSemantics semantics,
        AudiobookFilesystemDeleteResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            var ownerships = await _directoryOwnershipStore.GetOwnedWithinAsync(
                folderPath,
                semantics,
                cancellationToken);
            foreach (var ownership in ownerships)
            {
                ValidateOwnedDirectoryForDelete(ownership);
            }

            return ownerships
                .OrderByDescending(ownership => ownership.CanonicalPath.Length)
                .ToList();
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
        {
            result.Warnings.Add(
                "Directory cleanup ownership could not be validated, so only tracked audiobook files were deleted.");
            _logger.LogWarning(
                exception,
                "Blocked audiobook folder deletion because durable ownership could not be validated for {FolderPath}",
                LogRedaction.SanitizeFilePath(folderPath));
            return null;
        }
    }

    private static void ValidateOwnedDirectoryForDelete(
        LibraryDirectoryOwnership ownership)
    {
        if (ownership.State == LibraryDirectoryOwnershipState.Removing)
        {
            LibraryDirectoryOwnershipRemoval.ValidateRecoverableState(ownership);
            return;
        }

        if (!Directory.Exists(ownership.CanonicalPath))
        {
            throw new InvalidOperationException(
                "An owned directory is missing without a removal intent.");
        }

    }

    private async Task<PinnedDirectoryCreation.PinnedDirectoryAnchor?> PinOwnedDirectoryForRetirementAsync(
        LibraryDirectoryOwnership ownership,
        CancellationToken cancellationToken)
    {
        using var authorization = _ownershipAuthorizer == null
            ? throw new InvalidOperationException("Managed-root authorization is unavailable for owned directory retirement.")
            : await _ownershipAuthorizer.AuthorizeOwnershipAsync(ownership, cancellationToken);
        var parent = authorization.ParentAnchor;
        if (!parent.VisiblePathMatches())
            throw new IOException("The owned directory parent changed before live proof capture.");
        using var publication = parent.TryOpenExistingChildForPublication(
            Path.GetFileName(ownership.CanonicalPath));
        if (publication == null)
        {
            if (!parent.VisiblePathMatches())
                throw new IOException("The owned directory parent changed while absence was verified.");
            return null;
        }
        var original = publication.OpenCreatedDirectoryAnchor();
        if (parent.VisiblePathMatches() && original.VisiblePathMatches()) return original;
        original.Dispose();
        throw new IOException("The owned directory changed during live proof capture.");
    }

    private async Task<bool> RetireOwnedDirectoryAsync(
        LibraryDirectoryOwnership ownership,
        PinnedDirectoryCreation.PinnedDirectoryAnchor originalDirectory,
        CancellationToken cancellationToken = default)
    {
        var ownershipKey = ownership.PathOwnershipKey
            ?? throw new InvalidOperationException(
                "The durable directory ownership key is unavailable.");
        ValidateOwnedDirectoryForDelete(ownership);
        if (ownership.State != LibraryDirectoryOwnershipState.Removing)
        {
            await _directoryOwnershipStore.BeginRemovalAsync(
                ownership.Id,
                ownershipKey,
                cancellationToken);
            ownership.State = LibraryDirectoryOwnershipState.Removing;
        }
        var directoryPath = ownership.CanonicalPath;
        BeforeOwnedDirectoryRetirementForTest?.Invoke(directoryPath);
        using var authorization = _ownershipAuthorizer == null
            ? throw new InvalidOperationException("Managed-root authorization is unavailable for owned directory retirement.")
            : await _ownershipAuthorizer.AuthorizeOwnershipAsync(ownership, cancellationToken);
        var parent = authorization.ParentAnchor;
        var directoryName = Path.GetFileName(directoryPath);
        if (!parent.VisiblePathMatches())
            throw new IOException("The owned directory parent changed before retirement.");
        using var publication = parent.TryOpenExistingChildForPublication(directoryName);
        if (publication != null)
        {
            using var directory = publication.OpenCreatedDirectoryAnchor();
            if (!parent.VisiblePathMatches()
                || !directory.VisiblePathMatches()
                || !originalDirectory.VisiblePathMatches()
                || !originalDirectory.IdentifiesSameDirectory(directory)
                || !WaitForPinnedDirectoryEmpty(directory, () =>
                    parent.VisiblePathMatches() && originalDirectory.VisiblePathMatches()))
            {
                await _directoryOwnershipStore.RetainAsync(
                    ownership.Id,
                    ownershipKey,
                    "The directory changed before explicit library deletion completed.",
                    cancellationToken);
                ownership.State = LibraryDirectoryOwnershipState.Retained;
                return false;
            }

            publication.DeletePinnedEmptyDirectoryImmediately(directoryName);
        }
        else if (!parent.VisiblePathMatches())
        {
            throw new IOException("The owned directory parent changed while absence was verified.");
        }

        await _directoryOwnershipStore.MarkRemovedAsync(
            ownership.Id,
            ownershipKey,
            cancellationToken);
        ownership.State = LibraryDirectoryOwnershipState.Removed;
        ownership.PathOwnershipKey = null;
        return true;
    }

    private async Task RecoverMissingOwnedDirectoryAsync(
        string? directoryPath,
        FileSystemPathSemantics semantics,
        string directoryKind,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(directoryPath);
        if (string.IsNullOrWhiteSpace(normalizedPath)
            || Directory.Exists(normalizedPath))
        {
            return;
        }

        var resolution = await _directoryOwnershipStore.ResolveOwnedAsync(
            normalizedPath,
            semantics,
            cancellationToken);
        if (resolution.State == LibraryDirectoryOwnershipResolutionState.Unowned)
        {
            return;
        }
        if (resolution.State == LibraryDirectoryOwnershipResolutionState.Unavailable
            && resolution.IsTransient)
        {
            throw new IOException(
                resolution.Reason
                    ?? $"The missing {directoryKind} directory ownership proof is temporarily unavailable.");
        }
        if (resolution.State != LibraryDirectoryOwnershipResolutionState.Owned
            || resolution.Ownership == null)
        {
            throw new InvalidOperationException(
                resolution.Reason
                    ?? $"The missing {directoryKind} directory has conflicting or unavailable ownership state.");
        }
        if (resolution.Ownership.State != LibraryDirectoryOwnershipState.Removing)
        {
            throw new InvalidOperationException(
                $"The missing {directoryKind} directory has no durable interrupted-removal intent.");
        }

        if (!await ReconcileAbsentOwnedDirectoryAsync(
                resolution.Ownership, originalTarget: null, cancellationToken))
            throw new InvalidOperationException(
                "The interrupted owned directory removal could not be reconciled as absent.");
    }

    private async Task RecoverMissingOwnedAuthorParentAsync(
        Audiobook audiobook,
        string? deletedFolderPath,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken = default)
    {
        var parentFolder = NormalizePath(Path.GetDirectoryName(deletedFolderPath));
        if (string.IsNullOrWhiteSpace(parentFolder)
            || Directory.Exists(parentFolder)
            || IsFilesystemRoot(parentFolder, semantics)
            || !IsAuthorFolder(parentFolder, audiobook.Authors?.FirstOrDefault()))
        {
            return;
        }

        await RecoverMissingOwnedDirectoryAsync(
            parentFolder,
            semantics,
            "author",
            cancellationToken);
    }

    private async Task<bool> RetireOwnedHierarchyAsync(
        IReadOnlyList<LibraryDirectoryOwnership> ownerships,
        PinnedDirectoryCreation.PinnedDirectoryAnchor originalTarget,
        CapturedDeleteTreeProofs capturedTreeProofs,
        CancellationToken cancellationToken = default)
    {
        foreach (var ownership in ownerships
            .OrderByDescending(candidate => candidate.CanonicalPath.Length))
        {
            var relativePath = Path.GetRelativePath(originalTarget.FullPath, ownership.CanonicalPath);
            if (!originalTarget.VisiblePathMatches()) return false;
            if (relativePath == ".")
            {
                if (!await RetireOwnedDirectoryAsync(ownership, originalTarget, cancellationToken)) return false;
                continue;
            }
            var observation = capturedTreeProofs.GetValueOrDefault(relativePath);
            if (observation == null)
            {
                if (!await ReconcileAbsentOwnedDirectoryAsync(ownership, originalTarget, cancellationToken)) return false;
                continue;
            }
            if (_ownershipAuthorizer == null) return false;
            using var authorization = await _ownershipAuthorizer.AuthorizeOwnershipAsync(ownership, cancellationToken);
            using var publication = authorization.ParentAnchor.TryOpenExistingChildForPublication(
                Path.GetFileName(ownership.CanonicalPath));
            if (publication == null)
            {
                if (!await ReconcileAbsentOwnedDirectoryAsync(ownership, originalTarget, cancellationToken)) return false;
                continue;
            }
            using var directory = publication.OpenCreatedDirectoryAnchor();
            if (!observation.Matches(directory) || !originalTarget.VisiblePathMatches()
                || !await RetireOwnedDirectoryAsync(ownership, directory, cancellationToken)) return false;
        }

        return true;
    }

    private async Task<bool> ReconcileAbsentOwnedDirectoryAsync(
        LibraryDirectoryOwnership ownership,
        PinnedDirectoryCreation.PinnedDirectoryAnchor? originalTarget,
        CancellationToken cancellationToken)
    {
        if (ownership.State != LibraryDirectoryOwnershipState.Removing
            || ownership.PathOwnershipKey == null
            || _ownershipAuthorizer == null
            || (originalTarget != null && !originalTarget.VisiblePathMatches()))
            return false;

        // Interrupted unlink needs only a durable absence observation. A visible
        // occupant without an original operation pin must never enter retirement.
        BeforeOwnedDirectoryRetirementForTest?.Invoke(ownership.CanonicalPath);
        using var authorization = await _ownershipAuthorizer.AuthorizeOwnershipAsync(
            ownership, cancellationToken);
        var parent = authorization.ParentAnchor;
        if (!parent.VisiblePathMatches()) return false;
        using var publication = parent.TryOpenExistingChildForPublication(
            Path.GetFileName(ownership.CanonicalPath));
        if (publication != null
            || !parent.VisiblePathMatches()
            || (originalTarget != null && !originalTarget.VisiblePathMatches()))
            return false;

        await _directoryOwnershipStore.MarkRemovedAsync(
            ownership.Id, ownership.PathOwnershipKey, cancellationToken);
        ownership.State = LibraryDirectoryOwnershipState.Removed;
        ownership.PathOwnershipKey = null;
        return true;
    }

    private static bool IsFilesystemRoot(
        string? path,
        FileSystemPathSemantics semantics)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return !string.IsNullOrWhiteSpace(root)
            && FileSystemPathIdentity.AreEquivalent(root, fullPath, semantics);
    }

    private static bool IsAuthorFolder(string folderPath, string? authorName)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(authorName))
        {
            return false;
        }

        var folderName = Path.GetFileName(folderPath);
        return NormalizeName(folderName) == NormalizeName(authorName);
    }

    private static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = new string(value
            .Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))
            .ToArray());

        return string.Join(
            ' ',
            cleaned.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
    }
}
