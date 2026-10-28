using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed class RootFolderStorageConfirmationService(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    IFileSystemSemanticsResolver semanticsResolver,
    IMoveQueueService moveQueueService,
    IFilesystemMutationCoordinator mutationCoordinator,
    IAudiobookOperationCoordinator audiobookOperationCoordinator,
    IFileRegistrationRecoveryProbe fileRegistrationRecoveryProbe)
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

        if (await db.LibraryDirectoryOwnershipPathMigrations
                .AsNoTracking()
                .AnyAsync(
                    migration =>
                        migration.Relocation.RootFolderId == rootFolderId
                        || migration.Relocation.ActiveRootFolderId
                            == rootFolderId,
                    cancellationToken))
        {
            throw new InvalidOperationException(
                "The root folder cannot be confirmed while ownership path migration recovery is incomplete.");
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

        var persistedSemantics = RootFolderPathSemantics.ResolvePersisted(root);
        var canBootstrapSemantics = persistedSemantics == null
            || persistedSemantics.Value.DetectAmbiguousCaseMatches;
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

        await EnsureNoExternalRecoveryOwnerTouchesRootAsync(
            db,
            rootFolderId,
            canonicalRootPath,
            semantics.Semantics,
            cancellationToken);

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
        if (!pinned.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The root folder path changed while its filesystem settings were being confirmed.");
        }

        var expectedToken = RootFolderStorageHealthResolver.CreateConfirmationToken(
            root,
            canonicalRootPath,
            semantics.Semantics);
        if (!string.Equals(
                expectedToken,
                confirmationToken,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The root folder changed after it was displayed for confirmation. Refresh and review the current folder before confirming it.");
        }

        var committed = false;
        try
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(cancellationToken)
                : null;

            // Confirmation authorizes the configured path/case contract only.
            // Legacy physical-directory observations are deliberately preserved
            // as diagnostics and are not enrolled, replaced, or retired here.
            root.ResolvedCaseSensitivity = semantics.Semantics.CaseSensitivity;
            root.PathIdentityState = PathIdentityState.Valid;
            root.PathIdentityKey = FileSystemPathIdentity.CreateKey(
                "root",
                root.Path,
                semantics.Semantics);
            root.StorageContractRevision = checked(root.StorageContractRevision + 1);
            root.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            BeforeCommitForTest?.Invoke();
            RevalidatePinnedPath(pinned, cancellationToken);
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
            RevalidatePinnedPath(pinned, CancellationToken.None);
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
                    exception);
            }

            throw;
        }
    }

    private async Task EnsureNoExternalRecoveryOwnerTouchesRootAsync(
        ListenArrDbContext db,
        int rootFolderId,
        string canonicalRootPath,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken)
    {
        var audiobookPaths = await db.Audiobooks
            .AsNoTracking()
            .Select(audiobook => new
            {
                audiobook.Id,
                audiobook.BasePath,
                audiobook.FilePath
            })
            .ToListAsync(cancellationToken);
        var audiobookIds = audiobookPaths
            .Where(audiobook =>
                PathTouchesConfirmedRoot(audiobook.BasePath, canonicalRootPath, semantics)
                || PathTouchesConfirmedRoot(audiobook.FilePath, canonicalRootPath, semantics))
            .Select(audiobook => audiobook.Id)
            .ToHashSet();
        var audiobookFilePaths = await db.AudiobookFiles
            .AsNoTracking()
            .Select(file => new
            {
                file.AudiobookId,
                file.Path
            })
            .ToListAsync(cancellationToken);
        audiobookIds.UnionWith(audiobookFilePaths
            .Where(file => PathTouchesConfirmedRoot(
                file.Path,
                canonicalRootPath,
                semantics))
            .Select(file => file.AudiobookId));
        audiobookIds.UnionWith(await db.LibraryDirectoryOwnerships
            .AsNoTracking()
            .Where(ownership => ownership.ManagedRootFolderId == rootFolderId
                && ownership.AudiobookId != null
                && ownership.State != LibraryDirectoryOwnershipState.Removed)
            .Select(ownership => ownership.AudiobookId!.Value)
            .ToListAsync(cancellationToken));
        var registrationBlocker = (await fileRegistrationRecoveryProbe
                .GetBlockingBoundaryAsync(
                    canonicalRootPath,
                    semantics,
                    cancellationToken))
            .FirstOrDefault();
        if (registrationBlocker != null)
        {
            throw new RootFolderRecoveryBlockedException(registrationBlocker);
        }
        var verifiedRenames = await db.VerifiedFileRenameJournals.AsNoTracking()
            .Where(journal => journal.State != VerifiedFileRenameState.Completed
                && journal.State != VerifiedFileRenameState.CompletedSourceRetained
                && journal.State != VerifiedFileRenameState.RolledBack)
            .ToListAsync(cancellationToken);
        if (verifiedRenames.Any(journal => audiobookIds.Contains(journal.AudiobookId)
                || journal.SourceRootFolderId == rootFolderId
                || journal.DestinationRootFolderId == rootFolderId
                || PathTouchesConfirmedRoot(journal.SourcePath, canonicalRootPath, semantics)
                || PathTouchesConfirmedRoot(journal.DestinationPath, canonicalRootPath, semantics)))
        {
            throw new InvalidOperationException(
                "Resolve interrupted verified organize recovery under this root before confirming its storage folder.");
        }

        var activeMutationJournals = await db.FileMutationJournals
            .AsNoTracking()
            .Where(journal =>
                journal.AudiobookId != null
                    && journal.AudiobookFileId != null
                    && (journal.AudiobookFileId == FileMutationOwner.CompanionFile
                        || journal.AudiobookFileId
                            == FileMutationOwner.RegistrationCompanionFile
                        ? journal.State != FileMutationJournalState.Completed
                            && journal.State
                                != FileMutationJournalState.CompletedSourceRetained
                            && journal.State != FileMutationJournalState.RolledBack
                        : journal.State
                                != FileMutationJournalState.OwnerMetadataReconciled
                            && journal.State != FileMutationJournalState.RolledBack))
            .ToListAsync(cancellationToken);
        if (activeMutationJournals.Any(journal =>
                (journal.AudiobookId.HasValue
                    && audiobookIds.Contains(journal.AudiobookId.Value))
                || PathTouchesConfirmedRoot(journal.SourcePath, canonicalRootPath, semantics)
                || PathTouchesConfirmedRoot(journal.DestinationPath, canonicalRootPath, semantics)))
        {
            throw new InvalidOperationException(
                "Resolve active file import or organize recovery under this root before confirming its storage folder.");
        }

        if (audiobookIds.Count > 0
            && await db.AudiobookDeletionIntents
                .AsNoTracking()
                .AnyAsync(intent => audiobookIds.Contains(intent.AudiobookId)
                    && intent.State != AudiobookDeletionIntentState.Completed,
                    cancellationToken))
        {
            throw new InvalidOperationException(
                "Resolve active audiobook deletion recovery under this root before confirming its storage folder.");
        }
    }

    private static bool PathTouchesConfirmedRoot(
        string? path,
        string canonicalRootPath,
        FileSystemPathSemantics semantics)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return FileSystemPathIdentity.StoredPathMayTouchBoundary(
            path,
            canonicalRootPath,
            semantics);
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

    private static void RevalidatePinnedPath(
        PinnedDirectoryCreation.PinnedDirectoryAnchor pinned,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!pinned.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The root folder path changed while its filesystem settings were being committed.");
        }
    }

    private async Task MarkPostCommitConfirmationUnstableAsync(
        int rootFolderId,
        Exception exception)
    {
        await using var repair =
            await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        var persisted = await repair.RootFolders.SingleOrDefaultAsync(
            candidate => candidate.Id == rootFolderId,
            CancellationToken.None);
        if (persisted == null)
        {
            return;
        }

        persisted.PathIdentityState = PathIdentityState.Unavailable;
        persisted.DirectoryObjectIdentityUnavailableReason =
            "The root path or filesystem semantics changed immediately after confirmation; refresh the root folder state before performing filesystem operations. "
            + exception.Message;
        persisted.UpdatedAt = DateTime.UtcNow;
        await repair.SaveChangesAsync(CancellationToken.None);
    }
}
