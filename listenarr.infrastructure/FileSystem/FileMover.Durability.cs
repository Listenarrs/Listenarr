namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private void FlushFileMoveDirectory(
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory,
        string phase)
    {
        BeforeFileMoveDurabilityBarrierForTest?.Invoke(phase);
        directory.FlushDirectoryEntry();
    }
}
