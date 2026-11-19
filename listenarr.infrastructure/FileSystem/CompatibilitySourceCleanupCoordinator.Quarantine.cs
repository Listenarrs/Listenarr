namespace Listenarr.Infrastructure.FileSystem;

public sealed partial class CompatibilitySourceCleanupCoordinator
{
    private static bool ContentMatches(string path, long length, string sha256)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var parentPath = Path.GetDirectoryName(fullPath);
            var fileName = Path.GetFileName(fullPath);
            if (string.IsNullOrWhiteSpace(parentPath)
                || string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            using var parent = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parentPath);
            var outcome = parent.TryOpenExistingFileWithOutcome(
                fileName,
                requireDeleteAccess: false,
                out var openedFile);
            using var file = openedFile;
            if (outcome != PinnedFileOpenOutcome.Opened
                || file == null
                || !file.IsRegularFile()
                || !file.MatchesAsync(length, sha256, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult())
            {
                return false;
            }

            return parent.VisiblePathMatches() && file.VisiblePathMatches();
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException))
        {
            return false;
        }
    }
}
