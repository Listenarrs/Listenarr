using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private void ValidateExistingDestinationContents(
        string source,
        string destinationRoot,
        IReadOnlyCollection<MoveJobEntry> manifest,
        FileSystemPathSemantics targetSemantics,
        LibraryDirectoryOwnership? targetDirectoryOwnership = null)
    {
        if (!Directory.Exists(destinationRoot))
        {
            return;
        }

        RevalidateTargetDirectoryOwnership(targetDirectoryOwnership);
        if (!FileSystemSafety.TryEnumerateTreeWithoutLinks(
                destinationRoot,
                out var files,
                out var directories,
                out var reason))
        {
            throw new MoveNeedsAttentionException(reason);
        }

        var expectedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest)
        {
            if (IsRootManifestEntry(entry))
            {
                expectedPaths.Add(FileSystemPathIdentity.CreateKey(
                    "move-target",
                    destinationRoot,
                    targetSemantics));
                continue;
            }

            if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                    destinationRoot,
                    entry.RelativePath,
                    targetSemantics,
                    out var expectedPath))
            {
                throw new MoveNeedsAttentionException(
                    "A manifest entry escaped the destination root.");
            }

            expectedPaths.Add(FileSystemPathIdentity.CreateKey(
                "move-target",
                expectedPath,
                targetSemantics));
        }

        var sourceInsideDestination = IsSameOrInside(
            source,
            destinationRoot,
            targetSemantics);

        foreach (var directory in directories)
        {
            if (sourceInsideDestination
                && (IsSameOrInside(directory, source, targetSemantics)
                    || IsSameOrInside(source, directory, targetSemantics)))
            {
                continue;
            }

            var key = FileSystemPathIdentity.CreateKey(
                "move-target",
                directory,
                targetSemantics);
            if (!expectedPaths.Contains(key))
            {
                throw new MoveNeedsAttentionException(
                    $"Destination contains an unowned directory: {Path.GetRelativePath(destinationRoot, directory)}");
            }
        }

        foreach (var file in files)
        {
            if (sourceInsideDestination
                && IsSameOrInside(file, source, targetSemantics))
            {
                continue;
            }

            var key = FileSystemPathIdentity.CreateKey(
                "move-target",
                file,
                targetSemantics);
            if (!expectedPaths.Contains(key))
            {
                throw new MoveNeedsAttentionException(
                    $"Destination contains an unowned file: {Path.GetRelativePath(destinationRoot, file)}");
            }
        }
    }
}
