namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private static void EnsureRelocationTargetMutationCapability(
        RootFolderRelocationMode mode,
        string targetPath)
    {
        if (mode != RootFolderRelocationMode.Relocate
            || FileSystemMutationCapabilityProbe.ProbeReadOnlyNearestDirectory(targetPath) != true)
        {
            return;
        }

        throw new RootFolderPathChangeRejectedException(
            "root_folder_target_filesystem_mutation_unavailable",
            "The destination storage is mounted read-only, so Listenarr cannot relocate library files there.",
            "The target filesystem reports the ST_RDONLY mount flag.");
    }

    private static void EnsureRelocationSourceMutationCapability(
        RootFolderRelocationMode mode,
        RootFolder root)
    {
        if (mode != RootFolderRelocationMode.Relocate
            || FileSystemMutationCapabilityProbe.ProbeReadOnlyNearestDirectory(root.Path) != true)
        {
            return;
        }

        throw new RootFolderPathChangeRejectedException(
            "root_folder_source_filesystem_mutation_unavailable",
            "The current root storage is mounted read-only, so Listenarr cannot relocate its files. Use a metadata-only path change if you only need to repair the stored location.",
            "The source filesystem reports the ST_RDONLY mount flag.");
    }
}
