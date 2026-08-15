using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private static void RejectDuplicateRelocationTargets(
        IReadOnlyCollection<RelocationMovePlan> plans,
        FileSystemPathSemantics targetSemantics)
    {
        var targets = new List<RelocationTargetEntry>();
        foreach (var plan in plans)
        {
            foreach (var entry in plan.Manifest.Entries)
            {
                if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                        plan.RequestedPath,
                        entry.RelativePath,
                        targetSemantics,
                        out var targetPath))
                {
                    throw new InvalidOperationException(
                        "A tracked audiobook manifest entry is invalid for the relocation target.");
                }

                targets.Add(new RelocationTargetEntry(targetPath, entry.EntryType));
            }
        }

        targets.Sort((left, right) => targetSemantics.Comparer.Compare(left.Path, right.Path));
        for (var index = 1; index < targets.Count; index++)
        {
            var previous = targets[index - 1];
            var current = targets[index];
            if (FileSystemPathIdentity.AreEquivalent(
                    previous.Path,
                    current.Path,
                    targetSemantics))
            {
                if (previous.EntryType == MoveJobEntryType.Directory
                    && current.EntryType == MoveJobEntryType.Directory)
                {
                    continue;
                }

                throw new InvalidOperationException(
                    "The requested root relocation would map multiple tracked entries to the same target path.");
            }

            if (previous.EntryType == MoveJobEntryType.File
                && FileSystemPathIdentity.IsSameOrInside(
                    current.Path,
                    previous.Path,
                    targetSemantics))
            {
                throw new InvalidOperationException(
                    "The requested root relocation would place tracked content below a target file path.");
            }
        }
    }

    private sealed record RelocationTargetEntry(
        string Path,
        MoveJobEntryType EntryType);

    private static void RejectDuplicateAudiobookFileOwnership(
        ListenArrDbContext db) =>
        AudiobookFileOwnershipValidator.RejectDuplicateValidOwnership(
            db.ChangeTracker.Entries<AudiobookFile>().Select(entry => entry.Entity),
            "The requested root relocation would assign the same filesystem identity to multiple audiobook files.");
}
