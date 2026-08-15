namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    internal PinnedDirectoryAnchor OpenParentDirectoryAnchor()
    {
        ThrowIfDisposed();
        var anchor = new PinnedDirectoryAnchor(
            DuplicateSafeHandle(_parentHandle),
            _parentPath,
            _parentFollowsVisibleFinalLink);
        if (anchor.VisiblePathMatches())
        {
            return anchor;
        }

        anchor.Dispose();
        throw new InvalidOperationException(
            "The pinned directory parent changed while it was being opened.");
    }

    internal sealed partial class PinnedDirectoryAnchor
    {
        internal PinnedDirectoryCreation OpenExistingChildForPublication(
            string childName)
        {
            ThrowIfDisposed();
            ValidateLeafName(childName);
            var childPath = Path.Join(FullPath, childName);
            ExclusiveDirectoryCreator.InvokeBeforeOpenParentHook(childPath);
            EnsureVisiblePathMatches();
            var directoryHandle = OperatingSystem.IsWindows()
                ? OpenRelativeDirectoryWindows(
                    _handle,
                    childName,
                    childPath,
                    requireDeleteAccess: true)
                : OpenDirectoryAtUnix(_handle, childName);
            var publication = new PinnedDirectoryCreation(
                DuplicateSafeHandle(_handle),
                directoryHandle,
                FullPath,
                childName,
                created: true,
                _followVisibleFinalLink);
            if (publication.VisiblePathMatches())
            {
                return publication;
            }

            publication.Dispose();
            throw new InvalidOperationException(
                "The existing child changed while it was being pinned for publication.");
        }
    }
}
