using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Persistence;

public sealed class RootFolderObjectIdentityReconciler(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    IDirectoryObjectIdentityResolver identityResolver,
    IFilesystemMutationCoordinator mutationCoordinator,
    ILogger<RootFolderObjectIdentityReconciler> logger)
    : IRootFolderObjectIdentityReconciler
{
    public Task ReconcileAsync(CancellationToken cancellationToken = default) =>
        mutationCoordinator.ExecuteExclusiveAsync(
            ReconcileCoreAsync,
            cancellationToken);

    private async Task ReconcileCoreAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var roots = await db.RootFolders.ToListAsync(cancellationToken);
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    root.Path,
                    out var canonicalRootPath,
                    out var pathReason))
            {
                root.DirectoryObjectIdentityUnavailableReason = pathReason;
                logger.LogWarning(
                    "Root folder {RootFolderId} path is unavailable on this host; destructive ownership cleanup is disabled.",
                    root.Id);
                continue;
            }

            if (root.DirectoryObjectIdentityVersion == null
                || string.IsNullOrWhiteSpace(root.DirectoryObjectIdentity))
            {
                // Startup observation must never turn the first directory visible at a path
                // into trusted storage. This is especially important for temporarily absent
                // Docker/NAS mounts where the underlying mountpoint directory may still exist.
                root.DirectoryObjectIdentityUnavailableReason =
                    "The root folder physical directory has not been confirmed.";
                logger.LogWarning(
                    "Root folder {RootFolderId} has no authorized physical directory; filesystem mutation remains disabled until the folder is explicitly confirmed or the root path is changed.",
                    root.Id);
                continue;
            }

            var current = await identityResolver.ResolveExistingAsync(
                canonicalRootPath,
                root.DirectoryObjectIdentityVersion.Value,
                root.DirectoryObjectIdentity,
                cancellationToken);
            if (!current.IsAvailable)
            {
                root.DirectoryObjectIdentityUnavailableReason =
                    current.UnavailableReason
                    ?? "The live directory no longer matches its enrolled identity.";
                logger.LogWarning(
                    "Root folder {RootFolderId} enrolled identity is unavailable or mismatched; destructive ownership cleanup is disabled.",
                    root.Id);
                continue;
            }

            root.DirectoryObjectIdentityUnavailableReason = null;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

}
