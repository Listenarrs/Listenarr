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

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var plans = new List<OwnershipMigrationPlan>(ownerships.Count);
        var retirements = new List<LibraryDirectoryOwnership>();
        foreach (var ownership in ownerships)
        {
            if (ownership.AudiobookId is int audiobookId
                && skippedAudiobookIds.Contains(audiobookId))
            {
                retirements.Add(ownership);
                continue;
            }

            if (ownership.State is LibraryDirectoryOwnershipState.Unavailable
                or LibraryDirectoryOwnershipState.Conflict)
            {
                retirements.Add(ownership);
                continue;
            }

            if (!sourceSemantics.HasValue
                || ownership.DirectoryObjectIdentityVersion
                    != ManagedDirectoryIdentity.CurrentVersion
                || string.IsNullOrWhiteSpace(ownership.DirectoryObjectIdentity)
                || !string.IsNullOrWhiteSpace(
                    ownership.DirectoryObjectIdentityUnavailableReason)
                || string.IsNullOrWhiteSpace(ownership.PathOwnershipKey))
            {
                retirements.Add(ownership);
                continue;
            }

            string targetPath;
            try
            {
                targetPath = MapTargetPath(
                    root.Path,
                    relocation.TargetPath,
                    ownership.CanonicalPath,
                    sourceSemantics.Value,
                    targetSemantics);
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException)
            {
                retirements.Add(ownership);
                continue;
            }

            var targetGeneration = await ResolveExistingDirectoryObjectIdentityAsync(
                targetPath,
                ownership.DirectoryObjectIdentityVersion.Value,
                ownership.DirectoryObjectIdentity!,
                cancellationToken);
            if (!targetGeneration.IsAvailable)
            {
                // Metadata-only repair must never transfer cleanup authority to
                // a directory whose exact physical generation cannot be proven.
                // Retiring the old claim is conservative: it deletes nothing and
                // lets the configured root move away from unavailable storage.
                retirements.Add(ownership);
                continue;
            }

            var source = SnapshotOwnership(ownership);
            var target = SnapshotOwnership(ownership);
            target.Path = targetPath;
            target.CanonicalPath = targetPath;
            target.PathSyntax = targetSemantics.Syntax;
            target.PathCaseSensitivity =
                targetSemantics.CaseSensitivity;
            target.PathCaseSensitivityMode =
                relocation.TargetCaseSensitivityMode;
            target.PathIdentityBoundary = targetPath;
            target.PathIdentityLookupKey =
                FileSystemPathIdentity.CreateLookupKey(
                    "library-directory",
                    targetPath,
                    targetSemantics.Syntax);
            target.PathOwnershipKey = FileSystemPathIdentity.CreateKey(
                "library-directory",
                targetPath,
                targetSemantics);
            target.ManagedRootFolderId = root.Id;
            target.UpdatedAt = now;

            var journal = new LibraryDirectoryOwnershipPathMigration
            {
                OwnershipId = ownership.Id,
                RelocationId = relocation.Id,
                SourceCanonicalPath = source.CanonicalPath,
                SourcePathSyntax = source.PathSyntax,
                SourceCaseSensitivity = source.PathCaseSensitivity,
                SourceCaseSensitivityMode =
                    source.PathCaseSensitivityMode,
                SourceIdentityBoundary =
                    source.PathIdentityBoundary,
                SourceIdentityLookupKey =
                    source.PathIdentityLookupKey,
                SourceOwnershipKey = source.PathOwnershipKey!,
                TargetCanonicalPath = target.CanonicalPath,
                TargetPathSyntax = target.PathSyntax,
                TargetCaseSensitivity =
                    target.PathCaseSensitivity,
                TargetCaseSensitivityMode =
                    target.PathCaseSensitivityMode,
                TargetIdentityBoundary =
                    target.PathIdentityBoundary,
                TargetIdentityLookupKey =
                    target.PathIdentityLookupKey,
                TargetOwnershipKey = target.PathOwnershipKey!,
                CreatedAt = now,
                UpdatedAt = now
            };
            plans.Add(new OwnershipMigrationPlan(
                ownership,
                source,
                target,
                journal));
        }

        var duplicateTargetOwnershipIds = plans
            .GroupBy(plan => plan.Journal.TargetOwnershipKey)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(plan => plan.Tracked.Id))
            .ToHashSet();
        if (duplicateTargetOwnershipIds.Count > 0)
        {
            retirements.AddRange(plans
                .Where(plan => duplicateTargetOwnershipIds.Contains(plan.Tracked.Id))
                .Select(plan => plan.Tracked));
            plans.RemoveAll(plan =>
                duplicateTargetOwnershipIds.Contains(plan.Tracked.Id));
        }

        var migratingIds = plans.Select(plan => plan.Tracked.Id).ToArray();
        var targetKeys = plans
            .Select(plan => plan.Journal.TargetOwnershipKey)
            .ToArray();
        if (targetKeys.Length > 0)
        {
            var reservedTargetKeys = await db.LibraryDirectoryOwnerships
                .Where(candidate => !migratingIds.Contains(candidate.Id)
                    && candidate.State != LibraryDirectoryOwnershipState.Removed
                    && candidate.PathOwnershipKey != null
                    && targetKeys.Contains(candidate.PathOwnershipKey))
                .Select(candidate => candidate.PathOwnershipKey!)
                .ToListAsync(cancellationToken);
            if (reservedTargetKeys.Count > 0)
            {
                var reserved = reservedTargetKeys.ToHashSet(StringComparer.Ordinal);
                retirements.AddRange(plans
                    .Where(plan => reserved.Contains(plan.Journal.TargetOwnershipKey))
                    .Select(plan => plan.Tracked));
                plans.RemoveAll(plan =>
                    reserved.Contains(plan.Journal.TargetOwnershipKey));
            }
        }

        db.LibraryDirectoryOwnershipPathMigrations.AddRange(
            plans.Select(plan => plan.Journal));
        return new OwnershipMigrationPreparation(plans, retirements);
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
