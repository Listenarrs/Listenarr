using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed class RootFolderStorageConfirmationService(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    IFileSystemSemanticsResolver semanticsResolver,
    IMoveQueueService moveQueueService,
    IFilesystemMutationCoordinator mutationCoordinator,
    IAudiobookOperationCoordinator audiobookOperationCoordinator)
    : IRootFolderStorageConfirmationService
{
    internal Action? BeforeCommitForTest { get; set; }

    internal Action? AfterCommitForTest { get; set; }

    public Task<RootFolder> ConfirmCurrentFolderAsync(
        int rootFolderId,
        string expectedCurrentPath,
        string confirmationToken,
        CancellationToken cancellationToken = default) =>
        mutationCoordinator.ExecuteExclusiveAsync(
            token => ConfirmUnderGlobalLockAsync(
                rootFolderId,
                expectedCurrentPath,
                confirmationToken,
                token),
            cancellationToken);

    private async Task<RootFolder> ConfirmUnderGlobalLockAsync(
        int rootFolderId,
        string expectedCurrentPath,
        string confirmationToken,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCurrentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationToken);

        await using var discovery =
            await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var audiobookIds = await discovery.Audiobooks
            .AsNoTracking()
            .Select(audiobook => audiobook.Id)
            .ToListAsync(cancellationToken);

        return await audiobookOperationCoordinator.ExecuteExclusiveAsync(
            audiobookIds,
            lockedToken => ConfirmUnderAudiobookLocksAsync(
                rootFolderId,
                expectedCurrentPath,
                confirmationToken,
                lockedToken),
            cancellationToken);
    }

    private async Task<RootFolder> ConfirmUnderAudiobookLocksAsync(
        int rootFolderId,
        string expectedCurrentPath,
        string confirmationToken,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var root = await db.RootFolders.SingleOrDefaultAsync(
            candidate => candidate.Id == rootFolderId,
            cancellationToken)
            ?? throw new KeyNotFoundException("Root folder not found");

        if (await db.RootFolderRelocations.AnyAsync(
                relocation => relocation.ActiveRootFolderId == rootFolderId,
                cancellationToken))
        {
            throw new InvalidOperationException(
                "The root folder cannot be confirmed while a path change is active.");
        }

        if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                root.Path,
                out var canonicalRootPath,
                out var pathReason))
        {
            throw new InvalidOperationException(pathReason);
        }

        var semantics = await semanticsResolver.ResolveAsync(
            canonicalRootPath,
            root.CaseSensitivityMode,
            cancellationToken);
        if (semantics.State != PathIdentityState.Valid)
        {
            throw new InvalidOperationException(
                semantics.Reason
                    ?? "The root folder filesystem semantics could not be resolved.");
        }

        var hasAuthorizedIdentity = root.DirectoryObjectIdentityVersion.HasValue
            && !string.IsNullOrWhiteSpace(root.DirectoryObjectIdentity);
        var persistedSemantics = RootFolderPathSemantics.ResolvePersisted(root);
        var canBootstrapSemantics = !hasAuthorizedIdentity
            && (persistedSemantics == null
                || persistedSemantics.Value.DetectAmbiguousCaseMatches);
        if (!canBootstrapSemantics
            && (persistedSemantics == null
                || persistedSemantics.Value.DetectAmbiguousCaseMatches
                || persistedSemantics.Value.Semantics.Syntax != semantics.Semantics.Syntax
                || persistedSemantics.Value.Semantics.CaseSensitivity
                    != semantics.Semantics.CaseSensitivity))
        {
            throw new InvalidOperationException(
                "The root folder filesystem semantics changed or are incomplete; use the root path-change workflow to confirm the storage location and path rules together.");
        }

        var normalizedExpectedPath = FileUtils.NormalizeRootFolderPathForStorage(
            expectedCurrentPath);
        if (!FileSystemPathIdentity.AreEquivalent(
                root.Path,
                normalizedExpectedPath,
                semantics.Semantics))
        {
            throw new InvalidOperationException(
                "The root folder path changed before the folder could be confirmed.");
        }

        var blockingJobs = await moveQueueService.GetFilesystemBlockingJobsAsync(
            cancellationToken);
        if (blockingJobs.Any(job =>
                MoveJobBoundaryConflict.TouchesBoundary(
                    job,
                    root.Path,
                    semantics.Semantics)))
        {
            throw new InvalidOperationException(
                "Resolve active or recoverable moves touching this root before confirming its storage folder.");
        }

        using var pinned = PinnedDirectoryCreation.OpenPinnedBoundary(canonicalRootPath);
        var observedIdentity = CreateObservedIdentity(pinned);
        if (!pinned.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The root folder changed while its physical directory was being confirmed.");
        }

        var expectedToken = RootFolderStorageHealthResolver.CreateConfirmationToken(
            root,
            canonicalRootPath,
            observedIdentity);
        if (!string.Equals(
                expectedToken,
                confirmationToken,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The root folder changed after it was displayed for confirmation. Refresh and review the current folder before confirming it.");
        }

        var replacesAuthorizedGeneration = hasAuthorizedIdentity
            && (root.DirectoryObjectIdentityVersion != observedIdentity.Version
                || !string.Equals(
                    root.DirectoryObjectIdentity,
                    observedIdentity.Value,
                    StringComparison.Ordinal));
        var committed = false;
        try
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(cancellationToken)
                : null;

            if (replacesAuthorizedGeneration)
            {
                await RetireSupersededOwnershipAuthorityAsync(
                    db,
                    rootFolderId,
                    cancellationToken);
            }

            root.DirectoryObjectIdentityVersion = observedIdentity.Version;
            root.DirectoryObjectIdentity = observedIdentity.Value;
            root.DirectoryObjectIdentityUnavailableReason = null;
            root.ResolvedCaseSensitivity = semantics.Semantics.CaseSensitivity;
            root.PathIdentityState = PathIdentityState.Valid;
            root.PathIdentityKey = FileSystemPathIdentity.CreateKey(
                "root",
                root.Path,
                semantics.Semantics);
            root.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            BeforeCommitForTest?.Invoke();
            RevalidatePinnedGeneration(pinned, observedIdentity, cancellationToken);
            await RevalidateFilesystemSemanticsAsync(
                canonicalRootPath,
                root.CaseSensitivityMode,
                semantics.Semantics,
                cancellationToken);

            var completionToken =
                RequestCancellationBoundary.EnterNonCancelablePhase(cancellationToken);
            if (transaction != null)
            {
                await transaction.CommitAsync(completionToken);
            }
            committed = true;

            AfterCommitForTest?.Invoke();
            RevalidatePinnedGeneration(
                pinned,
                observedIdentity,
                CancellationToken.None);
            await RevalidateFilesystemSemanticsAsync(
                canonicalRootPath,
                root.CaseSensitivityMode,
                semantics.Semantics,
                CancellationToken.None);
            return root;
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException))
        {
            if (committed)
            {
                await MarkPostCommitConfirmationUnstableAsync(
                    rootFolderId,
                    observedIdentity,
                    exception);
            }

            throw;
        }
    }

    private static DirectoryObjectIdentityResolution CreateObservedIdentity(
        PinnedDirectoryCreation.PinnedDirectoryAnchor pinned) =>
        new(
            ManagedDirectoryIdentity.CurrentVersion,
            ManagedDirectoryIdentity.CreateMarkerless(
                pinned.GetDirectoryObjectIdentity()),
            null);

    private static async Task RetireSupersededOwnershipAuthorityAsync(
        ListenArrDbContext db,
        int rootFolderId,
        CancellationToken cancellationToken)
    {
        if (await db.LibraryDirectoryOwnershipPathMigrations.AnyAsync(
                migration => migration.Ownership.ManagedRootFolderId == rootFolderId
                    && migration.Ownership.State != LibraryDirectoryOwnershipState.Removed,
                cancellationToken))
        {
            throw new InvalidOperationException(
                "The root folder cannot be confirmed while directory ownership path migration recovery is incomplete.");
        }

        var ownerships = await db.LibraryDirectoryOwnerships
            .Where(ownership => ownership.ManagedRootFolderId == rootFolderId
                && ownership.State != LibraryDirectoryOwnershipState.Removed)
            .ToListAsync(cancellationToken);
        if (ownerships.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var ownership in ownerships)
        {
            ownership.State = LibraryDirectoryOwnershipState.Removed;
            ownership.PathOwnershipKey = null;
            ownership.ManagedRootFolderId = null;
            ownership.DirectoryObjectIdentityUnavailableReason = null;
            ownership.StateReason =
                "Retired because the managed root was explicitly confirmed as a different physical directory generation.";
            ownership.UpdatedAt = now;
        }
    }

    private async Task RevalidateFilesystemSemanticsAsync(
        string canonicalRootPath,
        FileSystemCaseSensitivityMode requestedMode,
        FileSystemPathSemantics expectedSemantics,
        CancellationToken cancellationToken)
    {
        var current = await semanticsResolver.ResolveAsync(
            canonicalRootPath,
            requestedMode,
            cancellationToken);
        if (current.State != PathIdentityState.Valid
            || current.Semantics.Syntax != expectedSemantics.Syntax
            || current.Semantics.CaseSensitivity != expectedSemantics.CaseSensitivity)
        {
            throw new InvalidOperationException(
                "The root folder filesystem semantics changed while confirmation was being committed.");
        }
    }

    private static void RevalidatePinnedGeneration(
        PinnedDirectoryCreation.PinnedDirectoryAnchor pinned,
        DirectoryObjectIdentityResolution expectedIdentity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ManagedDirectoryIdentity.MatchesNativeIdentity(
                expectedIdentity.Version,
                expectedIdentity.Value,
                pinned.GetDirectoryObjectIdentity())
            || !pinned.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The root folder changed while its physical directory confirmation was being committed.");
        }
    }

    private async Task MarkPostCommitConfirmationUnstableAsync(
        int rootFolderId,
        DirectoryObjectIdentityResolution committedIdentity,
        Exception exception)
    {
        await using var repair =
            await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        var persisted = await repair.RootFolders.SingleOrDefaultAsync(
            candidate => candidate.Id == rootFolderId,
            CancellationToken.None);
        if (persisted == null
            || persisted.DirectoryObjectIdentityVersion != committedIdentity.Version
            || !string.Equals(
                persisted.DirectoryObjectIdentity,
                committedIdentity.Value,
                StringComparison.Ordinal))
        {
            return;
        }

        persisted.DirectoryObjectIdentityUnavailableReason =
            "The confirmed storage folder changed immediately after authorization; refresh the root folder state before performing filesystem operations.";
        persisted.UpdatedAt = DateTime.UtcNow;
        await repair.SaveChangesAsync(CancellationToken.None);
    }
}
