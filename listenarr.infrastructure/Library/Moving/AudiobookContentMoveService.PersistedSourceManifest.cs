using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private static async Task ValidatePersistedSourceManifestAsync(
        string source,
        string target,
        bool targetInsideSource,
        IReadOnlyCollection<MoveJobEntry> manifest,
        FileSystemPathSemantics sourceSemantics,
        CancellationToken cancellationToken,
        bool requireTrackedFile = true,
        bool verifyFileContents = true,
        bool allowVerifiedNativeRenameMissingSources = false)
    {
        ValidateMoveSourceRoot(source);
        if (manifest.Count == 0)
        {
            if (requireTrackedFile)
            {
                throw new MoveNeedsAttentionException(
                    "The move job has no persisted tracked-file source manifest.");
            }

            return;
        }

        if (requireTrackedFile
            && manifest.All(entry => entry.EntryType != MoveJobEntryType.File))
        {
            throw new MoveNeedsAttentionException(
                "The move job has no persisted tracked-file source manifest.");
        }

        var expectedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MoveManifestIdentity.IsBoundaryAuthorization(entry))
            {
                continue;
            }
            if (IsRootManifestEntry(entry)
                || string.IsNullOrWhiteSpace(entry.RelativePath)
                || string.Equals(entry.RelativePath, ".", StringComparison.Ordinal)
                || !FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                    source,
                    entry.RelativePath,
                    sourceSemantics,
                    out var fullPath))
            {
                throw new MoveNeedsAttentionException(
                    "A persisted source manifest entry escaped the authorized source root.");
            }

            var identityKey = FileSystemPathIdentity.CreateKey(
                "move-source-entry",
                fullPath,
                sourceSemantics,
                MoveManifestIdentity.Version);
            if (!expectedPaths.Add(identityKey))
            {
                throw new MoveNeedsAttentionException(
                    "The persisted source manifest contains duplicate paths.");
            }

            if (targetInsideSource
                && IsSameOrInside(fullPath, target, sourceSemantics))
            {
                throw new MoveNeedsAttentionException(
                    "A persisted source manifest entry overlaps the move target subtree.");
            }

            ValidateManifestAncestorChain(
                source,
                fullPath,
                entry.EntryType,
                sourceSemantics);
            var entryExists = TryGetMarkerlessPathAttributes(
                fullPath,
                out var entryAttributes);
            if (entry.EntryType == MoveJobEntryType.Directory)
            {
                if (!entryExists
                    || (entryAttributes & FileAttributes.Directory) == 0
                    || (entryAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new MoveNeedsAttentionException(
                        $"Manifest directory changed type, disappeared, or became linked: {entry.RelativePath}");
                }

                continue;
            }

            if (!entryExists
                && allowVerifiedNativeRenameMissingSources
                && IsVerifiedMarkerlessNativeRenameEntry(entry))
            {
                continue;
            }

            if (entry.EntryType != MoveJobEntryType.File
                || !entryExists
                || (entryAttributes & FileAttributes.Directory) != 0
                || (entryAttributes & FileAttributes.ReparsePoint) != 0
                || !FileMetadataMatchesManifest(fullPath, entry)
                || (verifyFileContents
                    && !await FileMatchesManifestAsync(
                        fullPath,
                        entry,
                        cancellationToken)))
            {
                throw new MoveNeedsAttentionException(
                    $"Manifest file changed, disappeared, or became linked: {entry.RelativePath}");
            }
        }

        ValidateMoveSourceRoot(source);
    }

    private static bool IsVerifiedMarkerlessNativeRenameEntry(
        MoveJobEntry entry) =>
        entry.EntryType == MoveJobEntryType.File
        && entry.CopyState == MoveJobEntryCopyState.Verified
        && !string.IsNullOrWhiteSpace(entry.SourcePhysicalObjectIdentity)
        && !string.IsNullOrWhiteSpace(entry.TargetPhysicalObjectIdentity)
        && PinnedDirectoryCreation.ArePersistedObjectIdentitiesDurablyEquivalent(
            entry.SourcePhysicalObjectIdentity,
            entry.TargetPhysicalObjectIdentity);

    private static bool FileMetadataMatchesManifest(
        string path,
        MoveJobEntry manifestEntry)
    {
        if (manifestEntry.EntryType != MoveJobEntryType.File)
        {
            return false;
        }

        var fileInfo = new FileInfo(path);
        return fileInfo.Length == manifestEntry.Length
            && fileInfo.LastWriteTimeUtc == manifestEntry.LastWriteTimeUtc;
    }

    private static void ValidateManifestAncestorChain(
        string source,
        string fullPath,
        MoveJobEntryType entryType,
        FileSystemPathSemantics sourceSemantics)
    {
        var current = entryType == MoveJobEntryType.Directory
            ? fullPath
            : Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (!FileSystemPathIdentity.IsSameOrInside(
                    current,
                    source,
                    sourceSemantics))
            {
                throw new MoveNeedsAttentionException(
                    "A manifest ancestor escaped the authorized source root.");
            }

            if (!TryGetMarkerlessPathAttributes(current, out var attributes))
            {
                throw new MoveNeedsAttentionException(
                    "A manifest ancestor directory disappeared.");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0
                || (attributes & FileAttributes.Directory) == 0)
            {
                throw new MoveNeedsAttentionException(
                    "A manifest ancestor changed type or became linked.");
            }

            if (FileSystemPathIdentity.AreEquivalent(
                    current,
                    source,
                    sourceSemantics))
            {
                return;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent)
                || FileSystemPathIdentity.AreEquivalent(
                    parent,
                    current,
                    sourceSemantics))
            {
                break;
            }

            current = parent;
        }

        throw new MoveNeedsAttentionException(
            "A manifest ancestor chain did not terminate at the authorized source root.");
    }

}
