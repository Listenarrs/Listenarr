namespace Listenarr.Infrastructure.FileSystem;

internal static partial class FileSystemSafety
{
    public static bool TryDeleteEmptyDirectory(
        string directoryPath,
        IEnumerable<string?> allowedRoots,
        out string reason)
    {
        reason = string.Empty;
        try
        {
            var roots = allowedRoots.ToList();
            if (!TryValidateMutationTarget(
                    directoryPath,
                    roots,
                    out var normalizedDirectory,
                    out reason))
            {
                return false;
            }

            var parentPath = Path.GetDirectoryName(normalizedDirectory);
            var directoryName = Path.GetFileName(normalizedDirectory);
            if (string.IsNullOrWhiteSpace(parentPath)
                || string.IsNullOrWhiteSpace(directoryName))
            {
                reason = "Directory deletion was blocked because its parent could not be pinned.";
                return false;
            }

            PinnedDirectoryCreation pinnedDirectory;
            try
            {
                pinnedDirectory = PinnedDirectoryCreation.OpenExistingForPublication(
                    parentPath,
                    directoryName);
            }
            catch (Exception exception) when (IsProvenMissingPathException(exception))
            {
                return true;
            }

            using (pinnedDirectory)
            {
                if (!TryValidateMutationTarget(
                    normalizedDirectory,
                    roots,
                    out var revalidatedDirectory,
                    out reason)
                || !StringComparer.Ordinal.Equals(normalizedDirectory, revalidatedDirectory)
                    || !pinnedDirectory.VisiblePathMatches())
                {
                    reason = string.IsNullOrWhiteSpace(reason)
                        ? "Directory deletion was blocked because the validated path changed."
                        : reason;
                    return false;
                }

                using var pinnedAnchor = pinnedDirectory.OpenCreatedDirectoryAnchor();
                if (Directory.EnumerateFileSystemEntries(pinnedAnchor.FullPath).Any()
                    || !pinnedAnchor.VisiblePathMatches())
                {
                    reason = "Directory deletion was blocked because the pinned target changed or is not empty.";
                    return false;
                }

                pinnedDirectory.DeletePinnedEmptyDirectory(directoryName);
                return true;
            }
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            reason = $"Directory deletion failed safely: {exception.GetType().Name}.";
            return false;
        }
    }

    public static bool TryDeleteFile(
        string filePath,
        IEnumerable<string?> allowedRoots,
        out string reason) =>
        TryDeleteFile(
            filePath,
            allowedRoots,
            expectedPhysicalObjectIdentity: null,
            out reason);

    public static bool TryDeleteFile(
        string filePath,
        IEnumerable<string?> allowedRoots,
        string? expectedPhysicalObjectIdentity,
        out string reason)
    {
        reason = string.Empty;
        try
        {
            var roots = allowedRoots.ToList();
            if (!TryValidateMutationTarget(
                    filePath,
                    roots,
                    out var normalizedFile,
                    out reason))
            {
                return false;
            }

            var parentPath = Path.GetDirectoryName(normalizedFile);
            var fileName = Path.GetFileName(normalizedFile);
            if (string.IsNullOrWhiteSpace(parentPath)
                || string.IsNullOrWhiteSpace(fileName))
            {
                reason = "File deletion was blocked because its parent could not be pinned.";
                return false;
            }

            PinnedDirectoryCreation.PinnedDirectoryAnchor parent;
            try
            {
                parent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                    parentPath,
                    createMissing: false);
            }
            catch (Exception exception) when (IsProvenMissingPathException(exception))
            {
                return true;
            }

            using (parent)
            {
                var outcome = parent.TryOpenExistingFileForStableDeleteWithOutcome(
                    fileName,
                    out var openedEntry);
                using var entry = openedEntry;
                if (outcome == PinnedFileOpenOutcome.NotFound)
                {
                    if (!parent.VisiblePathMatches())
                    {
                        reason = "File deletion was blocked because its parent changed while absence was being proved.";
                        return false;
                    }

                    return true;
                }
                if (outcome != PinnedFileOpenOutcome.Opened || entry == null)
                {
                    reason = "File deletion was blocked because the target could not be inspected safely.";
                    return false;
                }
                if (!string.IsNullOrWhiteSpace(expectedPhysicalObjectIdentity)
                    && !entry.MatchesObjectIdentity(expectedPhysicalObjectIdentity))
                {
                    reason =
                        "File deletion was blocked because the target physical generation no longer matches the tracked audiobook file.";
                    return false;
                }

                if (!TryValidateMutationTarget(
                        normalizedFile,
                        roots,
                        out var revalidatedFile,
                        out reason)
                    || !StringComparer.Ordinal.Equals(normalizedFile, revalidatedFile)
                    || !parent.VisiblePathMatches()
                    || !entry.VisiblePathMatches())
                {
                    reason = string.IsNullOrWhiteSpace(reason)
                        ? "File deletion was blocked because the validated path changed."
                        : reason;
                    return false;
                }

                entry.Delete();
                return true;
            }
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            reason = $"File deletion failed safely: {exception.GetType().Name}.";
            return false;
        }
    }

    internal static bool IsProvenMissingPathException(Exception exception) =>
        exception is DirectoryNotFoundException or FileNotFoundException
        || exception is System.ComponentModel.Win32Exception win32
            && (OperatingSystem.IsWindows()
                ? win32.NativeErrorCode is 2 or 3
                : win32.NativeErrorCode == 2);
}
