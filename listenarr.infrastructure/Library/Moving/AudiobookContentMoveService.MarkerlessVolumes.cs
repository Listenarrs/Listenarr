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

            using var sourceParent = PinnedDirectoryCreation.OpenPinnedBoundary(
                sourceParentPath);
            using var targetParent = PinnedDirectoryCreation.OpenPinnedBoundary(
                targetVolumeAnchor);
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
        while (!Directory.Exists(current))
        {
            if (File.Exists(current))
            {
                throw new MoveNeedsAttentionException(
                    "A markerless target ancestor is occupied by a file.");
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
