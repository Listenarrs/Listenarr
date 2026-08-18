namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private void ValidateUnixMarkerlessMoveVolumes(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        IReadOnlyCollection<MoveJobEntry> files)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var entry in files)
        {
            var sourcePath = ResolveManifestPath(
                source,
                entry,
                request.SourceSemantics,
                "source");
            var targetPath = ResolveManifestPath(
                target,
                entry,
                request.TargetSemantics,
                "target");
            var sourceParentPath = Path.GetDirectoryName(sourcePath)
                ?? throw new MoveNeedsAttentionException(
                    "A markerless source file has no parent.");
            var targetParentPath = Path.GetDirectoryName(targetPath)
                ?? throw new MoveNeedsAttentionException(
                    "A markerless target file has no parent.");
            var targetVolumeAnchor = FindNearestExistingTargetAncestor(
                targetParentPath);

            using var sourceParent = OpenPinnedMoveBoundaryDescendant(
                request,
                sourceParentPath,
                request.SourceSemantics,
                sourceBoundary: true);
            using var targetParent = OpenPinnedMoveBoundaryDescendant(
                request,
                targetVolumeAnchor,
                request.TargetSemantics,
                sourceBoundary: false);
            using var sourceEntry = sourceParent.TryOpenExistingFile(
                Path.GetFileName(sourcePath),
                requireDeleteAccess: false);
            if (sourceEntry != null
                && (faultInjector?.ForceCrossVolumeForTest == true
                    || !sourceEntry.IsOnSameVolume(targetParent)))
            {
                throw new MoveNeedsAttentionException(
                    "Unix cross-volume library moves are blocked because exact source-generation retirement would require a library-side namespace claim.");
            }
        }
    }

    private static string FindNearestExistingTargetAncestor(string targetParentPath)
    {
        var current = Path.GetFullPath(targetParentPath);
        while (true)
        {
            if (TryGetMarkerlessPathAttributes(current, out var attributes))
            {
                if ((attributes & FileAttributes.Directory) == 0)
                {
                    throw new MoveNeedsAttentionException(
                        "A markerless target ancestor is occupied by a file.");
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new MoveNeedsAttentionException(
                        "A markerless target ancestor became a link.");
                }
                break;
            }

            current = Path.GetDirectoryName(current)
                ?? throw new MoveNeedsAttentionException(
                    "No existing ancestor could be found for the markerless move target.");
        }

        ValidateExistingMoveDirectory(
            current,
            "nearest existing markerless target volume ancestor");
        return current;
    }
}
