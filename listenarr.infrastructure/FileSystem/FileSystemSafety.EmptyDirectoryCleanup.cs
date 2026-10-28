namespace Listenarr.Infrastructure.FileSystem;

internal static partial class FileSystemSafety
{
    private static void DeleteEmptyDirectoriesPinned(string rootPath)
    {
        var normalizedRoot = Path.GetFullPath(rootPath);
        var rootParentPath = Path.GetDirectoryName(normalizedRoot);
        var rootName = Path.GetFileName(normalizedRoot);
        if (string.IsNullOrWhiteSpace(rootParentPath)
            || string.IsNullOrWhiteSpace(rootName))
        {
            return;
        }

        using var rootParent =
            PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                rootParentPath,
                createMissing: false);
        using var rootPublication =
            rootParent.TryOpenExistingChildForPublication(rootName);
        if (rootPublication == null)
        {
            return;
        }

        using var root = rootPublication.OpenCreatedDirectoryAnchor();
        var reason = "The pinned cleanup root changed.";
        if (!rootParent.VisiblePathMatches()
            || !root.VisiblePathMatches()
            || !TryEnumerateTreeWithoutLinks(
                normalizedRoot,
                out _,
                out var directories,
                out reason)
            || !root.VisiblePathMatches())
        {
            System.Diagnostics.Debug.WriteLine(
                $"Blocked empty-directory cleanup for '{normalizedRoot}': {reason}");
            return;
        }

        foreach (var directory in directories
            .OrderByDescending(GetPathSegmentCount))
        {
            InvokeBeforeEmptyDirectoryCandidatePinHook(directory);
            if (!root.VisiblePathMatches()
                || !TryDeletePinnedEmptyDescendant(
                    root,
                    normalizedRoot,
                    directory,
                    out reason))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Blocked empty-directory cleanup candidate '{directory}': {reason}");
            }
        }

        InvokeBeforeEmptyDirectoryCandidatePinHook(normalizedRoot);
        if (!rootParent.VisiblePathMatches()
            || !root.VisiblePathMatches()
            || Directory.EnumerateFileSystemEntries(root.FullPath).Any()
            || !root.VisiblePathMatches())
        {
            return;
        }

        root.Dispose();
        rootPublication.DeletePinnedEmptyDirectory(rootName);
        rootParent.FlushDirectoryEntry();
    }

    private static bool TryDeletePinnedEmptyDescendant(
        PinnedDirectoryCreation.PinnedDirectoryAnchor root,
        string normalizedRoot,
        string directoryPath,
        out string reason)
    {
        reason = string.Empty;
        PinnedDirectoryCreation.PinnedDirectoryAnchor? currentParent = null;
        try
        {
            var normalizedDirectory = Path.GetFullPath(directoryPath);
            var relative = Path.GetRelativePath(
                normalizedRoot,
                normalizedDirectory);
            var segments = relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0
                || segments.Any(segment => segment is "." or ".."))
            {
                reason = "The cleanup candidate is outside the pinned cleanup root.";
                return false;
            }

            currentParent = root.Duplicate();
            foreach (var segment in segments[..^1])
            {
                var next = currentParent.OpenExistingChild(segment);
                currentParent.Dispose();
                currentParent = next;
            }

            var candidateName = segments[^1];
            using var candidatePublication =
                currentParent.TryOpenExistingChildForPublication(candidateName);
            if (candidatePublication == null)
            {
                return true;
            }

            using var candidate =
                candidatePublication.OpenCreatedDirectoryAnchor();
            InvokeAfterEmptyDirectoryCandidatePinHook(normalizedDirectory);
            if (!root.VisiblePathMatches()
                || !currentParent.VisiblePathMatches()
                || !candidate.VisiblePathMatches()
                || Directory.EnumerateFileSystemEntries(candidate.FullPath).Any()
                || !candidate.VisiblePathMatches())
            {
                reason = "The pinned cleanup candidate changed or is not empty.";
                return false;
            }

            candidate.Dispose();
            candidatePublication.DeletePinnedEmptyDirectory(candidateName);
            currentParent.FlushDirectoryEntry();
            return true;
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            reason = $"The cleanup candidate could not be deleted through the pinned hierarchy: {exception.GetType().Name}.";
            return false;
        }
        finally
        {
            currentParent?.Dispose();
        }
    }

    private static int GetPathSegmentCount(string path) =>
        Path.GetFullPath(path).Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries).Length;
}
