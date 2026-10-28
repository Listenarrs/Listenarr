using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class EfLibraryDirectoryOwnershipStore
{
    private async Task ThrowIfPersistedPathConflictAsync(
        string canonicalPath,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken)
    {
        var lookupKey = FileSystemPathIdentity.CreateLookupKey(
            IdentityScope,
            canonicalPath,
            semantics.Syntax);
        await using var db =
            await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var candidates = await db.LibraryDirectoryOwnerships
            .Where(ownership =>
                ownership.PathIdentityLookupKey == lookupKey
                && ownership.State !=
                    LibraryDirectoryOwnershipState.Removed)
            .ToListAsync(cancellationToken);

        var matching = candidates
            .Select(candidate => new
            {
                Ownership = candidate,
                Comparison = Compare(
                    candidate,
                    canonicalPath,
                    semantics)
            })
            .Where(candidate => candidate.Comparison is
                OwnershipComparison.Compatible
                    or OwnershipComparison.Conflict)
            .ToList();
        var compatible = matching.Count(candidate =>
            candidate.Comparison == OwnershipComparison.Compatible
            && candidate.Ownership.State is
                LibraryDirectoryOwnershipState.Owned
                    or LibraryDirectoryOwnershipState.Retained);
        if (compatible <= 1
            && matching.All(candidate =>
                candidate.Comparison ==
                    OwnershipComparison.Compatible
                && candidate.Ownership.State is
                    LibraryDirectoryOwnershipState.Owned
                        or LibraryDirectoryOwnershipState.Retained
                        or LibraryDirectoryOwnershipState.Unavailable))
        {
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var candidate in matching.Select(item => item.Ownership))
        {
            candidate.State =
                LibraryDirectoryOwnershipState.Conflict;
            candidate.PathOwnershipKey = null;
            candidate.StateReason =
                "Conflicting durable ownership claims resolve to this directory.";
            candidate.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        if (transaction != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None);
        }

        throw new InvalidOperationException(
            "The requested library directory conflicts with an existing durable ownership claim.");
    }
}
