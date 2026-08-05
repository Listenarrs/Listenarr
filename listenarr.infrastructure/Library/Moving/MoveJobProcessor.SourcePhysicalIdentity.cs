using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal partial class MoveJobProcessor
{
    private static IReadOnlyDictionary<string, string>
        BuildSourcePhysicalObjectIdentities(
            Audiobook audiobook,
            MoveJob job,
            string source,
            FileSystemPathSemantics sourceSemantics)
    {
        var trackedRelativePaths = new HashSet<string>(sourceSemantics.Comparer);
        var identities = new Dictionary<string, string>(sourceSemantics.Comparer);
        foreach (var file in audiobook.Files ?? [])
        {
            if (file.PathIdentityState != PathIdentityState.Valid
                || string.IsNullOrWhiteSpace(file.CanonicalPath)
                || !FileSystemPathIdentity.TryGetRelativePathWithinBase(
                    source,
                    file.CanonicalPath,
                    sourceSemantics,
                    out var relativePath)
                || string.IsNullOrWhiteSpace(relativePath)
                || string.Equals(relativePath, ".", StringComparison.Ordinal))
            {
                continue;
            }

            trackedRelativePaths.Add(relativePath);
            if (!string.IsNullOrWhiteSpace(file.PhysicalObjectIdentity))
            {
                identities[relativePath] = file.PhysicalObjectIdentity;
            }
        }

        foreach (var entry in job.Entries.Where(candidate =>
            candidate.EntryType == MoveJobEntryType.File
            && candidate.CleanupState != MoveJobEntryCleanupState.Deleted
            && trackedRelativePaths.Contains(candidate.RelativePath)))
        {
            if (!identities.ContainsKey(entry.RelativePath))
            {
                throw new MoveNeedsAttentionException(
                    $"Tracked source physical identity is unavailable for '{entry.RelativePath}'. Rescan the audiobook before retrying the move.");
            }
        }

        return identities;
    }
}
