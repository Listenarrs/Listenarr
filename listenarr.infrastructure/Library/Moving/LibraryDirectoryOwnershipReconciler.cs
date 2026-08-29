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
    internal Action<LibraryDirectoryOwnership>? AfterRemovingDirectoryObservedMissingForTest
    {
        get;
        set;
    }

    internal Action<LibraryDirectoryOwnership>? AfterOwnershipAuthoritySavedForTest
    {
        get;
        set;
    }

    public Task ReconcileAsync(CancellationToken cancellationToken = default) =>
        mutationCoordinator.ExecuteExclusiveAsync(
            ReconcileCoreAsync,
            cancellationToken);

    private async Task ReconcileCoreAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
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

                using var authorization = ownership.ManagedRootFolderId.HasValue
                    ? await authorizer.AuthorizeOwnershipAsync(
                        ownership,
                        cancellationToken)
                    : await authorizer.AuthorizeContainingRootAsync(
                        ownership.CanonicalPath,
                        ownership.GetIdentity().Semantics,
                        cancellationToken);
                var directoryName = Path.GetFileName(ownership.CanonicalPath);
                using var publication =
                    authorization.ParentAnchor.TryOpenExistingChildForPublication(
                        directoryName);
                if (publication == null)
                {
                    if (ownership.State != LibraryDirectoryOwnershipState.Removing)
                    {
                        throw new InvalidOperationException(
                            "The owned directory is missing.");
                    }

                    AfterRemovingDirectoryObservedMissingForTest?.Invoke(ownership);
                    if (!authorization.ParentAnchor.VisiblePathMatches())
                    {
                        throw new IOException(
                            "The owned directory parent changed while physical removal was being proved.");
                    }

                    var now = DateTime.UtcNow;
                    ownership.State = LibraryDirectoryOwnershipState.Removed;
                    ownership.PathOwnershipKey = null;
                    ownership.ManagedRootFolderId = null;
                    ownership.StateReason = null;
                    ownership.UpdatedAt = now;
                    await db.SaveChangesAsync(cancellationToken);
                    continue;
                }
                using var directory = publication.OpenCreatedDirectoryAnchor();
                if (ownership.DirectoryObjectIdentityVersion
                    != ManagedDirectoryIdentity.CurrentVersion
                    || !directory.MatchesManagedDirectoryOwnershipIdentity(
                        ownership.DirectoryObjectIdentityVersion,
                        ownership.DirectoryObjectIdentity,
                        ownership.OwnershipToken))
                {
                    throw new InvalidOperationException(
                        "The persisted directory ownership identity is not the current supported generation.");
                }

                await using var authorityTransaction = db.Database.IsRelational()
                    ? await db.Database.BeginTransactionAsync(cancellationToken)
                    : null;
                ownership.ManagedRootFolderId = authorization.RootFolderId;
                ownership.DirectoryObjectIdentityUnavailableReason = null;
                ownership.StateReason = null;
                if (ownership.State == LibraryDirectoryOwnershipState.Unavailable)
                {
                    ownership.State = LibraryDirectoryOwnershipState.Owned;
                }
                ownership.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                AfterOwnershipAuthoritySavedForTest?.Invoke(ownership);
                if (!directory.MatchesManagedDirectoryOwnershipIdentity(
                        ownership.DirectoryObjectIdentityVersion,
                        ownership.DirectoryObjectIdentity,
                        ownership.OwnershipToken)
                    || !directory.VisiblePathMatches()
                    || !authorization.ParentAnchor.VisiblePathMatches())
                {
                    throw new InvalidOperationException(
                        "The directory ownership generation changed before reconciled destructive authority committed.");
                }
                if (authorityTransaction != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await authorityTransaction.CommitAsync(CancellationToken.None);
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
}
