using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class AudiobookFilesystemDeleteService
{
    internal static bool VerifyTrackedFileCleanupComplete(
        IReadOnlyCollection<string> trackedFilePaths)
        => VerifyTrackedFileCleanupCompleteCoreAsync(trackedFilePaths,
            path => Task.FromResult(PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                Path.GetDirectoryName(path)!, createMissing: false))).GetAwaiter().GetResult();

    private Task<bool> VerifyTrackedFileCleanupCompleteAsync(
        IReadOnlyCollection<string> trackedFilePaths,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken) =>
        VerifyTrackedFileCleanupCompleteCoreAsync(trackedFilePaths,
            path => OpenPinnedDeleteFileParentAsync(path, semantics, cancellationToken));

    private static async Task<bool> VerifyTrackedFileCleanupCompleteCoreAsync(
        IReadOnlyCollection<string> trackedFilePaths,
        Func<string, Task<PinnedDirectoryCreation.PinnedDirectoryAnchor>> openParent)
    {
        foreach (var trackedPath in trackedFilePaths)
        {
            var parentPath = Path.GetDirectoryName(trackedPath);
            var fileName = Path.GetFileName(trackedPath);
            if (string.IsNullOrWhiteSpace(parentPath)
                || string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            PinnedDirectoryCreation.PinnedDirectoryAnchor parent;
            try
            {
                parent = await openParent(trackedPath);
            }
            catch (Exception exception) when (
                FileSystemSafety.IsProvenMissingPathException(exception))
            {
                continue;
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException
                    or ArgumentException or InvalidOperationException
                    or NotSupportedException or PathTooLongException
                    or System.ComponentModel.Win32Exception)
            {
                return false;
            }

            using (parent)
            {
                var outcome = parent.TryOpenExistingFileWithOutcome(
                    fileName,
                    requireDeleteAccess: false,
                    out var openedEntry);
                using var entry = openedEntry;
                if (outcome == PinnedFileOpenOutcome.NotFound)
                {
                    if (!parent.VisiblePathMatches())
                    {
                        return false;
                    }

                    continue;
                }
                if (outcome != PinnedFileOpenOutcome.Opened || entry == null
                    || !parent.VisiblePathMatches()
                    || !entry.VisiblePathMatches())
                {
                    return false;
                }

                // A currently visible tracked path means cleanup is incomplete.
                // Persisted physical identity is deliberately irrelevant here.
                return false;
            }
        }

        return true;
    }
}
