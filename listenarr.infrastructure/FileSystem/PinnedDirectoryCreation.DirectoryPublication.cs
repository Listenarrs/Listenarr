namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    internal static PinnedDirectoryCreation OpenExistingForPublication(
        string parentPath,
        string childName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentPath);
        ValidateLeafName(childName);
        using var parentAnchor = OpenPinnedHierarchyNoFollow(
            parentPath,
            createMissing: false);
        var parentHandle = parentAnchor.DuplicateHandleForOperation();
        try
        {
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
            var publicationVisibility = publication.ProbeVisiblePathMatch();
            if (publicationVisibility == RegistrationPublicationMatchOutcome.Match)
            {
                return publication;
            }

            publication.Dispose();
            if (publicationVisibility == RegistrationPublicationMatchOutcome.Unavailable)
            {
                throw new IOException(
                    "The existing directory is temporarily unavailable while being pinned for publication.");
            }
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
        DeletePinnedEmptyDirectoryCore(currentName, requireImmediateDeletion: false);

    internal void DeletePinnedEmptyDirectoryImmediately(string currentName) =>
        DeletePinnedEmptyDirectoryCore(currentName, requireImmediateDeletion: true);

    private void DeletePinnedEmptyDirectoryCore(
        string currentName,
        bool requireImmediateDeletion)
    {
        ThrowIfDisposed();
        ValidateLeafName(currentName);
        if (!Created || _directoryHandle == null || _directoryHandle.IsInvalid)
        {
            throw new InvalidOperationException(
                "A pinned directory handle is required for deletion.");
        }

        var currentPath = Path.Join(_parentPath, currentName);
        using var parentAnchor = new PinnedDirectoryAnchor(
            DuplicateSafeHandle(_parentHandle),
            _parentPath,
            _parentFollowsVisibleFinalLink);
        var parentVisibility = parentAnchor.ProbeVisiblePathMatch();
        if (parentVisibility == RegistrationPublicationMatchOutcome.Unavailable)
        {
            throw new IOException(
                "The directory parent is temporarily unavailable before pinned deletion.");
        }
        if (parentVisibility != RegistrationPublicationMatchOutcome.Match)
        {
            throw new InvalidOperationException(
                "The directory parent changed before pinned deletion.");
        }
        using (var currentAnchor = new PinnedDirectoryAnchor(
            DuplicateSafeHandle(_directoryHandle),
            currentPath,
            followVisibleFinalLink: false))
        {
            var currentVisibility = currentAnchor.ProbeVisiblePathMatch();
            if (currentVisibility == RegistrationPublicationMatchOutcome.Unavailable)
            {
                throw new IOException(
                    "The directory is temporarily unavailable before pinned deletion.");
            }
            if (currentVisibility != RegistrationPublicationMatchOutcome.Match)
            {
                throw new InvalidOperationException(
                    "The directory changed before pinned deletion.");
            }
        }

        if (OperatingSystem.IsWindows())
        {
            if (!requireImmediateDeletion)
            {
                DeleteOpenedFileWindows(_directoryHandle);
                return;
            }

            // POSIX delete semantics are applied through a distinct file object. Closing
            // that handle before returning is the immediate-deletion boundary; using a
            // duplicate of _directoryHandle would keep cleanup tied to the original file
            // object's lifetime and could leave a child delete-pending while its parent is
            // deleted immediately afterwards.
            using (var deletionHandle = OpenRelativeDirectoryWindows(
                _parentHandle,
                currentName,
                currentPath,
                requireDeleteAccess: true))
            {
                if (!HandlesIdentifySameDirectory(_directoryHandle, deletionHandle))
                {
                    throw new InvalidOperationException(
                        "The directory changed before immediate pinned deletion.");
                }

                DeleteOpenedFileImmediatelyWindows(
                    deletionHandle,
                    allowLegacyFallback: false);
            }

            try
            {
                using var visible = OpenRelativeDirectoryWindows(
                    _parentHandle,
                    currentName,
                    currentPath);
                if (HandlesIdentifySameDirectory(_directoryHandle, visible))
                {
                    throw new System.ComponentModel.Win32Exception(
                        145,
                        "The verified empty directory remained visible after immediate deletion.");
                }
                // A different generation may have been created at the pathname after
                // the verified directory was removed. It is not owned by this deletion.
            }
            catch (System.ComponentModel.Win32Exception exception) when (
                exception.NativeErrorCode is 2 or 3)
            {
                // The parent-relative reopen is the namespace disappearance proof.
            }

            var postDeleteParentVisibility = parentAnchor.ProbeVisiblePathMatch();
            if (postDeleteParentVisibility == RegistrationPublicationMatchOutcome.Unavailable)
            {
                throw new IOException(
                    "The directory parent became temporarily unavailable after pinned deletion.");
            }
            if (postDeleteParentVisibility != RegistrationPublicationMatchOutcome.Match)
            {
                throw new InvalidOperationException(
                    "The directory parent changed while pinned deletion was completing.");
            }
            return;
        }

        PinnedFilesystemMutationHooks.InvokeBeforeUnixDirectoryDeleteRevalidation(
            currentPath);
        using var reopened = OpenDirectoryAtUnix(_parentHandle, currentName);
        if (!HandlesIdentifySameDirectory(_directoryHandle, reopened))
        {
            throw new InvalidOperationException(
                "The empty directory changed before handle-relative deletion.");
        }

        var flags = OperatingSystem.IsMacOS() ? AtRemovedirMac : AtRemovedirLinux;
        if (UnlinkAt(
                _parentHandle.DangerousGetHandle().ToInt32(),
                currentName,
                flags) != 0)
        {
            throw new System.ComponentModel.Win32Exception(
                System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                "Could not remove the verified empty directory.");
        }
        var unixPostDeleteParentVisibility = parentAnchor.ProbeVisiblePathMatch();
        if (unixPostDeleteParentVisibility == RegistrationPublicationMatchOutcome.Unavailable)
        {
            throw new IOException(
                "The directory parent became temporarily unavailable after pinned deletion.");
        }
        if (unixPostDeleteParentVisibility != RegistrationPublicationMatchOutcome.Match)
        {
            throw new InvalidOperationException(
                "The directory parent changed while pinned deletion was completing.");
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
        var sourceVisibility = ProbeVisiblePathMatch();
        if (sourceVisibility == RegistrationPublicationMatchOutcome.Unavailable)
        {
            throw new IOException(
                "The prepared directory is temporarily unavailable before publication.");
        }
        if (sourceVisibility != RegistrationPublicationMatchOutcome.Match)
        {
            throw new InvalidOperationException(
                "The prepared directory changed before publication.");
        }
        var destinationVisibility = destinationParent.ProbeVisiblePathMatch();
        if (destinationVisibility == RegistrationPublicationMatchOutcome.Unavailable)
        {
            throw new IOException(
                "The destination parent is temporarily unavailable before directory publication.");
        }
        if (destinationVisibility != RegistrationPublicationMatchOutcome.Match)
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
        var publishedVisibility = publishedAnchor.ProbeVisiblePathMatch();
        if (publishedVisibility == RegistrationPublicationMatchOutcome.Match)
        {
            return publishedAnchor;
        }

        publishedAnchor.Dispose();
        if (publishedVisibility == RegistrationPublicationMatchOutcome.Unavailable)
        {
            throw new IOException(
                "The published directory is temporarily unavailable after publication.");
        }
        throw new InvalidOperationException(
            "The published directory does not identify the prepared pinned directory.");
    }

}
