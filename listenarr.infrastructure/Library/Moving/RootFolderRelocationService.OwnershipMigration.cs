using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private sealed record OwnershipMigrationPlan(
        LibraryDirectoryOwnership Tracked,
        LibraryDirectoryOwnership Source,
        LibraryDirectoryOwnership Target,
        LibraryDirectoryOwnershipPathMigration Journal);

    private sealed class OwnershipMigrationTargetLease : IDisposable
    {
        private readonly OwnershipMigrationPlan _plan;
        private readonly PinnedDirectoryCreation.PinnedDirectoryAnchor _parent;
        private readonly PinnedDirectoryCreation.PinnedDirectoryAnchor _directory;

        public OwnershipMigrationTargetLease(
            OwnershipMigrationPlan plan,
            string targetBoundary)
        {
            _plan = plan;
            var targetParentPath = Path.GetDirectoryName(
                plan.Target.CanonicalPath)
                ?? throw new InvalidOperationException(
                    "The migrated ownership target has no parent directory.");
            _parent = OpenDirectoryParentWithinBoundary(
                targetBoundary,
                targetParentPath,
                plan.Target.GetIdentity().Semantics);
            try
            {
                _directory = _parent.OpenExistingChild(
                    Path.GetFileName(plan.Target.CanonicalPath));
                ValidateAndCapture();
            }
            catch
            {
                _parent.Dispose();
                throw;
            }
        }

        public void ValidateAndCapture()
        {
            var nativeIdentity = _directory.GetDirectoryObjectIdentity();
            if (!_directory.MatchesManagedDirectoryOwnershipIdentity(
                    _plan.Source.DirectoryObjectIdentityVersion,
                    _plan.Source.DirectoryObjectIdentity,
                    _plan.Source.OwnershipToken)
                || !ReservationPathMatchesOrThrowUnavailable(
                    _directory,
                    "The metadata-only ownership target is temporarily unavailable.")
                || !ReservationPathMatchesOrThrowUnavailable(
                    _parent,
                    "The metadata-only ownership target parent is temporarily unavailable."))
            {
                throw new InvalidOperationException(
                    "Metadata-only relocation cannot transfer directory ownership to a different physical generation.");
            }

            _plan.Target.DirectoryObjectIdentityVersion =
                ManagedDirectoryIdentity.CurrentVersion;
            _plan.Target.DirectoryObjectIdentity = ManagedDirectoryIdentity.Create(
                _plan.Target.OwnershipToken,
                nativeIdentity);
            _plan.Target.DirectoryObjectIdentityUnavailableReason = null;
        }

        public void Dispose()
        {
            _directory.Dispose();
            _parent.Dispose();
        }
    }

    private async Task<OwnershipMigrationPreparation>
        PrepareOwnershipMigrationsAsync(
            ListenArrDbContext db,
            RootFolderRelocation relocation,
            RootFolder root,
            FileSystemPathSemantics? sourceSemantics,
            FileSystemPathSemantics targetSemantics,
            IReadOnlySet<int> skippedAudiobookIds,
            CancellationToken cancellationToken)
    {
        var ownerships = await db.LibraryDirectoryOwnerships
            .Where(ownership =>
                ownership.ManagedRootFolderId == root.Id
                && ownership.State != LibraryDirectoryOwnershipState.Removed)
            .ToListAsync(cancellationToken);
        if (ownerships.Count == 0)
        {
            return new OwnershipMigrationPreparation([], []);
        }
        if (ownerships.Any(ownership =>
            ownership.State == LibraryDirectoryOwnershipState.Removing))
        {
            throw new RootFolderPathChangeRejectedException(
                "root_folder_ownership_recovery_blocked",
                "This root folder has unfinished directory cleanup. Let Listenarr finish or recover that cleanup before changing the root folder path.",
                "Metadata-only relocation is blocked while directory ownership cleanup is removing a directory.");
        }

        // Metadata-only relocation crosses a durable authority boundary.
        // Persisted directory identities are diagnostics and cannot prove that
        // a directory at the new path remains safe for future deletion.
        // Retire every old cleanup claim; do not transfer destructive ownership.
        _ = relocation;
        _ = sourceSemantics;
        _ = targetSemantics;
        _ = skippedAudiobookIds;
        return new OwnershipMigrationPreparation([], ownerships);
    }

    private static IReadOnlyList<OwnershipMigrationTargetLease>
        PinOwnershipMigrationTargets(
            IReadOnlyList<OwnershipMigrationPlan> plans,
            string targetBoundary,
            CancellationToken cancellationToken)
    {
        var leases = new List<OwnershipMigrationTargetLease>(plans.Count);
        try
        {
            foreach (var plan in plans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                leases.Add(new OwnershipMigrationTargetLease(
                    plan,
                    targetBoundary));
            }
            return leases;
        }
        catch
        {
            DisposeOwnershipMigrationTargetLeases(leases);
            throw;
        }
    }

    private static void RevalidateOwnershipMigrationTargetLeases(
        IReadOnlyList<OwnershipMigrationTargetLease> leases,
        CancellationToken cancellationToken)
    {
        foreach (var lease in leases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lease.ValidateAndCapture();
        }
    }

    private static void DisposeOwnershipMigrationTargetLeases(
        IEnumerable<OwnershipMigrationTargetLease> leases)
    {
        foreach (var lease in leases.Reverse())
        {
            lease.Dispose();
        }
    }

    private static void ApplyOwnershipMigrationMetadata(
        IReadOnlyList<OwnershipMigrationPlan> plans,
        DateTime now)
    {
        foreach (var plan in plans)
        {
            plan.Tracked.PathOwnershipKey = null;
        }

        foreach (var plan in plans)
        {
            var ownership = plan.Tracked;
            var target = plan.Target;
            ownership.Path = target.Path;
            ownership.CanonicalPath = target.CanonicalPath;
            ownership.PathSyntax = target.PathSyntax;
            ownership.PathCaseSensitivity =
                target.PathCaseSensitivity;
            ownership.PathCaseSensitivityMode =
                target.PathCaseSensitivityMode;
            ownership.PathIdentityBoundary =
                target.PathIdentityBoundary;
            ownership.PathIdentityLookupKey =
                target.PathIdentityLookupKey;
            ownership.ManagedRootFolderId =
                target.ManagedRootFolderId;
            ownership.DirectoryObjectIdentityVersion =
                target.DirectoryObjectIdentityVersion;
            ownership.DirectoryObjectIdentity =
                target.DirectoryObjectIdentity;
            ownership.DirectoryObjectIdentityUnavailableReason =
                target.DirectoryObjectIdentityUnavailableReason;
            ownership.UpdatedAt = now;
        }
    }

    private static void AssignOwnershipMigrationKeys(
        IReadOnlyList<OwnershipMigrationPlan> plans,
        DateTime now)
    {
        foreach (var plan in plans)
        {
            plan.Tracked.PathOwnershipKey =
                plan.Target.PathOwnershipKey;
            plan.Journal.UpdatedAt = now;
        }
    }

    private static LibraryDirectoryOwnership SnapshotOwnership(
        LibraryDirectoryOwnership source) => new()
        {
            Id = source.Id,
            Path = source.Path,
            CanonicalPath = source.CanonicalPath,
            PathSyntax = source.PathSyntax,
            PathCaseSensitivity = source.PathCaseSensitivity,
            PathCaseSensitivityMode =
                source.PathCaseSensitivityMode,
            PathIdentityBoundary =
                source.PathIdentityBoundary,
            PathIdentityLookupKey =
                source.PathIdentityLookupKey,
            PathOwnershipKey = source.PathOwnershipKey,
            OwnershipToken = source.OwnershipToken,
            State = source.State,
            CreationWorkflow = source.CreationWorkflow,
            CreationOperationId = source.CreationOperationId,
            AudiobookId = source.AudiobookId,
            ManagedRootFolderId = source.ManagedRootFolderId,
            DirectoryObjectIdentityVersion =
                source.DirectoryObjectIdentityVersion,
            DirectoryObjectIdentity =
                source.DirectoryObjectIdentity,
            DirectoryObjectIdentityUnavailableReason =
                source.DirectoryObjectIdentityUnavailableReason,
            StateReason = source.StateReason,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt
        };

    private static PinnedDirectoryCreation.PinnedDirectoryAnchor
        OpenDirectoryParentWithinBoundary(
            string boundaryPath,
            string parentPath,
            FileSystemPathSemantics semantics)
    {
        var canonicalBoundary = FileSystemPathIdentity.Canonicalize(
            boundaryPath,
            semantics.Syntax);
        var canonicalParent = FileSystemPathIdentity.Canonicalize(
            parentPath,
            semantics.Syntax);
        if (!FileSystemPathIdentity.IsSameOrInside(
                canonicalParent,
                canonicalBoundary,
                semantics))
        {
            throw new InvalidOperationException(
                "An ownership migration directory escaped its authorized root boundary.");
        }

        var current = PinnedDirectoryCreation.OpenPinnedBoundary(
            canonicalBoundary);
        try
        {
            if (FileSystemPathIdentity.AreEquivalent(
                    canonicalParent,
                    canonicalBoundary,
                    semantics))
            {
                return current;
            }

            var relative = Path.GetRelativePath(
                canonicalBoundary,
                canonicalParent);
            foreach (var segment in relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment is "." or "..")
                {
                    throw new InvalidOperationException(
                        "An ownership migration directory contains navigation segments.");
                }

                var next = current.OpenExistingChild(segment);
                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }
}
