using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Jobs;

public partial class MoveQueueService
{
    private static ValidatedMoveManifest ValidateSourceManifest(
        string source,
        PathIdentitySnapshot sourceIdentity,
        string target,
        PathIdentitySnapshot targetIdentity,
        IReadOnlyCollection<MoveSourceManifestEntry> sourceEntries)
    {
        ArgumentNullException.ThrowIfNull(sourceEntries);
        if (sourceEntries.Count == 0
            || sourceEntries.All(entry => entry.EntryType != MoveJobEntryType.File))
        {
            throw new InvalidOperationException(
                "A physical move requires at least one validated tracked file manifest entry.");
        }

        var sourcePaths = new HashSet<string>(StringComparer.Ordinal);
        var targetPaths = new HashSet<string>(StringComparer.Ordinal);
        var persistedEntries = new List<MoveJobEntry>(sourceEntries.Count);
        foreach (var entry in sourceEntries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (string.IsNullOrWhiteSpace(entry.RelativePath)
                || string.Equals(entry.RelativePath, ".", StringComparison.Ordinal)
                || Path.IsPathRooted(entry.RelativePath)
                || !FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                    source,
                    entry.RelativePath,
                    sourceIdentity.Semantics,
                    out var sourcePath)
                || !FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                    target,
                    entry.RelativePath,
                    targetIdentity.Semantics,
                    out var targetPath))
            {
                throw new InvalidOperationException(
                    "A move source manifest entry escaped its source or target coordinate root.");
            }

            var sourceKey = FileSystemPathIdentity.CreateKey(
                "move-source-entry",
                sourcePath,
                sourceIdentity.Semantics);
            var targetKey = FileSystemPathIdentity.CreateKey(
                "move-target-entry",
                targetPath,
                targetIdentity.Semantics);
            if (!sourcePaths.Add(sourceKey)
                || !targetPaths.Add(targetKey))
            {
                throw new InvalidOperationException(
                    "The move source manifest contains duplicate or target-colliding paths.");
            }

            if (entry.EntryType == MoveJobEntryType.File)
            {
                if (entry.Length < 0
                    || (entry.Sha256 != null
                        && (entry.Sha256.Length != 64
                            || !entry.Sha256.All(Uri.IsHexDigit))))
                {
                    throw new InvalidOperationException(
                        "A move source file manifest entry has invalid length or hash evidence.");
                }
            }
            else if (entry.EntryType == MoveJobEntryType.Directory)
            {
                if (entry.Sha256 != null || entry.Length != 0)
                {
                    throw new InvalidOperationException(
                        "A move source directory manifest entry contains file-only evidence.");
                }
            }
            else
            {
                throw new InvalidOperationException(
                    "The move source manifest contains an unsupported entry type.");
            }

            persistedEntries.Add(new MoveJobEntry
            {
                RelativePath = entry.RelativePath,
                EntryType = entry.EntryType,
                Length = entry.Length,
                LastWriteTimeUtc = entry.LastWriteTimeUtc,
                Sha256 = entry.Sha256,
                CopyState = MoveJobEntryCopyState.Pending,
                CleanupState = MoveJobEntryCleanupState.Pending
            });
        }

        return new ValidatedMoveManifest(persistedEntries);
    }

    private sealed record ValidatedMoveManifest(
        IReadOnlyList<MoveJobEntry> Entries);
}
