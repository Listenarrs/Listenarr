namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class AudiobookFilesystemDeleteService
{
    internal static bool VerifyTrackedFileCleanupComplete(
        IReadOnlyDictionary<string, string> trackedPhysicalObjectIdentities)
    {
        foreach (var tracked in trackedPhysicalObjectIdentities)
        {
            var parentPath = Path.GetDirectoryName(tracked.Key);
            var fileName = Path.GetFileName(tracked.Key);
            if (string.IsNullOrWhiteSpace(parentPath)
                || string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            PinnedDirectoryCreation.PinnedDirectoryAnchor parent;
            try
            {
                parent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                    parentPath,
                    createMissing: false);
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
                if (entry.MatchesObjectIdentity(tracked.Value))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
