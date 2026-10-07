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
        if (retainedFiles
            || sourceDirectoryState == MoveJobEntryCleanupState.Retained
            || physicalEntries.Any(entry =>
                entry.CleanupState == MoveJobEntryCleanupState.Retained))
        {
            return MarkerlessSourceCleanupDisposition.Retain;
        }

        // DeleteAuthorized and Deleted are persisted observations, not durable
        // permission to issue another delete after a process boundary. Returning
        // Delete here only means cleanup had started; resume will reconcile and
        // retain any surviving source.
        var destructiveStructure = IsDestructiveCleanupState(sourceDirectoryState)
            || physicalEntries.Any(entry =>
                IsDestructiveCleanupState(entry.CleanupState));
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
        return retainedFiles
            || sourceDirectoryState == MoveJobEntryCleanupState.Retained;
    }

    private static bool IsDestructiveCleanupState(
        MoveJobEntryCleanupState cleanupState) =>
        cleanupState is MoveJobEntryCleanupState.DeleteAuthorized
            or MoveJobEntryCleanupState.Deleted;
}
