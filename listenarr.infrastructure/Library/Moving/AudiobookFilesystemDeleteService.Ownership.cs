using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class AudiobookFilesystemDeleteService
{
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
                var exactOwnership = deleteTarget.OwnedDirectories.FirstOrDefault(
                    ownership => FileSystemPathIdentity.AreEquivalent(
                        ownership.CanonicalPath,
                        deleteTarget.FolderPath,
                        deleteTarget.Semantics));
                if (exactOwnership != null
                    && (!string.IsNullOrWhiteSpace(
                            exactOwnership.DirectoryObjectIdentityUnavailableReason)
                        || !ManagedDirectoryIdentity.Matches(
                            exactOwnership.DirectoryObjectIdentityVersion,
                            exactOwnership.DirectoryObjectIdentity,
                            exactOwnership.OwnershipToken,
                            target.GetDirectoryObjectIdentity())))
                {
                    throw new InvalidOperationException(
                        "The audiobook folder differs from its durable ownership identity.");
                }
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
                "The audiobook folder could not be bound to its managed physical directory, so its contents were not deleted.");
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
                "Durable directory ownership could not be validated, so only tracked audiobook files were deleted.");
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

    private async Task<bool> RetireOwnedDirectoryAsync(
        LibraryDirectoryOwnership ownership,
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
        if (_ownershipAuthorizer == null)
        {
            throw new InvalidOperationException(
                "Managed-root ownership authorization is unavailable.");
        }

        using var authorization = await _ownershipAuthorizer.AuthorizeOwnershipAsync(
            ownership,
            cancellationToken);
        var outcome = LibraryDirectoryOwnershipRemoval.RemoveEmptyDirectory(
            ownership,
            authorization.ParentAnchor,
            cancellationToken);
        if (outcome == LibraryDirectoryRemovalOutcome.Retained)
        {
            await _directoryOwnershipStore.RetainAsync(
                ownership.Id,
                ownershipKey,
                "The directory gained content before explicit library deletion completed.",
                cancellationToken);
            ownership.State = LibraryDirectoryOwnershipState.Retained;
            return false;
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

        await RetireOwnedDirectoryAsync(
            resolution.Ownership,
            cancellationToken);
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
        CancellationToken cancellationToken = default)
    {
        foreach (var ownership in ownerships
            .OrderByDescending(candidate => candidate.CanonicalPath.Length))
        {
            if (!await RetireOwnedDirectoryAsync(ownership, cancellationToken))
            {
                return false;
            }
        }

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
