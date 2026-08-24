namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private enum MarkerlessSourceCleanupDisposition
    {
        NotStarted,
        Delete,
        Retain
    }

    private static MarkerlessSourceCleanupDisposition
        ResolveMarkerlessSourceCleanupDisposition(
            MoveJobEntryCleanupState sourceDirectoryState,
            IReadOnlyCollection<MoveJobEntry> physicalEntries)
    {
        var physicalFiles = physicalEntries
            .Where(entry => entry.EntryType == MoveJobEntryType.File)
            .ToList();
        var retainedFiles = physicalFiles.Any(entry =>
            entry.CleanupState == MoveJobEntryCleanupState.Retained);
        var destructiveFiles = physicalFiles.Any(entry =>
            IsDestructiveCleanupState(entry.CleanupState));
        if (retainedFiles && destructiveFiles)
        {
            throw new MoveNeedsAttentionException(
                "The persisted source-file cleanup evidence mixes retained and destructive dispositions.");
        }

        if (retainedFiles)
        {
            if (IsDestructiveCleanupState(sourceDirectoryState))
            {
                throw new MoveNeedsAttentionException(
                    "Retained source files cannot exist beneath a destructively retired source root.");
            }
            return MarkerlessSourceCleanupDisposition.Retain;
        }

        if (destructiveFiles)
        {
            return MarkerlessSourceCleanupDisposition.Delete;
        }

        var retainedStructure = sourceDirectoryState
                == MoveJobEntryCleanupState.Retained
            || physicalEntries.Any(entry =>
                entry.CleanupState == MoveJobEntryCleanupState.Retained);
        var destructiveStructure = IsDestructiveCleanupState(sourceDirectoryState)
            || physicalEntries.Any(entry =>
                IsDestructiveCleanupState(entry.CleanupState));
        if (retainedStructure && destructiveStructure)
        {
            throw new MoveNeedsAttentionException(
                "The persisted source cleanup evidence has no authoritative file disposition.");
        }

        if (retainedStructure)
        {
            return MarkerlessSourceCleanupDisposition.Retain;
        }

        if (destructiveStructure)
        {
            return MarkerlessSourceCleanupDisposition.Delete;
        }

        return MarkerlessSourceCleanupDisposition.NotStarted;
    }

    private static bool ResolveCompletedMarkerlessSourceRetention(
        MoveJobEntryCleanupState sourceDirectoryState,
        IReadOnlyCollection<MoveJobEntry> physicalEntries)
    {
        var physicalFiles = physicalEntries
            .Where(entry => entry.EntryType == MoveJobEntryType.File)
            .ToList();
        var retainedFiles = physicalFiles.Any(entry =>
            entry.CleanupState == MoveJobEntryCleanupState.Retained);
        var deletedFiles = physicalFiles.Any(entry =>
            entry.CleanupState == MoveJobEntryCleanupState.Deleted);
        if (retainedFiles && deletedFiles)
        {
            throw new MoveNeedsAttentionException(
                "The persisted source-file cleanup evidence mixes retained and deleted dispositions.");
        }

        if (sourceDirectoryState == MoveJobEntryCleanupState.Deleted
            && physicalEntries.Any(entry =>
                entry.CleanupState == MoveJobEntryCleanupState.Retained))
        {
            throw new MoveNeedsAttentionException(
                "Retained source content cannot exist beneath a deleted source root.");
        }

        if (retainedFiles
            && sourceDirectoryState != MoveJobEntryCleanupState.Retained)
        {
            throw new MoveNeedsAttentionException(
                "Retained source files require a retained source root.");
        }

        return retainedFiles;
    }

    private static bool IsDestructiveCleanupState(
        MoveJobEntryCleanupState cleanupState) =>
        cleanupState is MoveJobEntryCleanupState.DeleteAuthorized
            or MoveJobEntryCleanupState.Deleted;
}
