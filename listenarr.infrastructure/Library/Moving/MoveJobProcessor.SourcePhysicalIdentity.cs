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
        var identities = new Dictionary<string, string>(sourceSemantics.Comparer);
        foreach (var file in audiobook.Files ?? [])
        {
            if (file.PathIdentityState != PathIdentityState.Valid
                || string.IsNullOrWhiteSpace(file.CanonicalPath)
                || string.IsNullOrWhiteSpace(file.PhysicalObjectIdentity)
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

            identities[relativePath] = file.PhysicalObjectIdentity;
        }

        foreach (var entry in job.Entries.Where(candidate =>
            candidate.EntryType == MoveJobEntryType.File
            && candidate.CleanupState != MoveJobEntryCleanupState.Deleted))
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
