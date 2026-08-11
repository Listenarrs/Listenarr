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
        var transfers = new List<OwnershipMigrationPlan>(plans.Count);
        var retirements = new List<LibraryDirectoryOwnership>();
        foreach (var plan in plans)
        {
            var ownership = plan.Tracked;
            if (ownership.State == LibraryDirectoryOwnershipState.Removing)
            {
                throw new InvalidOperationException(
                    "Directory cleanup began before ownership migration recovery completed.");
            }
            if (ownership.AudiobookId is int audiobookId
                && skippedAudiobookIds.Contains(audiobookId))
            {
                retirements.Add(ownership);
                continue;
            }
            if (ownership.State is LibraryDirectoryOwnershipState.Unavailable
                or LibraryDirectoryOwnershipState.Conflict
                or LibraryDirectoryOwnershipState.Removed
                || plan.Source.DirectoryObjectIdentityVersion
                    != ManagedDirectoryIdentity.CurrentVersion
                || string.IsNullOrWhiteSpace(plan.Source.DirectoryObjectIdentity))
            {
                retirements.Add(ownership);
                continue;
            }

            var targetGeneration = await ResolveExistingDirectoryObjectIdentityAsync(
                plan.Target.CanonicalPath,
                plan.Source.DirectoryObjectIdentityVersion!.Value,
                plan.Source.DirectoryObjectIdentity!,
                cancellationToken);
            if (!targetGeneration.IsAvailable)
            {
                retirements.Add(ownership);
                continue;
            }

            transfers.Add(plan);
        }

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

        // A committed metadata-only journal can transfer cleanup authority only
        // for ownerships that have an explicit path-migration journal. Any
        // unjournaled claim is conservatively retired during recovery.
        retirements.AddRange(unjournaledOwnerships);
        return new OwnershipMigrationPreparation(
            transfers,
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
