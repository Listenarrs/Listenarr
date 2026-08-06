using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed class LibraryDirectoryOwnershipReconciler(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    LibraryDirectoryOwnershipBoundaryAuthorizer authorizer,
    IFilesystemMutationCoordinator mutationCoordinator,
    ILogger<LibraryDirectoryOwnershipReconciler> logger)
    : ILibraryDirectoryOwnershipReconciler
{
    public Task ReconcileAsync(CancellationToken cancellationToken = default) =>
        mutationCoordinator.ExecuteExclusiveAsync(
            ReconcileCoreAsync,
            cancellationToken);

    private async Task ReconcileCoreAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await BackfillLegacyRemovedOwnershipEvidenceAsync(db, cancellationToken);
        var retiredMarkers = await db.LibraryDirectoryOwnershipRetiredMarkers
            .Where(marker =>
                marker.State
                    == LibraryDirectoryOwnershipRetiredMarkerState.Pending)
            .ToListAsync(cancellationToken);
        foreach (var evidence in retiredMarkers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrWhiteSpace(evidence.CanonicalPayload)
                    || string.IsNullOrWhiteSpace(evidence.PayloadSha256)
                    || string.IsNullOrWhiteSpace(
                        evidence.CanonicalMarkerPath))
                {
                    LibraryDirectoryOwnershipRetiredMarkerEvidence
                        .MaterializeCanonicalPayload(evidence);
                    evidence.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                }

                ReconcileRetiredMarker(evidence);
                evidence.State =
                    LibraryDirectoryOwnershipRetiredMarkerState.Removed;
                evidence.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not (
                OperationCanceledException or OutOfMemoryException
                    or StackOverflowException))
            {
                logger.LogWarning(
                    exception,
                    "Retired directory ownership marker evidence {EvidenceId} could not be reconciled safely.",
                    evidence.Id);
            }
        }

        var ownerships = await db.LibraryDirectoryOwnerships
            .Where(ownership =>
                ownership.State != LibraryDirectoryOwnershipState.Removed
                && !db.LibraryDirectoryOwnershipPathMigrations.Any(
                    migration => migration.OwnershipId == ownership.Id))
            .ToListAsync(cancellationToken);
        foreach (var ownership in ownerships)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ownership.State == LibraryDirectoryOwnershipState.Conflict)
            {
                continue;
            }

            try
            {
                if (!FileSystemPathIdentity.TryCanonicalizeStoredPathWithIdentityForHost(
                        ownership.CanonicalPath,
                        ownership.GetIdentity(),
                        out _,
                        out var pathReason))
                {
                    throw new InvalidOperationException(pathReason);
                }

                if (ownership.State == LibraryDirectoryOwnershipState.Removing
                    && !Directory.Exists(ownership.CanonicalPath)
                    && !Directory.Exists(
                        LibraryDirectoryOwnershipRemoval.GetQuarantinePath(ownership)))
                {
                    using var missingAuthorization =
                        ownership.ManagedRootFolderId.HasValue
                            ? await authorizer.AuthorizeOwnershipAsync(
                                ownership,
                                cancellationToken)
                            : await authorizer.AuthorizeContainingRootAsync(
                                ownership.CanonicalPath,
                                ownership.GetIdentity().Semantics,
                                cancellationToken);
                    LibraryDirectoryOwnershipMarker.MarkerPayload? legacyPayload = null;
                    try
                    {
                        LibraryDirectoryOwnershipRemoval.TryValidateLegacyMissingBothRecovery(
                            ownership,
                            missingAuthorization.ParentAnchor,
                            out legacyPayload);
                    }
                    catch (Exception exception) when (exception is not (
                        OperationCanceledException or OutOfMemoryException
                            or StackOverflowException))
                    {
                        logger.LogWarning(
                            exception,
                            "An obsolete directory ownership artifact for ownership {OwnershipId} was preserved because it could not be validated; the completed removal is still converging from durable database state.",
                            ownership.Id);
                    }

                    var now = DateTime.UtcNow;
                    if (legacyPayload != null)
                    {
                        db.LibraryDirectoryOwnershipRetiredMarkers.Add(
                            LibraryDirectoryOwnershipRetiredMarkerEvidence.Create(
                                ownership,
                                legacyPayload,
                                now));
                    }
                    ownership.State = LibraryDirectoryOwnershipState.Removed;
                    ownership.PathOwnershipKey = null;
                    ownership.ManagedRootFolderId = null;
                    ownership.StateReason = null;
                    ownership.UpdatedAt = now;
                    await db.SaveChangesAsync(cancellationToken);
                    continue;
                }

                using var authorization = ownership.ManagedRootFolderId.HasValue
                    ? await authorizer.AuthorizeOwnershipAsync(
                        ownership,
                        cancellationToken)
                    : await authorizer.AuthorizeContainingRootAsync(
                        ownership.CanonicalPath,
                        ownership.GetIdentity().Semantics,
                        cancellationToken);
                var directoryName = Path.GetFileName(ownership.CanonicalPath);
                var quarantineName =
                    $".listenarr-directory-removing-{ownership.OwnershipToken}";
                using var publication =
                    authorization.ParentAnchor.TryOpenExistingChildForPublication(
                        directoryName)
                    ?? (ownership.State == LibraryDirectoryOwnershipState.Removing
                        ? authorization.ParentAnchor.TryOpenExistingChildForPublication(
                            quarantineName)
                        : null)
                    ?? throw new InvalidOperationException(
                        "The owned directory and its recovery quarantine are missing.");
                using var directory = publication.OpenCreatedDirectoryAnchor();
                var liveIdentity = directory.GetDirectoryObjectIdentity();
                if (ownership.DirectoryObjectIdentityVersion
                    == ManagedDirectoryIdentity.CurrentVersion)
                {
                    if (!ManagedDirectoryIdentity.Matches(
                            ownership.DirectoryObjectIdentityVersion,
                            ownership.DirectoryObjectIdentity,
                            ownership.OwnershipToken,
                            liveIdentity))
                    {
                        throw new InvalidOperationException(
                            "The live directory differs from its persisted Listenarr enrollment identity.");
                    }
                }
                else if (ownership.DirectoryObjectIdentityVersion == 1)
                {
                    if (!string.Equals(
                            ownership.DirectoryObjectIdentity,
                            liveIdentity,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The live directory differs from its legacy physical identity.");
                    }
                }
                else if (ownership.DirectoryObjectIdentityVersion.HasValue)
                {
                    throw new InvalidOperationException(
                        "The persisted directory identity version cannot be reconciled automatically.");
                }
                else
                {
                    // A pre-physical-identity claim can be upgraded only after the
                    // exact live directory is pinned through its managed root.
                }

                ownership.ManagedRootFolderId = authorization.RootFolderId;
                ownership.DirectoryObjectIdentityVersion =
                    ManagedDirectoryIdentity.CurrentVersion;
                ownership.DirectoryObjectIdentity = ManagedDirectoryIdentity.Create(
                    ownership.OwnershipToken,
                    liveIdentity);
                ownership.DirectoryObjectIdentityUnavailableReason = null;
                ownership.StateReason = null;
                if (ownership.State == LibraryDirectoryOwnershipState.Unavailable)
                {
                    ownership.State = LibraryDirectoryOwnershipState.Owned;
                }
                ownership.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);

                // Older builds left these marker files permanently. The durable row,
                // managed-root authorization, and pinned native directory generation
                // now provide the at-rest proof. Retire only artifacts that still match
                // this exact ownership; unrelated files are preserved.
                if (!LibraryDirectoryOwnershipMarker.TryRetireMatchingMarkers(
                        ownership,
                        directory,
                        authorization.ParentAnchor,
                        out var markerRetirementReason))
                {
                    logger.LogWarning(
                        "Obsolete directory ownership artifacts for ownership {OwnershipId} could not be retired safely and were preserved: {Reason}",
                        ownership.Id,
                        markerRetirementReason);
                }
            }
            catch (Exception exception) when (exception is not (
                OperationCanceledException or OutOfMemoryException
                    or StackOverflowException))
            {
                ownership.DirectoryObjectIdentityUnavailableReason =
                    exception.Message;
                ownership.StateReason =
                    "Physical directory ownership could not be reconciled safely.";
                ownership.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                logger.LogWarning(
                    exception,
                    "Directory ownership {OwnershipId} could not be reconciled and was disabled for destructive cleanup.",
                    ownership.Id);
            }
        }

    }

    private async Task BackfillLegacyRemovedOwnershipEvidenceAsync(
        ListenArrDbContext db,
        CancellationToken cancellationToken)
    {
        var legacyRemoved = await db.LibraryDirectoryOwnerships
            .Where(ownership =>
                ownership.State == LibraryDirectoryOwnershipState.Removed
                && (ownership.ManagedRootFolderId != null
                    || ownership.StateReason != null
                        && ownership.StateReason.StartsWith(
                            LibraryDirectoryOwnershipMigrationPreflight
                                .LegacyRemovedRootStateReasonPrefix)
                    || !db.LibraryDirectoryOwnershipRetiredMarkers.Any(
                        marker => marker.OwnershipId == ownership.Id)))
            .ToListAsync(cancellationToken);

        foreach (var ownership in legacyRemoved)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evidenceExists = await db.LibraryDirectoryOwnershipRetiredMarkers
                .AnyAsync(
                    marker => marker.OwnershipId == ownership.Id,
                    cancellationToken);
            var hasPreservedRoot =
                LibraryDirectoryOwnershipMigrationPreflight
                    .TryReadLegacyRemovedRootState(
                        ownership.StateReason,
                        out var preservedRootFolderId,
                        out var originalStateReason);
            if (!evidenceExists)
            {
                db.LibraryDirectoryOwnershipRetiredMarkers.Add(
                    LibraryDirectoryOwnershipRetiredMarkerEvidence
                        .CreateLegacyPending(
                            ownership,
                            hasPreservedRoot ? preservedRootFolderId : null));
            }

            ownership.ManagedRootFolderId = null;
            if (hasPreservedRoot)
            {
                ownership.StateReason = originalStateReason;
            }
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // A second process may have materialized the same unique evidence
                // row after our read. Reload and accept that race only when the
                // evidence now exists; otherwise preserve the original failure.
                db.ChangeTracker.Clear();
                var persistedEvidence = await db
                    .LibraryDirectoryOwnershipRetiredMarkers
                    .AnyAsync(
                        marker => marker.OwnershipId == ownership.Id,
                        cancellationToken);
                if (!persistedEvidence)
                {
                    throw;
                }

                var persistedOwnership = await db.LibraryDirectoryOwnerships
                    .SingleAsync(
                        candidate => candidate.Id == ownership.Id,
                        cancellationToken);
                var requiresOwnershipCleanup =
                    persistedOwnership.ManagedRootFolderId.HasValue;
                if (persistedOwnership.ManagedRootFolderId.HasValue)
                {
                    persistedOwnership.ManagedRootFolderId = null;
                }

                if (LibraryDirectoryOwnershipMigrationPreflight
                    .TryReadLegacyRemovedRootState(
                        persistedOwnership.StateReason,
                        out _,
                        out var persistedOriginalStateReason))
                {
                    persistedOwnership.StateReason = persistedOriginalStateReason;
                    requiresOwnershipCleanup = true;
                }

                if (requiresOwnershipCleanup)
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
            }
        }
    }

    private static void ReconcileRetiredMarker(
        LibraryDirectoryOwnershipRetiredMarker evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence.CanonicalMarkerPath))
        {
            throw new InvalidOperationException(
                "The retired ownership marker path has not been materialized.");
        }

        if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                evidence.CanonicalMarkerPath,
                out var canonicalMarkerPath,
                out var reason)
            || !FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                canonicalMarkerPath,
                out var markerSyntax)
            || markerSyntax != evidence.PathSyntax)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(reason)
                    ? "The retired ownership marker path syntax does not match its persisted evidence."
                    : reason);
        }

        var parentPath = Path.GetDirectoryName(canonicalMarkerPath)
            ?? throw new InvalidOperationException(
                "The retired ownership marker has no parent directory.");
        using var parent =
            PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parentPath);
        using var marker = parent.TryOpenExistingFile(
            Path.GetFileName(canonicalMarkerPath),
            requireDeleteAccess: true);
        if (marker == null)
        {
            return;
        }

        var payload = LibraryDirectoryOwnershipMarker.ReadPayload(marker);
        if (!LibraryDirectoryOwnershipRetiredMarkerEvidence.Matches(
                evidence,
                payload)
            || !parent.VisiblePathMatches()
            || !marker.VisiblePathMatches()
            || !LibraryDirectoryOwnershipRetiredMarkerEvidence.Matches(
                evidence,
                LibraryDirectoryOwnershipMarker.ReadPayload(marker)))
        {
            throw new InvalidOperationException(
                "The retired ownership marker does not match its immutable cleanup evidence.");
        }

        marker.Delete();
    }
}
