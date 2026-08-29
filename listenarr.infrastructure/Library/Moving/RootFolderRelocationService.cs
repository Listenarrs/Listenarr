using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    IFileSystemSemanticsResolver semanticsResolver,
    IHubBroadcaster hubBroadcaster,
    TimeProvider timeProvider,
    IFilesystemMutationCoordinator mutationCoordinator,
    IAudiobookOperationCoordinator audiobookOperationCoordinator,
    IServiceScopeFactory manifestScopeFactory,
    ILibraryFilesystemReadiness filesystemReadiness,
    IDirectoryObjectIdentityResolver? directoryObjectIdentityResolver = null,
    IFileRegistrationRecoveryProbe? fileRegistrationRecoveryProbe = null) : IRootFolderRelocationService
{
    private async Task<StartOutcome> StartCoreAsync(
        int rootFolderId,
        RootFolderPathChangeCommand command,
        CancellationToken cancellationToken)
    {
        await using var manifestScope = _manifestScopeFactory.CreateAsyncScope();
        var moveSourceManifestService = manifestScope.ServiceProvider
            .GetRequiredService<IMoveSourceManifestService>();

        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.DesiredName))
        {
            throw new ArgumentException("Root folder name is required.", nameof(command));
        }

        RejectTargetNavigationSegments(command.TargetPath);
        var targetPath = FileUtils.NormalizeRootFolderPathForStorage(command.TargetPath);
        var targetResolution = await semanticsResolver.ResolveAsync(
            targetPath,
            command.TargetCaseSensitivityMode,
            cancellationToken);
        if (targetResolution.State != PathIdentityState.Valid)
        {
            throw new RootFolderPathChangeRejectedException(
                "root_folder_target_unavailable",
                "Listenarr cannot verify the new root folder path. Make sure the destination is mounted and accessible, or choose an explicit filesystem case-sensitivity setting, then try again.",
                targetResolution.Reason ?? "Target filesystem semantics are unavailable; select an explicit override.");
        }
        EnsureRelocationTargetMutationSemanticsAuthority(
            command.Mode,
            command.TargetCaseSensitivityMode,
            targetResolution);
        EnsureRelocationTargetMutationCapability(command.Mode, targetPath);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var root = await db.RootFolders.SingleOrDefaultAsync(
            candidate => candidate.Id == rootFolderId,
            cancellationToken)
            ?? throw new KeyNotFoundException("Root folder not found");
        ValidateExpectedCurrentPath(command, root);
        EnsureRelocationSourceMutationCapability(command.Mode, root);

        if (await db.RootFolderRelocations.AnyAsync(
            relocation => relocation.ActiveRootFolderId == rootFolderId,
            cancellationToken))
        {
            throw new RootFolderPathChangeRejectedException(
                "root_folder_relocation_active",
                "This root folder already has a path change in progress. Wait for it to finish, or resolve and retry the existing relocation before changing the path again.",
                "The root folder already has an active relocation.");
        }

        FileSystemSemanticsResolution? sourceResolution = null;
        try
        {
            if (FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    root.Path,
                    out var canonicalSourcePath,
                    out _))
            {
                var resolvedSource = await semanticsResolver.ResolveAsync(
                    canonicalSourcePath,
                    root.CaseSensitivityMode,
                    cancellationToken);
                if (resolvedSource.State == PathIdentityState.Valid)
                {
                    sourceResolution = resolvedSource;
                }
            }
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or NotSupportedException or PathTooLongException or
            System.Security.SecurityException)
        {
            sourceResolution = null;
        }

        if (sourceResolution == null && command.Mode != RootFolderRelocationMode.MetadataOnly)
        {
            throw new RootFolderPathChangeRejectedException(
                "root_folder_source_unavailable",
                "Listenarr cannot access or verify the current root folder, so its files cannot be moved safely. Restore access to the current folder, or change the path without moving files to repair the stored location.",
                "The current root folder path is invalid or unavailable; use metadata-only path change to repair it before relocating files.");
        }
        EnsureRelocationSourceMutationSemanticsAuthority(
            command.Mode,
            root.CaseSensitivityMode,
            sourceResolution);

        var sourcePathSemantics = ResolveStartSourcePathSemantics(
            root,
            sourceResolution,
            command.Mode,
            targetResolution.Semantics.Syntax);
        var sourceOperationSemantics = sourcePathSemantics.SourceOperationSemantics;
        if (sourceOperationSemantics.HasValue
            && command.Mode != RootFolderRelocationMode.MetadataOnly
            && sourceOperationSemantics.Value.Syntax == targetResolution.Semantics.Syntax
            && FileSystemPathIdentity.AreEquivalent(
                root.Path,
                targetPath,
                sourceOperationSemantics.Value))
        {
            throw new ArgumentException(
                "Root folder relocation source and target paths must be distinct under the persisted root semantics.",
                nameof(command));
        }

        var targetIdentityKey = await ValidateStartRecoveryBoundariesAsync(
            db,
            rootFolderId,
            root,
            sourcePathSemantics,
            targetPath,
            targetResolution,
            cancellationToken);

        var sourceObjectIdentity =
            await ResolveRelocationSourceObjectIdentityAsync(
                root,
                command,
                cancellationToken);

        var storedSourcePathSemantics = sourcePathSemantics.StoredSourcePathSemantics;
        var metadataSourcePathSemantics = sourcePathSemantics.MetadataSourcePathSemantics;
        var allowContextualAmbiguousMetadataSyntax =
            sourcePathSemantics.AllowContextualAmbiguousMetadataSyntax;
        var sourceCaseSensitivityMode = sourcePathSemantics.SourceCaseSensitivityMode;

        var (affected, invalidStoredBasePaths) = await LoadAffectedAudiobooksAsync(
            db,
            root.Path,
            metadataSourcePathSemantics,
            allowContextualAmbiguousMetadataSyntax,
            cancellationToken);

        if (command.Mode != RootFolderRelocationMode.MetadataOnly && invalidStoredBasePaths.Count > 0)
        {
            throw new RootFolderPathChangeRejectedException(
                "root_folder_metadata_repair_required",
                "One or more audiobooks under this root have invalid stored paths, so Listenarr cannot move them safely. Change the root path without moving files first to repair the stored metadata.",
                "One or more audiobook base paths are invalid; use metadata-only path change to repair stored metadata before relocating files.");
        }

        var affectedAudiobookIds = affected
            .Concat(invalidStoredBasePaths)
            .Select(candidate => candidate.Audiobook.Id)
            .ToHashSet();
        await EnsureNoUnresolvedMoveConflictsAsync(
            db,
            affectedAudiobookIds,
            root.Path,
            sourceOperationSemantics,
            targetPath,
            targetResolution.Semantics,
            cancellationToken);
        var externalRecoveryConflict = await FindExternalRecoveryConflictAsync(
            db,
            affectedAudiobookIds,
            cancellationToken);
        if (externalRecoveryConflict != null)
        {
            throw new RootFolderPathChangeRejectedException(
                externalRecoveryConflict.Code,
                externalRecoveryConflict.PublicMessage,
                externalRecoveryConflict.Detail);
        }

        var movePlans = new List<RelocationMovePlan>();
        if (sourceResolution != null
            && command.Mode == RootFolderRelocationMode.Relocate)
        {
            foreach (var candidate in affected)
            {
                var manifest = await moveSourceManifestService.BuildAsync(
                    candidate.Audiobook,
                    cancellationToken);
                if (!FileSystemPathIdentity.IsSameOrInside(
                        manifest.SourceRoot,
                        root.Path,
                        sourceOperationSemantics!.Value))
                {
                    throw new InvalidOperationException(
                        "A tracked audiobook move source escaped the relocating root folder.");
                }
                if (manifest.SourceIdentity.Syntax != sourceOperationSemantics.Value.Syntax
                    || !FileSystemPathIdentity.IsSameOrInside(
                        manifest.SourceIdentity.BoundaryPath,
                        root.Path,
                        sourceOperationSemantics.Value)
                    || !FileSystemPathIdentity.IsSameOrInside(
                        manifest.SourceIdentity.BoundaryPath,
                        root.Path,
                        manifest.SourceIdentity.Semantics))
                {
                    throw new InvalidOperationException(
                        "A tracked audiobook move source is not authorized by the relocating root folder boundary.");
                }

                var requestedPath = MapTargetPath(
                    root.Path,
                    targetPath,
                    manifest.SourceRoot,
                    sourceOperationSemantics!.Value,
                    targetResolution.Semantics);
                var targetIdentity = PathIdentitySnapshot.FromResolution(
                    targetResolution.Semantics,
                    command.TargetCaseSensitivityMode,
                    targetPath,
                    requestedPath);
                if (FileSystemPathIdentity.AreEquivalentEndpoints(
                        manifest.SourceRoot,
                        manifest.SourceIdentity,
                        requestedPath,
                        targetIdentity))
                {
                    throw new InvalidOperationException(
                        "Root folder relocation produced an identical source and target child move.");
                }

                movePlans.Add(new RelocationMovePlan(
                    candidate,
                    manifest,
                    requestedPath,
                    targetIdentity));
            }

            RejectDuplicateRelocationTargets(movePlans, targetResolution.Semantics);
        }

        // Directory enrollment is a filesystem mutation. Keep it behind every
        // read-only request/root/source/conflict/manifest validation so a rejected
        // path-change request cannot leave Listenarr metadata in an unadopted target.
        var targetObjectIdentity =
            await ResolveOrEnrollDirectoryObjectIdentityAsync(
                targetPath,
                cancellationToken);

        if (command.Mode == RootFolderRelocationMode.Relocate
            && !targetObjectIdentity.IsAvailable
            && targetObjectIdentity.FailureKind
                != DirectoryObjectIdentityFailureKind.Missing)
        {
            throw new RootFolderPathChangeRejectedException(
                "root_folder_target_unavailable",
                "Listenarr cannot verify the new root folder's physical directory identity. Make sure the destination is mounted and accessible, then try again.",
                targetObjectIdentity.UnavailableReason
                    ?? "Target physical directory identity is unavailable.");
        }

        RootFolderRelocation? relocation = null;
        var relocationWasPrecommitted = false;
        var precommittedContinuationCommitted = false;
        PinnedDirectoryCreation.PinnedDirectoryAnchor? relocationCommitTargetLease = null;
        try
        {
            if (command.Mode == RootFolderRelocationMode.Relocate
                && !targetObjectIdentity.IsAvailable)
            {
                var reservationNow = timeProvider.GetUtcNow().UtcDateTime;
                relocation = new RootFolderRelocation
                {
                    RootFolderId = root.Id,
                    ActiveRootFolderId = root.Id,
                    SourcePath = root.Path,
                    SourceCaseSensitivityMode = sourceCaseSensitivityMode,
                    TargetPath = targetPath,
                    TargetIdentityEnrollmentState =
                        TargetIdentityEnrollmentState.Unavailable,
                    TargetDirectoryObjectIdentityUnavailableReason =
                        "Target directory creation reservations are pending.",
                    Mode = command.Mode,
                    Status = RootFolderRelocationStatus.NeedsAttention,
                    DeleteEmptySource = command.DeleteEmptySource,
                    DesiredName = command.DesiredName.Trim(),
                    DesiredIsDefault = command.DesiredIsDefault,
                    TargetCaseSensitivityMode =
                        command.TargetCaseSensitivityMode,
                    TotalJobs = movePlans.Count,
                    Error =
                        "Target reservations were committed before move jobs were published.",
                    CreatedAt = reservationNow,
                    UpdatedAt = reservationNow
                };
                db.RootFolderRelocations.Add(relocation);
                await db.SaveChangesAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                await transaction.CommitAsync(CancellationToken.None);
                relocationWasPrecommitted = true;
                targetObjectIdentity = await ReserveRelocationTargetAsync(
                    relocation.Id,
                    targetPath,
                    cancellationToken);
                relocation.TargetDirectoryObjectIdentityVersion =
                    targetObjectIdentity.Version;
                relocation.TargetDirectoryObjectIdentity =
                    targetObjectIdentity.Value;
                relocation.TargetDirectoryObjectIdentityUnavailableReason =
                    targetObjectIdentity.UnavailableReason;
                relocation.TargetIdentityEnrollmentState =
                    targetObjectIdentity.IsAvailable
                        ? TargetIdentityEnrollmentState.Authorized
                        : TargetIdentityEnrollmentState.Unavailable;
            }
            await using var continuationTransaction = relocationWasPrecommitted
                ? await db.Database.BeginTransactionAsync(cancellationToken)
                : null;

            var now = timeProvider.GetUtcNow();
            var nowUtc = now.UtcDateTime;

            if (command.Mode == RootFolderRelocationMode.MetadataOnly)
            {
                return await StartMetadataOnlyAsync(
                    db,
                    transaction,
                    root,
                    command,
                    targetPath,
                    targetResolution,
                    targetObjectIdentity,
                    targetIdentityKey,
                    sourceCaseSensitivityMode,
                    affected,
                    invalidStoredBasePaths,
                    metadataSourcePathSemantics?.Semantics,
                    storedSourcePathSemantics?.Semantics,
                    rootFolderId,
                    now,
                    cancellationToken);
            }

            relocation ??= new RootFolderRelocation
            {
                RootFolderId = root.Id,
                ActiveRootFolderId = root.Id,
                SourcePath = root.Path,
                SourceCaseSensitivityMode = sourceCaseSensitivityMode,
                TargetPath = targetPath,
                TargetDirectoryObjectIdentityVersion = targetObjectIdentity.Version,
                TargetDirectoryObjectIdentity = targetObjectIdentity.Value,
                TargetDirectoryObjectIdentityUnavailableReason = targetObjectIdentity.UnavailableReason,
                TargetIdentityEnrollmentState = targetObjectIdentity.IsAvailable
                    ? TargetIdentityEnrollmentState.Authorized
                    : TargetIdentityEnrollmentState.Unavailable,
                Mode = command.Mode,
                Status = RootFolderRelocationStatus.Pending,
                DeleteEmptySource = command.DeleteEmptySource,
                DesiredName = command.DesiredName.Trim(),
                DesiredIsDefault = command.DesiredIsDefault,
                TargetCaseSensitivityMode = command.TargetCaseSensitivityMode,
                TotalJobs = movePlans.Count,
                CreatedAt = nowUtc
            };
            relocation.Status = RootFolderRelocationStatus.Pending;
            relocation.Error = null;
            if (!relocationWasPrecommitted)
            {
                db.RootFolderRelocations.Add(relocation);
            }

            foreach (var plan in movePlans)
            {
                var audiobook = plan.Candidate.Audiobook;
                if (sourceObjectIdentity == null
                    || !sourceObjectIdentity.IsAvailable
                    || !targetObjectIdentity.IsAvailable)
                {
                    throw new InvalidOperationException(
                        "Relocation move jobs require durable source- and target-boundary generation authorization.");
                }

                var entries = plan.Manifest.Entries
                    .Select(entry => new MoveJobEntry
                    {
                        RelativePath = entry.RelativePath,
                        EntryType = entry.EntryType,
                        Length = entry.Length,
                        LastWriteTimeUtc = entry.LastWriteTimeUtc,
                        Sha256 = entry.Sha256,
                        CopyState = MoveJobEntryCopyState.Pending,
                        CleanupState = MoveJobEntryCleanupState.Pending
                    })
                    .ToList();
                entries.Add(
                    MoveManifestIdentity.CreateSourceBoundaryAuthorization(
                        sourceObjectIdentity.Version!.Value,
                        sourceObjectIdentity.Value!));
                entries.Add(
                    MoveManifestIdentity.CreateTargetBoundaryAuthorization(
                        targetObjectIdentity.Version!.Value,
                        targetObjectIdentity.Value!));
                var moveJob = new MoveJob
                {
                    AudiobookId = audiobook.Id,
                    RequestedPath = plan.RequestedPath,
                    SourcePath = plan.Manifest.SourceRoot,
                    SourceCleanupBoundary = root.Path,
                    DeleteEmptySource = command.DeleteEmptySource,
                    Status = MoveJobStatus.Queued,
                    Phase = MoveJobPhase.None,
                    EnqueuedAt = nowUtc,
                    RelocationId = relocation.Id,
                    IdentityKeyVersion = MoveManifestIdentity.Version,
                    ExecutionProtocolVersion = MoveExecutionProtocol.Current,
                    ActiveDeduplicationKey = MoveManifestIdentity.CreateDeduplicationKey(
                        audiobook.Id,
                        plan.Manifest.SourceRoot,
                        plan.Manifest.SourceIdentity,
                        plan.RequestedPath,
                        plan.TargetIdentity,
                        entries),
                    Entries = entries
                };
                moveJob.SetSourceIdentity(plan.Manifest.SourceIdentity);
                moveJob.SetTargetIdentity(plan.TargetIdentity);
                db.MoveJobs.Add(moveJob);
            }

            await db.SaveChangesAsync(cancellationToken);
            if (affected.Count == 0)
            {
                relocationCommitTargetLease = await CompleteEmptyRelocationAsync(
                    db,
                    root,
                    relocation,
                    command,
                    targetPath,
                    targetResolution,
                    targetObjectIdentity,
                    targetIdentityKey,
                    rootFolderId,
                    nowUtc,
                    cancellationToken);
            }

            if (relocationCommitTargetLease != null)
            {
                RevalidatePinnedTargetDirectoryGeneration(
                    relocationCommitTargetLease,
                    targetObjectIdentity.Version,
                    targetObjectIdentity.Value,
                    targetObjectIdentity.UnavailableReason,
                    CancellationToken.None);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (continuationTransaction != null)
            {
                await continuationTransaction.CommitAsync(
                    CancellationToken.None);
                precommittedContinuationCommitted = true;
            }
            else
            {
                await transaction.CommitAsync(CancellationToken.None);
            }
            var result = Map(relocation, root.Path);
            return new StartOutcome(result, true);
        }
        catch (Exception exception) when (
            relocationWasPrecommitted
            && !precommittedContinuationCommitted
            && exception is not (
                OutOfMemoryException
                    or StackOverflowException))
        {
            await MarkPrecommittedRelocationNeedsAttentionAsync(
                relocation!.Id,
                exception,
                CancellationToken.None);
            throw;
        }
        finally
        {
            relocationCommitTargetLease?.Dispose();
        }
    }
}
