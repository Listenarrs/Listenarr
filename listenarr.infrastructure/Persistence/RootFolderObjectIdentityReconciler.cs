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

            DirectoryObjectIdentityResolution current;
            if (root.DirectoryObjectIdentityVersion == null
                || string.IsNullOrWhiteSpace(root.DirectoryObjectIdentity))
            {
                current = await identityResolver.ResolveAsync(
                    canonicalRootPath,
                    cancellationToken);
            }
            else if (root.DirectoryObjectIdentityVersion
                == ManagedDirectoryIdentity.CurrentVersion)
            {
                current = await identityResolver.ResolveExistingAsync(
                    canonicalRootPath,
                    root.DirectoryObjectIdentityVersion.Value,
                    root.DirectoryObjectIdentity,
                    cancellationToken);
            }
            else
            {
                current = DirectoryObjectIdentityResolution.Unavailable(
                    $"Directory identity version {root.DirectoryObjectIdentityVersion} is unsupported.");
            }

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

            if (root.DirectoryObjectIdentityVersion
                    == ManagedDirectoryIdentity.CurrentVersion
                && !string.Equals(
                    current.Value,
                    root.DirectoryObjectIdentity,
                    StringComparison.Ordinal))
            {
                root.DirectoryObjectIdentityUnavailableReason =
                    "The live directory enrollment differs from the persisted root identity.";
                logger.LogWarning(
                    "Root folder {RootFolderId} enrolled identity changed; destructive ownership cleanup is disabled.",
                    root.Id);
                continue;
            }

            root.DirectoryObjectIdentityVersion = current.Version;
            root.DirectoryObjectIdentity = current.Value;
            root.DirectoryObjectIdentityUnavailableReason = null;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

}
