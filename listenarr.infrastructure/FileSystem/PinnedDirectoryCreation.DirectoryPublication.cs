namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    internal static PinnedDirectoryCreation OpenExistingForPublication(
        string parentPath,
        string childName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentPath);
        ValidateLeafName(childName);
        ExclusiveDirectoryCreator.InvokeBeforeOpenParentHook(parentPath);

        var parentHandle = OperatingSystem.IsWindows()
            ? OpenDirectoryWindows(parentPath, openReparsePoint: true)
            : OpenDirectoryUnix(parentPath, noFollow: true);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                EnsureWindowsParentIsNotReparsePoint(parentHandle, parentPath);
            }

            var childPath = Path.Join(parentPath, childName);
            var directoryHandle = OperatingSystem.IsWindows()
                ? OpenRelativeDirectoryWindows(
                    parentHandle,
                    childName,
                    childPath,
                    requireDeleteAccess: true)
                : OpenDirectoryAtUnix(parentHandle, childName);
            var publication = new PinnedDirectoryCreation(
                parentHandle,
                directoryHandle,
                parentPath,
                childName,
                created: true,
                parentFollowsVisibleFinalLink: false);
            if (publication.VisiblePathMatches())
            {
                return publication;
            }

            publication.Dispose();
            throw new InvalidOperationException(
                "The existing directory changed while it was being pinned for publication.");
        }
        catch
        {
            parentHandle.Dispose();
            throw;
        }
    }

    internal PinnedDirectoryAnchor PublishCreatedDirectoryAs(string finalName)
    {
        using var parentAnchor = new PinnedDirectoryAnchor(
            DuplicateSafeHandle(_parentHandle),
            _parentPath,
            _parentFollowsVisibleFinalLink);
        return PublishCreatedDirectoryTo(parentAnchor, finalName);
    }

    internal void DeletePinnedEmptyDirectory(string currentName) =>
        DeletePinnedEmptyDirectoryCore(currentName, requireImmediateNamespaceRetirement: false);

    internal void RetirePinnedEmptyDirectoryFromNamespace(string currentName) =>
        DeletePinnedEmptyDirectoryCore(currentName, requireImmediateNamespaceRetirement: true);

    private void DeletePinnedEmptyDirectoryCore(
        string currentName,
        bool requireImmediateNamespaceRetirement)
    {
        ThrowIfDisposed();
        ValidateLeafName(currentName);
        if (!Created || _directoryHandle == null || _directoryHandle.IsInvalid)
        {
            throw new InvalidOperationException(
                "A pinned directory handle is required for deletion.");
        }

        var currentPath = Path.Join(_parentPath, currentName);
        using (var currentAnchor = new PinnedDirectoryAnchor(
            DuplicateSafeHandle(_directoryHandle),
            currentPath,
            followVisibleFinalLink: false))
        {
            if (!currentAnchor.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The directory changed before pinned deletion.");
            }
        }

        if (OperatingSystem.IsWindows())
        {
            if (!requireImmediateNamespaceRetirement)
            {
                DeleteOpenedFileWindows(_directoryHandle);
                return;
            }

            // POSIX delete semantics are applied through a distinct file object. Closing
            // that handle before returning is the namespace-retirement boundary; using a
            // duplicate of _directoryHandle would keep cleanup tied to the original file
            // object's lifetime and could leave a child delete-pending while its parent is
            // retired immediately afterwards.
            using (var retirementHandle = OpenRelativeDirectoryWindows(
                _parentHandle,
                currentName,
                currentPath,
                requireDeleteAccess: true))
            {
                if (!HandlesIdentifySameDirectory(_directoryHandle, retirementHandle))
                {
                    throw new InvalidOperationException(
                        "The directory changed before immediate pinned retirement.");
                }

                DeleteOpenedFileImmediatelyWindows(
                    retirementHandle,
                    allowLegacyFallback: false);
            }

            if (Directory.Exists(currentPath))
            {
                using var visible = OpenRelativeDirectoryWindows(
                    _parentHandle,
                    currentName,
                    currentPath);
                if (HandlesIdentifySameDirectory(_directoryHandle, visible))
                {
                    throw new System.ComponentModel.Win32Exception(
                        145,
                        "The verified empty directory remained visible after immediate retirement.");
                }
            }

            return;
        }

        var retiredName = $".listenarr-retired-directory-{Guid.NewGuid():N}.state";
        RenameRelativeEntry(
            _parentHandle,
            _directoryHandle,
            currentName,
            _parentHandle,
            retiredName);
        using var reopened = OpenDirectoryAtUnix(_parentHandle, retiredName);
        if (!HandlesIdentifySameDirectory(_directoryHandle, reopened))
        {
            throw new InvalidOperationException(
                "The retired directory no longer identifies the pinned directory.");
        }

        var flags = OperatingSystem.IsMacOS() ? AtRemovedirMac : AtRemovedirLinux;
        if (UnlinkAt(
                _parentHandle.DangerousGetHandle().ToInt32(),
                retiredName,
                flags) != 0)
        {
            throw new System.ComponentModel.Win32Exception(
                System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                "Could not remove the verified empty directory.");
        }
    }

    internal PinnedDirectoryAnchor PublishCreatedDirectoryTo(
        PinnedDirectoryAnchor destinationParent,
        string finalName)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destinationParent);
        ValidateLeafName(finalName);
        if (!Created || _directoryHandle == null || _directoryHandle.IsInvalid)
        {
            throw new InvalidOperationException(
                "A pinned directory handle is required for publication.");
        }
        if (!VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The prepared directory changed before publication.");
        }
        if (!destinationParent.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The destination parent changed before directory publication.");
        }

        using var destinationHandle = destinationParent.DuplicateHandleForOperation();
        RenameRelativeEntry(
            _parentHandle,
            _directoryHandle,
            _childName,
            destinationHandle,
            finalName);
        var publishedPath = Path.Join(destinationParent.FullPath, finalName);
        var publishedAnchor = new PinnedDirectoryAnchor(
            DuplicateSafeHandle(_directoryHandle),
            publishedPath,
            followVisibleFinalLink: false);
        if (publishedAnchor.VisiblePathMatches())
        {
            return publishedAnchor;
        }

        publishedAnchor.Dispose();
        throw new InvalidOperationException(
            "The published directory does not identify the prepared pinned directory.");
    }

}
