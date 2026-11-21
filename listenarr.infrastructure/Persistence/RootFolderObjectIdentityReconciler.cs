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

            try
            {
                using var pinnedRoot = PinnedDirectoryCreation.OpenPinnedBoundary(
                    canonicalRootPath);
                var initialVisibility = pinnedRoot.ProbeVisiblePathMatch();
                if (initialVisibility != RegistrationPublicationMatchOutcome.Match)
                {
                    root.DirectoryObjectIdentityUnavailableReason =
                        initialVisibility == RegistrationPublicationMatchOutcome.Unavailable
                            ? "The root folder is temporarily unavailable while its current path is being observed."
                            : "The root folder changed while its current path was being observed.";
                    await db.SaveChangesAsync(cancellationToken);
                    continue;
                }

                var current = await identityResolver.ResolveAsync(
                    canonicalRootPath,
                    cancellationToken);
                await using var observationTransaction = db.Database.IsRelational()
                    ? await db.Database.BeginTransactionAsync(cancellationToken)
                    : null;
                if (current.IsAvailable)
                {
                    root.DirectoryObjectIdentityVersion = current.Version;
                    root.DirectoryObjectIdentity = current.Value;
                    root.DirectoryObjectIdentityUnavailableReason = null;
                }
                else
                {
                    // Physical identity is optional diagnostic information. Keep
                    // any prior observation for troubleshooting, but never turn
                    // identity unavailability into filesystem mutation authority.
                    root.DirectoryObjectIdentityUnavailableReason =
                        current.UnavailableReason
                        ?? "The current root-folder physical identity is unavailable.";
                }

                await db.SaveChangesAsync(cancellationToken);
                AfterRootAuthoritySavedForTest?.Invoke(root);
                cancellationToken.ThrowIfCancellationRequested();
                var commitVisibility = pinnedRoot.ProbeVisiblePathMatch();
                if (commitVisibility != RegistrationPublicationMatchOutcome.Match)
                {
                    throw commitVisibility == RegistrationPublicationMatchOutcome.Unavailable
                        ? new IOException(
                            "The root folder became temporarily unavailable while its diagnostic observation was being committed.")
                        : new InvalidOperationException(
                            "The root folder changed while its diagnostic observation was being committed.");
                }

                if (observationTransaction != null)
                {
                    await observationTransaction.CommitAsync(CancellationToken.None);
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
                    "Root folder {RootFolderId} diagnostic physical identity could not be refreshed safely.",
                    root.Id);
            }
        }
    }

}
