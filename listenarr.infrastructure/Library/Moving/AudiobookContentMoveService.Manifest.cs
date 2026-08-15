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
        CancellationToken cancellationToken)
    {
        foreach (var entry in manifest)
        {
            if (IsRootManifestEntry(entry))
            {
                if (!Directory.Exists(destinationRoot))
                {
                    throw new MoveNeedsAttentionException(
                        "Published destination root is missing.");
                }

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
                if (!Directory.Exists(destinationPath))
                {
                    throw new MoveNeedsAttentionException(
                        $"Published directory is missing: {entry.RelativePath}");
                }

                continue;
            }

            if (!File.Exists(destinationPath))
            {
                throw new MoveNeedsAttentionException(
                    $"Published file verification failed: {entry.RelativePath}");
            }

            var parentPath = Path.GetDirectoryName(destinationPath)
                ?? throw new MoveNeedsAttentionException(
                    "A published manifest file has no parent directory.");
            using var parent = PinnedDirectoryCreation.OpenPinnedBoundary(parentPath);
            using var file = parent.OpenExistingFile(
                Path.GetFileName(destinationPath),
                requireDeleteAccess: false);
            if (!file.VisiblePathMatches()
                || (!string.IsNullOrWhiteSpace(entry.TargetPhysicalObjectIdentity)
                    && !string.Equals(
                        entry.TargetPhysicalObjectIdentity,
                        file.GetObjectIdentity(),
                        StringComparison.Ordinal)))
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
}
