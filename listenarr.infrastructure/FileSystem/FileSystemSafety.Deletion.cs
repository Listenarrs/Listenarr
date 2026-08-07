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

            if (!Directory.Exists(normalizedDirectory))
            {
                if (!File.Exists(normalizedDirectory))
                {
                    return true;
                }

                reason = "Directory deletion was blocked because the target path is occupied by a file.";
                return false;
            }

            if ((File.GetAttributes(normalizedDirectory) & FileAttributes.ReparsePoint) != 0)
            {
                reason = "Directory deletion was blocked because the target is a symbolic link or reparse point.";
                return false;
            }

            if (Directory.EnumerateFileSystemEntries(normalizedDirectory).Any())
            {
                reason = "Directory deletion was blocked because the target is not empty.";
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

            using var pinnedDirectory =
                PinnedDirectoryCreation.OpenExistingForPublication(
                    parentPath,
                    directoryName);
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

            if (!File.Exists(normalizedFile))
            {
                return !Directory.Exists(normalizedFile);
            }

            var parentPath = Path.GetDirectoryName(normalizedFile);
            var fileName = Path.GetFileName(normalizedFile);
            if (string.IsNullOrWhiteSpace(parentPath)
                || string.IsNullOrWhiteSpace(fileName))
            {
                reason = "File deletion was blocked because its parent could not be pinned.";
                return false;
            }

            using var parent =
                PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parentPath);
            using var entry = parent.OpenExistingFile(
                fileName,
                requireDeleteAccess: true);
            if (!string.IsNullOrWhiteSpace(expectedPhysicalObjectIdentity)
                && !string.Equals(
                    entry.GetObjectIdentity(),
                    expectedPhysicalObjectIdentity,
                    StringComparison.Ordinal))
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
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            reason = $"File deletion failed safely: {exception.GetType().Name}.";
            return false;
        }
    }
}
