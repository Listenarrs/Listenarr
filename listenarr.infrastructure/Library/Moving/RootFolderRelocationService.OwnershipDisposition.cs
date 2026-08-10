namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private sealed record OwnershipMigrationPreparation(
        IReadOnlyList<OwnershipMigrationPlan> Transfers,
        IReadOnlyList<LibraryDirectoryOwnership> Retirements);

    private async Task<OwnershipMigrationPreparation>
        RevalidateRecoveredOwnershipPlansAsync(
            IReadOnlyList<OwnershipMigrationPlan> plans,
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

        return new OwnershipMigrationPreparation(transfers, retirements);
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
