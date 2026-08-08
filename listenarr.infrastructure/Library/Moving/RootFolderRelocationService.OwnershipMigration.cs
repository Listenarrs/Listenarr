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
            if (!ManagedDirectoryIdentity.Matches(
                    _plan.Source.DirectoryObjectIdentityVersion,
                    _plan.Source.DirectoryObjectIdentity,
                    _plan.Source.OwnershipToken,
                    nativeIdentity)
                || !_directory.VisiblePathMatches()
                || !_parent.VisiblePathMatches())
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

    private sealed record MetadataRewriteSnapshot(
        Audiobook Audiobook,
        string? BasePath,
        string? FilePath,
        string? ImageUrl,
        IReadOnlyList<(AudiobookFile File, AudiobookFilePathState State)> Files);

    private static void PreflightMetadataPathRewrites(
        ListenArrDbContext db,
        IReadOnlyList<(AudiobookPathCandidate Candidate, string Destination)> plans,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        FileSystemCaseSensitivityMode targetMode)
    {
        var snapshots = plans.Select(plan =>
            new MetadataRewriteSnapshot(
                plan.Candidate.Audiobook,
                plan.Candidate.Audiobook.BasePath,
                plan.Candidate.Audiobook.FilePath,
                plan.Candidate.Audiobook.ImageUrl,
                (plan.Candidate.Audiobook.Files ?? [])
                    .Select(file => (file, file.CapturePathState()))
                    .ToArray()))
            .ToArray();
        try
        {
            foreach (var plan in plans)
            {
                AudiobookPathReferenceRewriter.Rewrite(
                    plan.Candidate.Audiobook,
                    plan.Candidate.StoredBasePath,
                    plan.Destination,
                    sourceSemantics,
                    targetSemantics,
                    targetMode);
            }
            RejectDuplicateAudiobookFileOwnership(db);
        }
        finally
        {
            foreach (var snapshot in snapshots)
            {
                snapshot.Audiobook.BasePath = snapshot.BasePath;
                snapshot.Audiobook.FilePath = snapshot.FilePath;
                snapshot.Audiobook.ImageUrl = snapshot.ImageUrl;
                foreach (var (file, state) in snapshot.Files)
                {
                    file.RestorePathState(state);
                }
            }
        }
    }

    private async Task<IReadOnlyList<OwnershipMigrationPlan>>
        PrepareOwnershipMigrationsAsync(
            ListenArrDbContext db,
            RootFolderRelocation relocation,
            RootFolder root,
            FileSystemPathSemantics? sourceSemantics,
            FileSystemPathSemantics targetSemantics,
            CancellationToken cancellationToken)
    {
        var ownerships = await db.LibraryDirectoryOwnerships
            .Where(ownership =>
                ownership.ManagedRootFolderId == root.Id
                && ownership.State != LibraryDirectoryOwnershipState.Removed)
            .ToListAsync(cancellationToken);
        if (ownerships.Count == 0)
        {
            return [];
        }
        if (!sourceSemantics.HasValue)
        {
            throw new InvalidOperationException(
                "Stored source path semantics are unavailable for live directory ownership migration.");
        }
        if (ownerships.Any(ownership => ownership.State is not (
            LibraryDirectoryOwnershipState.Owned
                or LibraryDirectoryOwnershipState.Retained)))
        {
            throw new InvalidOperationException(
                "Metadata-only relocation is blocked while directory ownership is removing, conflicted, or unavailable.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var plans = new List<OwnershipMigrationPlan>(ownerships.Count);
        foreach (var ownership in ownerships)
        {
            if (ownership.DirectoryObjectIdentityVersion
                    != ManagedDirectoryIdentity.CurrentVersion
                || string.IsNullOrWhiteSpace(
                    ownership.DirectoryObjectIdentity)
                || !string.IsNullOrWhiteSpace(
                    ownership.DirectoryObjectIdentityUnavailableReason))
            {
                throw new InvalidOperationException(
                    "Metadata-only relocation requires an enrolled physical identity for every owned directory.");
            }

            var targetPath = MapTargetPath(
                root.Path,
                relocation.TargetPath,
                ownership.CanonicalPath,
                sourceSemantics.Value,
                targetSemantics);
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
                SourceOwnershipKey = source.PathOwnershipKey
                    ?? throw new InvalidOperationException(
                        "A live ownership claim has no reserved path key."),
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

        var duplicateTarget = plans
            .GroupBy(plan => plan.Journal.TargetOwnershipKey)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateTarget != null)
        {
            throw new InvalidOperationException(
                "Multiple ownership claims map to the same relocation target.");
        }
        var migratingIds = plans.Select(plan => plan.Tracked.Id).ToArray();
        var targetKeys = plans
            .Select(plan => plan.Journal.TargetOwnershipKey)
            .ToArray();
        if (targetKeys.Length > 0
            && await db.LibraryDirectoryOwnerships.AnyAsync(
                candidate => !migratingIds.Contains(candidate.Id)
                    && candidate.State
                        != LibraryDirectoryOwnershipState.Removed
                    && candidate.PathOwnershipKey != null
                    && targetKeys.Contains(candidate.PathOwnershipKey),
                cancellationToken))
        {
            throw new InvalidOperationException(
                "A relocation ownership target is already reserved.");
        }

        db.LibraryDirectoryOwnershipPathMigrations.AddRange(
            plans.Select(plan => plan.Journal));
        return plans;
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
