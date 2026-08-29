using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private static void EnsureRelocationTargetMutationSemanticsAuthority(
        RootFolderRelocationMode mode,
        FileSystemCaseSensitivityMode requestedMode,
        FileSystemSemanticsResolution resolution)
    {
        if (mode != RootFolderRelocationMode.Relocate
            || requestedMode != FileSystemCaseSensitivityMode.Auto
            || resolution.HasDurableMutationSemanticsAuthority)
        {
            return;
        }

        throw new RootFolderPathChangeRejectedException(
            "root_folder_target_mutation_semantics_unproven",
            "Listenarr can read the new root folder, but automatic case-sensitivity detection is not stable enough to move files there. Select Sensitive or Insensitive explicitly and try again.",
            "The target filesystem semantics were inferred from a behavioral lookup probe rather than an authoritative filesystem capability.");
    }

    private static void EnsureRelocationSourceMutationSemanticsAuthority(
        RootFolderRelocationMode mode,
        FileSystemCaseSensitivityMode requestedMode,
        FileSystemSemanticsResolution? resolution)
    {
        if (mode != RootFolderRelocationMode.Relocate
            || requestedMode != FileSystemCaseSensitivityMode.Auto
            || resolution == null
            || resolution.HasDurableMutationSemanticsAuthority)
        {
            return;
        }

        throw new RootFolderPathChangeRejectedException(
            "root_folder_source_mutation_semantics_unproven",
            "Listenarr can read the current root folder, but automatic case-sensitivity detection is not stable enough to move files from it. Select Sensitive or Insensitive explicitly and try again.",
            "The source filesystem semantics were inferred from a behavioral lookup probe rather than an authoritative filesystem capability.");
    }

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
