using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class EfLibraryDirectoryOwnershipStore
{
    internal Action? AfterMarkerlessReplacementCommitForTest
    {
        get;
        set;
    }

    public async Task<bool> TryRetireReplacedByMarkerlessMoveAsync(
        string path,
        FileSystemPathSemantics semantics,
        Guid moveJobId,
        string replacementDirectoryObjectIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementDirectoryObjectIdentity);
        if (moveJobId == Guid.Empty)
        {
            throw new ArgumentException(
                "A markerless replacement proof requires a move job ID.",
                nameof(moveJobId));
        }
        EnsureResolved(semantics);

        var canonicalPath = FileSystemPathIdentity.Canonicalize(
            path,
            semantics.Syntax);
        var lookupKey = FileSystemPathIdentity.CreateLookupKey(
            IdentityScope,
            canonicalPath,
            semantics.Syntax);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var candidates = await db.LibraryDirectoryOwnerships
            .Where(ownership => ownership.PathIdentityLookupKey == lookupKey
                && ownership.State != LibraryDirectoryOwnershipState.Removed)
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return false;
        }

        var compatible = new List<LibraryDirectoryOwnership>();
        var conflicting = false;
        foreach (var candidate in candidates)
        {
            var comparison = Compare(candidate, canonicalPath, semantics);
            if (comparison == OwnershipComparison.Compatible
                && candidate.State is LibraryDirectoryOwnershipState.Owned
                    or LibraryDirectoryOwnershipState.Retained
                    or LibraryDirectoryOwnershipState.Unavailable)
            {
                compatible.Add(candidate);
            }
            else if (comparison is OwnershipComparison.Compatible
                or OwnershipComparison.Conflict)
            {
                conflicting = true;
            }
        }

        if (compatible.Count == 0 && !conflicting)
        {
            return false;
        }
        if (conflicting || compatible.Count != 1)
        {
            throw new InvalidOperationException(
                "The markerless replacement path has conflicting durable ownership claims.");
        }

        var stale = compatible[0];
        var originalManagedRootFolderId = stale.ManagedRootFolderId;
        if (string.IsNullOrWhiteSpace(stale.PathOwnershipKey))
        {
            throw new InvalidOperationException(
                "The prior directory ownership claim is not eligible for markerless replacement retirement.");
        }

        var move = await db.MoveJobs
            .AsNoTracking()
            .Include(job => job.CreatedDirectories)
            .SingleOrDefaultAsync(job => job.Id == moveJobId, cancellationToken);
        if (move == null
            || !MoveExecutionProtocol.IsCurrent(move.ExecutionProtocolVersion)
            || string.IsNullOrWhiteSpace(move.RequestedPath)
            || !FileSystemPathIdentity.AreEquivalent(
                canonicalPath,
                move.RequestedPath,
                semantics)
            || !PersistedDirectoryObjectIdentitiesEquivalent(
                move.TargetDirectoryObjectIdentity,
                replacementDirectoryObjectIdentity))
        {
            return false;
        }

        var creationEvidence = move.CreatedDirectories
            .Where(directory => FileSystemPathIdentity.AreEquivalent(
                directory.Path,
                canonicalPath,
                semantics))
            .ToList();
        if (creationEvidence.Count != 1
            || creationEvidence[0].State != MoveCreatedDirectoryState.Created
            || !PersistedDirectoryObjectIdentitiesEquivalent(
                creationEvidence[0].DirectoryObjectIdentity,
                replacementDirectoryObjectIdentity))
        {
            return false;
        }

        using var authorization = await _boundaryAuthorizer.AuthorizeContainingRootAsync(
            canonicalPath,
            semantics,
            cancellationToken);
        using var liveDirectory = authorization.ParentAnchor.OpenExistingChild(
            Path.GetFileName(canonicalPath));
        if (!liveDirectory.MatchesDirectoryObjectIdentity(
                replacementDirectoryObjectIdentity)
            || !DirectoryVisibilityMatchesOrThrowUnavailable(
                liveDirectory,
                "The markerless replacement directory is temporarily unavailable while its move generation is being verified.")
            || !DirectoryVisibilityMatchesOrThrowUnavailable(
                authorization.ParentAnchor,
                "The markerless replacement directory parent is temporarily unavailable while its move generation is being verified."))
        {
            throw new InvalidOperationException(
                "The markerless replacement directory no longer matches its persisted move generation.");
        }
        if (liveDirectory.MatchesManagedDirectoryOwnershipIdentity(
                stale.DirectoryObjectIdentityVersion,
                stale.DirectoryObjectIdentity,
                stale.OwnershipToken))
        {
            return false;
        }
        if (!DirectoryVisibilityMatchesOrThrowUnavailable(
                liveDirectory,
                "The markerless replacement directory is temporarily unavailable before stale ownership retirement.")
            || !DirectoryVisibilityMatchesOrThrowUnavailable(
                authorization.ParentAnchor,
                "The markerless replacement directory parent is temporarily unavailable before stale ownership retirement."))
        {
            throw new InvalidOperationException(
                "The markerless replacement directory changed before stale ownership retirement.");
        }

        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        stale.State = LibraryDirectoryOwnershipState.Removed;
        stale.PathOwnershipKey = null;
        stale.ManagedRootFolderId = null;
        stale.StateReason = null;
        stale.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        if (transaction != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None);
        }

        AfterMarkerlessReplacementCommitForTest?.Invoke();
        var postCommitDirectoryVisibility = liveDirectory.ProbeVisiblePathMatch();
        var postCommitParentVisibility = authorization.ParentAnchor.ProbeVisiblePathMatch();
        if (!liveDirectory.MatchesDirectoryObjectIdentity(
                replacementDirectoryObjectIdentity)
            || postCommitDirectoryVisibility == RegistrationPublicationMatchOutcome.Mismatch
            || postCommitParentVisibility == RegistrationPublicationMatchOutcome.Mismatch)
        {
            var reason =
                "The markerless replacement directory changed physical generation immediately after stale ownership retirement committed.";
            await using var repairDb = await dbContextFactory.CreateDbContextAsync(
                CancellationToken.None);
            var persisted = await repairDb.LibraryDirectoryOwnerships
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == stale.Id,
                    CancellationToken.None);
            if (persisted != null
                && persisted.State == LibraryDirectoryOwnershipState.Removed)
            {
                persisted.State = LibraryDirectoryOwnershipState.Unavailable;
                persisted.PathOwnershipKey = null;
                persisted.ManagedRootFolderId = originalManagedRootFolderId;
                persisted.StateReason = reason;
                persisted.DirectoryObjectIdentityUnavailableReason = reason;
                persisted.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
                await repairDb.SaveChangesAsync(CancellationToken.None);
            }

            throw new InvalidOperationException(reason);
        }

        return true;
    }

    private static bool PersistedDirectoryObjectIdentitiesEquivalent(
        string? left,
        string right) =>
        !string.IsNullOrWhiteSpace(left)
        && (string.Equals(left, right, StringComparison.Ordinal)
            || PinnedDirectoryCreation.ArePersistedObjectIdentitiesDurablyEquivalent(
                left,
                right));
}
