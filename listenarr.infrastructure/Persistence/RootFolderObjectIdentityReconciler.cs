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
    internal Action<RootFolder>? AfterRootAuthoritySavedForTest
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
                await db.SaveChangesAsync(cancellationToken);
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
                await db.SaveChangesAsync(cancellationToken);
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
                    "Root folder {RootFolderId} enrolled identity is unavailable or mismatched; destructive ownership cleanup is disabled. Reason: {Reason}",
                    root.Id,
                    root.DirectoryObjectIdentityUnavailableReason);
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }

            try
            {
                using var pinnedRoot = PinnedDirectoryCreation.OpenPinnedBoundary(
                    canonicalRootPath);
                var initialVisibility = pinnedRoot.ProbeVisiblePathMatch();
                if (!pinnedRoot.MatchesManagedDirectoryIdentity(
                        root.DirectoryObjectIdentityVersion,
                        root.DirectoryObjectIdentity)
                    || initialVisibility != RegistrationPublicationMatchOutcome.Match)
                {
                    root.DirectoryObjectIdentityUnavailableReason =
                        initialVisibility == RegistrationPublicationMatchOutcome.Unavailable
                            ? "The root folder is temporarily unavailable while its authorized physical generation is being verified."
                            : "The live root directory no longer matches its enrolled physical identity.";
                    await db.SaveChangesAsync(cancellationToken);
                    continue;
                }

                await using var authorityTransaction = db.Database.IsRelational()
                    ? await db.Database.BeginTransactionAsync(cancellationToken)
                    : null;
                root.DirectoryObjectIdentityUnavailableReason = null;
                await db.SaveChangesAsync(cancellationToken);
                AfterRootAuthoritySavedForTest?.Invoke(root);
                cancellationToken.ThrowIfCancellationRequested();
                var commitVisibility = pinnedRoot.ProbeVisiblePathMatch();
                if (!pinnedRoot.MatchesManagedDirectoryIdentity(
                        root.DirectoryObjectIdentityVersion,
                        root.DirectoryObjectIdentity)
                    || commitVisibility != RegistrationPublicationMatchOutcome.Match)
                {
                    throw commitVisibility == RegistrationPublicationMatchOutcome.Unavailable
                        ? new IOException(
                            "The root folder became temporarily unavailable before reconciled filesystem authority committed.")
                        : new InvalidOperationException(
                            "The root folder changed physical generation before reconciled filesystem authority committed.");
                }

                if (authorityTransaction != null)
                {
                    await authorityTransaction.CommitAsync(CancellationToken.None);
                }
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException
                    or InvalidOperationException
                    or System.ComponentModel.Win32Exception)
            {
                root.DirectoryObjectIdentityUnavailableReason = exception.Message;
                await db.SaveChangesAsync(CancellationToken.None);
                logger.LogWarning(
                    exception,
                    "Root folder {RootFolderId} could not restore reconciled filesystem authority safely.",
                    root.Id);
            }
        }
    }

}
