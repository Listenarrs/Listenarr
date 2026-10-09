using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private sealed record OwnershipMigrationPreparation(
        IReadOnlyList<OwnershipMigrationPlan> Transfers,
        IReadOnlyList<LibraryDirectoryOwnership> Retirements);

    private async Task<OwnershipMigrationPreparation>
        RevalidateRecoveredOwnershipPlansAsync(
            ListenArrDbContext db,
            RootFolder root,
            IReadOnlyList<OwnershipMigrationPlan> plans,
            IReadOnlySet<int> skippedAudiobookIds,
            CancellationToken cancellationToken)
    {
        var retirements = new List<LibraryDirectoryOwnership>(plans.Count);
        foreach (var plan in plans)
        {
            var ownership = plan.Tracked;
            if (ownership.State == LibraryDirectoryOwnershipState.Removing)
            {
                throw new InvalidOperationException(
                    "Directory cleanup began before ownership migration recovery completed.");
            }

            // A recovered path-migration journal is descriptive only. A restart
            // cannot recreate destructive directory ownership at the target.
            retirements.Add(ownership);
        }

        _ = skippedAudiobookIds;
        var journaledOwnershipIds = plans
            .Select(plan => plan.Tracked.Id)
            .ToHashSet();
        var unjournaledOwnerships = await db.LibraryDirectoryOwnerships
            .Where(ownership =>
                ownership.ManagedRootFolderId == root.Id
                && ownership.State != LibraryDirectoryOwnershipState.Removed
                && !journaledOwnershipIds.Contains(ownership.Id))
            .ToListAsync(cancellationToken);
        if (unjournaledOwnerships.Any(ownership =>
            ownership.State == LibraryDirectoryOwnershipState.Removing))
        {
            throw new InvalidOperationException(
                "Directory cleanup began before metadata-only recovery completed.");
        }

        // No metadata-only restart path transfers cleanup authority. Journaled
        // and unjournaled claims are both retired conservatively.
        retirements.AddRange(unjournaledOwnerships);
        return new OwnershipMigrationPreparation(
            [],
            retirements.DistinctBy(ownership => ownership.Id).ToArray());
    }

    private static void RetireUntransferredOwnerships(
        IReadOnlyList<LibraryDirectoryOwnership> ownerships,
        DateTime now)
    {
        foreach (var ownership in ownerships)
        {
            ownership.State = LibraryDirectoryOwnershipState.Removed;
            ownership.PathOwnershipKey = null;
            ownership.ManagedRootFolderId = null;
            ownership.StateReason = null;
            ownership.UpdatedAt = now;
        }
    }
}
