namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private static async Task RetireOwnershipMigrationTargetsAsync(
        IReadOnlyList<OwnershipMigrationPlan> plans,
        string targetBoundary,
        int? targetIdentityVersion,
        string? targetIdentityValue,
        string? targetIdentityUnavailableReason,
        CancellationToken cancellationToken)
    {
        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetParentPath = Path.GetDirectoryName(plan.Target.CanonicalPath)
                ?? throw new InvalidOperationException(
                    "The ownership migration target has no parent directory.");
            using var targetParent = await OpenVerifiedMarkerParentWithinBoundaryAsync(
                targetBoundary,
                targetParentPath,
                plan.Target.GetIdentity().Semantics,
                targetIdentityVersion,
                targetIdentityValue,
                targetIdentityUnavailableReason,
                cancellationToken);
            using var targetDirectory = targetParent.OpenExistingChild(
                Path.GetFileName(plan.Target.CanonicalPath));
            if (!ManagedDirectoryIdentity.Matches(
                    plan.Target.DirectoryObjectIdentityVersion,
                    plan.Target.DirectoryObjectIdentity,
                    plan.Target.OwnershipToken,
                    targetDirectory.GetDirectoryObjectIdentity())
                || !targetDirectory.VisiblePathMatches()
                || !targetParent.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The ownership migration target changed before temporary artifact cleanup.");
            }

            if (!LibraryDirectoryOwnershipMarker.TryRetireMigrationArtifacts(
                    plan.Source,
                    plan.Target,
                    targetDirectory,
                    targetParent,
                    out var reason))
            {
                throw new InvalidOperationException(
                    $"Temporary ownership migration artifacts could not be retired safely: {reason}");
            }
        }
    }
}
