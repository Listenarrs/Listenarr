using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    internal static void ValidateTargetManifest(
        string target,
        IReadOnlyCollection<MoveJobEntry> manifest,
        FileSystemPathSemantics targetSemantics)
    {
        var identities = new Dictionary<string, MoveJobEntry>(StringComparer.Ordinal);
        foreach (var entry in manifest)
        {
            if (IsRootManifestEntry(entry))
            {
                var rootKey = FileSystemPathIdentity.CreateKey(
                    "move-target",
                    target,
                    targetSemantics);
                if (identities.ContainsKey(rootKey))
                {
                    throw new MoveNeedsAttentionException(
                        "The manifest contains duplicate destination-root proof entries.");
                }

                identities.Add(rootKey, entry);
                continue;
            }

            if (Path.IsPathRooted(entry.RelativePath))
            {
                throw new MoveNeedsAttentionException(
                    "A manifest entry must be relative to the destination root.");
            }

            if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                target,
                entry.RelativePath,
                targetSemantics,
                out var destinationPath))
            {
                throw new MoveNeedsAttentionException(
                    "A manifest entry escaped the destination root.");
            }

            var key = FileSystemPathIdentity.CreateKey(
                "move-target",
                destinationPath,
                targetSemantics);
            if (identities.TryGetValue(key, out var existing))
            {
                throw new MoveNeedsAttentionException(
                    $"Target filesystem cannot represent both '{existing.RelativePath}' and '{entry.RelativePath}'.");
            }

            identities.Add(key, entry);
        }
    }

    internal static async Task VerifyPublishedManifestAsync(
        string destinationRoot,
        IReadOnlyCollection<MoveJobEntry> manifest,
        FileSystemPathSemantics semantics,
        string boundaryPath,
        string boundaryObjectIdentity,
        CancellationToken cancellationToken)
    {
        foreach (var entry in manifest)
        {
            if (IsRootManifestEntry(entry))
            {
                using var root = OpenPinnedPublishedManifestDirectory(
                    boundaryPath,
                    destinationRoot,
                    semantics,
                    boundaryObjectIdentity);
                ValidatePublishedManifestDirectory(root, entry);
                continue;
            }

            if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                destinationRoot,
                entry.RelativePath,
                semantics,
                out var destinationPath))
            {
                throw new MoveNeedsAttentionException(
                    "A manifest entry escaped the destination root.");
            }

            if (entry.EntryType == MoveJobEntryType.Directory)
            {
                using var directory = OpenPinnedPublishedManifestDirectory(
                    boundaryPath,
                    destinationPath,
                    semantics,
                    boundaryObjectIdentity);
                ValidatePublishedManifestDirectory(directory, entry);
                continue;
            }

            var parentPath = Path.GetDirectoryName(destinationPath)
                ?? throw new MoveNeedsAttentionException(
                    "A published manifest file has no parent directory.");
            using var parent = OpenPinnedPublishedManifestDirectory(
                boundaryPath,
                parentPath,
                semantics,
                boundaryObjectIdentity);
            using var file = parent.OpenExistingFile(
                Path.GetFileName(destinationPath),
                requireDeleteAccess: false);
            if (!PinnedFileVisibleOrThrowUnavailable(
                    file,
                    $"Published file is temporarily unavailable: {entry.RelativePath}")
                || (!string.IsNullOrWhiteSpace(entry.TargetPhysicalObjectIdentity)
                    && !file.MatchesObjectIdentity(
                        entry.TargetPhysicalObjectIdentity)))
            {
                throw new MoveNeedsAttentionException(
                    $"Published file generation changed: {entry.RelativePath}");
            }

            var verified = !string.IsNullOrWhiteSpace(entry.Sha256)
                ? await PinnedFileMatchesManifestAsync(
                    file,
                    entry,
                    cancellationToken)
                : IsVerifiedMarkerlessNativeRenameEntry(entry)
                    && file.MatchesMetadata(
                        entry.Length,
                        entry.LastWriteTimeUtc);
            if (!verified)
            {
                throw new MoveNeedsAttentionException(
                    $"Published file verification failed: {entry.RelativePath}");
            }
        }
    }

    private static PinnedDirectoryCreation.PinnedDirectoryAnchor
        OpenPinnedPublishedManifestDirectory(
            string boundaryPath,
            string directoryPath,
            FileSystemPathSemantics semantics,
            string boundaryObjectIdentity)
    {
        if (!FileSystemPathIdentity.TryGetRelativePathWithinBase(
                boundaryPath,
                directoryPath,
                semantics,
                out var relativePath))
        {
            throw new MoveNeedsAttentionException(
                "A published manifest directory escaped its authorized scan boundary.");
        }

        var current = PinnedDirectoryCreation.OpenPinnedBoundary(boundaryPath);
        try
        {
            if (!current.MatchesDirectoryObjectIdentity(boundaryObjectIdentity)
                || !PinnedDirectoryVisibleOrThrowUnavailable(
                    current,
                    "The published manifest scan boundary is temporarily unavailable."))
            {
                throw new MoveNeedsAttentionException(
                    "The published manifest scan boundary changed physical generation.");
            }

            foreach (var segment in SplitMovePathSegments(relativePath, semantics))
            {
                var next = current.OpenExistingChild(segment);
                current.Dispose();
                current = next;
            }

            if (!PinnedDirectoryVisibleOrThrowUnavailable(
                    current,
                    "The published manifest directory is temporarily unavailable while being pinned."))
            {
                throw new MoveNeedsAttentionException(
                    "A published manifest directory changed while it was being pinned.");
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static void ValidatePublishedManifestDirectory(
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory,
        MoveJobEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.TargetPhysicalObjectIdentity)
            && !directory.MatchesDirectoryObjectIdentity(
                entry.TargetPhysicalObjectIdentity))
        {
            throw new MoveNeedsAttentionException(
                $"Published directory generation changed: {entry.RelativePath}");
        }
    }
}
