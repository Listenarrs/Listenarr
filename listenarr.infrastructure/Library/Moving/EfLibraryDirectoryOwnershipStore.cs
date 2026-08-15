using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class EfLibraryDirectoryOwnershipStore(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    TimeProvider timeProvider,
    LibraryDirectoryOwnershipBoundaryAuthorizer? boundaryAuthorizer = null)
    : ILibraryDirectoryOwnershipStore
{
    private const string IdentityScope = "library-directory";
    private readonly LibraryDirectoryOwnershipBoundaryAuthorizer _boundaryAuthorizer =
        boundaryAuthorizer
        ?? new LibraryDirectoryOwnershipBoundaryAuthorizer(dbContextFactory);

    internal Action? BeforeNewOwnershipCommitForTest
    {
        get;
        set;
    }

    internal Action? AfterOwnedDirectoryPhysicalIdentityPinnedForTest
    {
        get;
        set;
    }

    internal Action? AfterOwnershipCommitForTest
    {
        get;
        set;
    }

    public async Task<LibraryDirectoryOwnership> RecordCreatedAsync(
        LibraryDirectoryOwnershipClaim claim,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentException.ThrowIfNullOrWhiteSpace(claim.Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(claim.CreationWorkflow);
        EnsureResolved(claim.Semantics);
        var canonicalPath = FileSystemPathIdentity.Canonicalize(
            claim.Path,
            claim.Semantics.Syntax);
        await ThrowIfPersistedPathConflictAsync(
            canonicalPath,
            claim.Semantics,
            cancellationToken);
        using var authorization = await _boundaryAuthorizer.AuthorizeContainingRootAsync(
            claim.Path,
            claim.Semantics,
            cancellationToken);
        using var existing = authorization.ParentAnchor.OpenExistingChildForPublication(
            Path.GetFileName(canonicalPath));
        return await RecordCreatedCoreAsync(
            claim,
            existing,
            pinnedCreationIsNew: false,
            authorization.RootFolderId,
            cancellationToken);
    }

    public async Task<LibraryDirectoryOwnership> ClaimRetainedAsync(
        LibraryDirectoryOwnershipClaim claim,
        CancellationToken cancellationToken = default) =>
        await RecordCreatedAsync(claim, cancellationToken);

    internal Task<LibraryDirectoryOwnership> RecordPinnedCreatedAsync(
        LibraryDirectoryOwnershipClaim claim,
        PinnedDirectoryCreation pinnedCreation,
        int managedRootFolderId,
        CancellationToken cancellationToken = default) =>
        RecordCreatedCoreAsync(
            claim,
            pinnedCreation,
            pinnedCreationIsNew: true,
            managedRootFolderId,
            cancellationToken);

    private Task<LibraryDirectoryOwnership> RepairPinnedExistingAsync(
        LibraryDirectoryOwnershipClaim claim,
        PinnedDirectoryCreation pinnedCreation,
        int managedRootFolderId,
        CancellationToken cancellationToken = default) =>
        RecordCreatedCoreAsync(
            claim,
            pinnedCreation,
            pinnedCreationIsNew: false,
            managedRootFolderId,
            cancellationToken);

    private async Task<LibraryDirectoryOwnership> RecordCreatedCoreAsync(
        LibraryDirectoryOwnershipClaim claim,
        PinnedDirectoryCreation? pinnedCreation,
        bool pinnedCreationIsNew,
        int? managedRootFolderId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentException.ThrowIfNullOrWhiteSpace(claim.Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(claim.CreationWorkflow);
        EnsureResolved(claim.Semantics);

        var canonicalPath = FileSystemPathIdentity.Canonicalize(
            claim.Path,
            claim.Semantics.Syntax);
        using var existingPinnedCreation = pinnedCreation == null
            ? PinnedDirectoryCreation.OpenExistingForPublication(
                Path.GetDirectoryName(canonicalPath)
                    ?? throw new InvalidOperationException(
                        "The claimed directory has no parent directory."),
                Path.GetFileName(canonicalPath))
            : null;
        var markerCreation = pinnedCreation ?? existingPinnedCreation
            ?? throw new InvalidOperationException(
                "The claimed directory could not be pinned.");
        if (!markerCreation.Created
            || !FileSystemPathIdentity.AreEquivalent(
                markerCreation.FullPath,
                canonicalPath,
                claim.Semantics)
            || !markerCreation.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The claimed directory no longer matches its validated pathname.");
        }
        using var claimedDirectory = markerCreation.OpenCreatedDirectoryAnchor();
        var directoryObjectIdentity = claimedDirectory.GetDirectoryObjectIdentity();
        if (!claimedDirectory.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The claimed directory changed while its physical identity was captured.");
        }

        var lookupKey = FileSystemPathIdentity.CreateLookupKey(
            IdentityScope,
            canonicalPath,
            claim.Semantics.Syntax);
        var ownershipKey = FileSystemPathIdentity.CreateKey(
            IdentityScope,
            canonicalPath,
            claim.Semantics);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var candidates = await db.LibraryDirectoryOwnerships
            .Where(ownership => ownership.PathIdentityLookupKey == lookupKey
                && ownership.State != LibraryDirectoryOwnershipState.Removed)
            .ToListAsync(cancellationToken);

        var compatible = new List<LibraryDirectoryOwnership>();
        var conflicts = new List<LibraryDirectoryOwnership>();
        foreach (var candidate in candidates)
        {
            var comparison = Compare(candidate, canonicalPath, claim.Semantics);
            if (comparison == OwnershipComparison.Compatible
                && candidate.State is LibraryDirectoryOwnershipState.Owned
                    or LibraryDirectoryOwnershipState.Retained
                    or LibraryDirectoryOwnershipState.Unavailable)
            {
                compatible.Add(candidate);
            }
            else if (comparison is OwnershipComparison.Compatible or OwnershipComparison.Conflict)
            {
                conflicts.Add(candidate);
            }
        }

        if (pinnedCreationIsNew && (compatible.Count > 0 || conflicts.Count > 0))
        {
            throw new InvalidOperationException(
                "The newly created directory conflicts with an existing durable ownership claim.");
        }

        if (compatible.Count == 1 && conflicts.Count == 0)
        {
            var existing = compatible[0];
            EnsureAuthorizedPhysicalIdentity(
                existing,
                managedRootFolderId,
                directoryObjectIdentity);
            cancellationToken.ThrowIfCancellationRequested();
            ValidatePinnedOwnership(existing, markerCreation);
            existing.State = LibraryDirectoryOwnershipState.Owned;
            existing.StateReason = null;
            existing.DirectoryObjectIdentityUnavailableReason = null;
            existing.UpdatedAt = now;
            await db.SaveChangesAsync(CancellationToken.None);
            if (transaction != null)
            {
                await transaction.CommitAsync(CancellationToken.None);
            }
            await RevalidateCommittedOwnershipAsync(
                existing,
                markerCreation,
                CancellationToken.None);
            return existing;
        }

        if (compatible.Count > 1 || conflicts.Count > 0)
        {
            foreach (var candidate in compatible.Concat(conflicts).DistinctBy(item => item.Id))
            {
                candidate.State = LibraryDirectoryOwnershipState.Conflict;
                candidate.PathOwnershipKey = null;
                candidate.StateReason = "Conflicting durable ownership claims resolve to this directory.";
                candidate.UpdatedAt = now;
            }

            db.LibraryDirectoryOwnerships.Add(CreateOwnership(
                claim,
                canonicalPath,
                lookupKey,
                ownershipKey: null,
                LibraryDirectoryOwnershipState.Conflict,
                "Conflicting durable ownership claims resolve to this directory.",
                managedRootFolderId,
                directoryObjectIdentity,
                now));
            await db.SaveChangesAsync(cancellationToken);
            if (transaction != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await transaction.CommitAsync(CancellationToken.None);
            }
            throw new InvalidOperationException(
                "The created library directory conflicts with an existing durable ownership claim.");
        }

        var ownership = CreateOwnership(
            claim,
            canonicalPath,
            lookupKey,
            ownershipKey,
            LibraryDirectoryOwnershipState.Owned,
            reason: null,
            managedRootFolderId,
            directoryObjectIdentity,
            now);
        ValidatePinnedOwnership(ownership, markerCreation);
        BeforeNewOwnershipCommitForTest?.Invoke();
        db.LibraryDirectoryOwnerships.Add(ownership);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (transaction != null)
            {
                await transaction.CommitAsync(CancellationToken.None);
            }
            await RevalidateCommittedOwnershipAsync(
                ownership,
                markerCreation,
                CancellationToken.None);
        }
        catch (UniqueConstraintViolationException)
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            if (pinnedCreation != null)
            {
                throw new InvalidOperationException(
                    "The newly created directory lost a concurrent durable ownership race.");
            }

            await using var retryDb = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var concurrent = await retryDb.LibraryDirectoryOwnerships
                .SingleOrDefaultAsync(
                    candidate => candidate.PathOwnershipKey == ownershipKey,
                    cancellationToken);
            if (concurrent != null
                && Compare(concurrent, canonicalPath, claim.Semantics) == OwnershipComparison.Compatible)
            {
                EnsureAuthorizedPhysicalIdentity(
                    concurrent,
                    managedRootFolderId,
                    directoryObjectIdentity);
                cancellationToken.ThrowIfCancellationRequested();
                ValidatePinnedOwnership(concurrent, markerCreation);
                concurrent.State = LibraryDirectoryOwnershipState.Owned;
                concurrent.StateReason = null;
                concurrent.DirectoryObjectIdentityUnavailableReason = null;
                concurrent.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
                await retryDb.SaveChangesAsync(CancellationToken.None);
                await RevalidateCommittedOwnershipAsync(
                    concurrent,
                    markerCreation,
                    CancellationToken.None);
                return concurrent;
            }
            throw;
        }

        return ownership;
    }

}
