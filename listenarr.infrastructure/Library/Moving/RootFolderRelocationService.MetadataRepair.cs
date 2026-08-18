using Listenarr.Application.Common.Exceptions;
using Listenarr.Domain.Audiobooks.Enumerations;
using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    public Task<RootFolderMetadataRepairDetails?> GetSkippedMetadataRepairDetailsAsync(
        Guid relocationId,
        int audiobookId,
        CancellationToken cancellationToken = default) =>
        _mutationCoordinator.ExecuteExclusiveAsync(
            token => _audiobookOperationCoordinator.ExecuteExclusiveAsync(
                audiobookId,
                lockedToken => GetSkippedMetadataRepairDetailsCoreAsync(
                    relocationId,
                    audiobookId,
                    lockedToken),
                token),
            cancellationToken);

    public Task<RootFolderMetadataRepairDetails> RemoveSkippedMetadataRepairFileAsync(
        Guid relocationId,
        int audiobookId,
        int audiobookFileId,
        CancellationToken cancellationToken = default) =>
        _mutationCoordinator.ExecuteExclusiveAsync(
            token => _audiobookOperationCoordinator.ExecuteExclusiveAsync(
                audiobookId,
                lockedToken => RemoveSkippedMetadataRepairFileCoreAsync(
                    relocationId,
                    audiobookId,
                    audiobookFileId,
                    lockedToken),
                token),
            cancellationToken);

    private async Task<RootFolderMetadataRepairDetails?>
        GetSkippedMetadataRepairDetailsCoreAsync(
            Guid relocationId,
            int audiobookId,
            CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var state = await LoadSkippedMetadataRepairStateAsync(
            db,
            relocationId,
            audiobookId,
            cancellationToken);
        return state == null
            ? null
            : BuildMetadataRepairDetails(
                db,
                state.Relocation,
                state.Audiobook,
                state.SkippedItem,
                state.SourceSemantics,
                state.TargetSemantics);
    }

    private async Task<RootFolderMetadataRepairDetails>
        RemoveSkippedMetadataRepairFileCoreAsync(
            Guid relocationId,
            int audiobookId,
            int audiobookFileId,
            CancellationToken cancellationToken)
    {
        _filesystemReadiness.EnsureMetadataRepairReady();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var state = await LoadSkippedMetadataRepairStateAsync(
            db,
            relocationId,
            audiobookId,
            cancellationToken)
            ?? throw new KeyNotFoundException(
                "The skipped audiobook metadata repair no longer exists.");
        await EnsureMetadataRepairRowMutationAllowedAsync(
            db,
            audiobookId,
            cancellationToken);
        var details = BuildMetadataRepairDetails(
            db,
            state.Relocation,
            state.Audiobook,
            state.SkippedItem,
            state.SourceSemantics,
            state.TargetSemantics);
        if (!IsRepairableMetadataSkipReason(details.ReasonCode))
        {
            throw new ApplicationConflictException(
                "root_folder_metadata_repair_not_record_conflict",
                "This audiobook is blocked by a different path-repair issue and no tracked file record can be removed from this repair screen.");
        }
        if (!details.CollisionGroups
            .SelectMany(group => group.Files)
            .Any(file =>
                file.AudiobookFileId == audiobookFileId
                && file.CanRemove
                && file.AudiobookId == audiobookId))
        {
            throw new ApplicationConflictException(
                "root_folder_metadata_repair_file_not_colliding",
                "That tracked file record no longer participates in a destination path collision. Refresh the repair details and try again.");
        }

        var trackedFile = state.Audiobook.Files?.SingleOrDefault(
            file => file.Id == audiobookFileId)
            ?? throw new KeyNotFoundException("Audiobook file record not found");
        if (StoredFileReferenceMatchesTrackedPath(
                state.Audiobook.FilePath,
                trackedFile.Path,
                state.SourceSemantics))
        {
            state.Audiobook.FilePath = null;
        }
        db.AudiobookFiles.Remove(trackedFile);
        state.Audiobook.Files?.Remove(trackedFile);
        var result = BuildMetadataRepairDetails(
            db,
            state.Relocation,
            state.Audiobook,
            state.SkippedItem,
            state.SourceSemantics,
            state.TargetSemantics);
        await db.SaveChangesAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
        return result;
    }

    private async Task EnsureMetadataRepairRowMutationAllowedAsync(
        ListenArrDbContext db,
        int audiobookId,
        CancellationToken cancellationToken)
    {
        var moveJobs = await db.MoveJobs
            .AsNoTracking()
            .Where(job => job.AudiobookId == audiobookId)
            .ToListAsync(cancellationToken);
        if (moveJobs.Any(MoveRecoveryPolicy.BlocksFilesystemMutation))
        {
            throw new ApplicationConflictException(
                "move_recovery_required",
                "An unresolved audiobook move owns this audiobook's path state. Resolve that move before repairing tracked file records.");
        }

        if (await db.FileMutationJournals
            .AsNoTracking()
            .AnyAsync(
                journal => journal.AudiobookId == audiobookId
                    && journal.AudiobookFileId == null
                    && journal.Action == FileAction.Move
                    && journal.State != FileMutationJournalState.Completed,
                cancellationToken))
        {
            throw new ApplicationConflictException(
                "registration_recovery_pending",
                "A committed file import still owns source-cleanup state for this audiobook. Complete that recovery before repairing tracked file records.");
        }

        if (await db.FileMutationJournals
            .AsNoTracking()
            .AnyAsync(
                journal => journal.AudiobookId == audiobookId
                    && journal.AudiobookFileId != null
                    && (journal.AudiobookFileId == FileMutationOwner.CompanionFile
                        ? journal.State != FileMutationJournalState.Completed
                        : journal.State != FileMutationJournalState.OwnerMetadataReconciled),
                cancellationToken))
        {
            throw new ApplicationConflictException(
                "rename_recovery_pending",
                "An unresolved file organize operation owns this audiobook's path state. Complete restart recovery before repairing tracked file records.");
        }

        if (await db.AudiobookDeletionIntents
            .AsNoTracking()
            .AnyAsync(
                intent => intent.AudiobookId == audiobookId
                    && intent.State != AudiobookDeletionIntentState.Completed,
                cancellationToken))
        {
            throw new ApplicationConflictException(
                "delete_recovery_pending",
                "An audiobook deletion owns this audiobook's path state. Complete that recovery before repairing tracked file records.");
        }
    }

    private async Task<SkippedMetadataRepairState?> LoadSkippedMetadataRepairStateAsync(
        ListenArrDbContext db,
        Guid relocationId,
        int audiobookId,
        CancellationToken cancellationToken)
    {
        var relocation = await db.RootFolderRelocations
            .Include(candidate => candidate.SkippedItems)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == relocationId,
                cancellationToken);
        if (relocation == null)
        {
            return null;
        }
        if (relocation.Mode != RootFolderRelocationMode.MetadataOnly
            || relocation.Status != RootFolderRelocationStatus.NeedsAttention
            || relocation.ActiveRootFolderId == null)
        {
            throw new ApplicationConflictException(
                "root_folder_metadata_repair_inactive",
                "This root-folder path repair is not waiting for audiobook metadata repair.");
        }

        var skippedItem = relocation.SkippedItems.SingleOrDefault(
            item => item.AudiobookId == audiobookId);
        if (skippedItem == null)
        {
            return null;
        }
        var audiobook = await db.Audiobooks
            .Include(candidate => candidate.Files)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == audiobookId,
                cancellationToken);
        if (audiobook == null)
        {
            return null;
        }
        await db.AudiobookFiles.LoadAsync(cancellationToken);
        if (relocation.RootFolderId is not int rootFolderId)
        {
            throw new InvalidOperationException(
                "The metadata repair no longer references a root folder.");
        }
        var root = await db.RootFolders
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == rootFolderId,
                cancellationToken)
            ?? throw new InvalidOperationException(
                "The metadata repair root folder no longer exists.");
        var targetSemantics = RootFolderPathSemantics.ResolvePersisted(root)?.Semantics
            ?? throw new InvalidOperationException(
                "The repaired root folder no longer has authoritative target path semantics.");
        if (!TryResolvePersistedRelocationSourceSemantics(
                relocation,
                out var sourceSemantics,
                out var sourceReason))
        {
            throw new InvalidOperationException(sourceReason);
        }

        return new SkippedMetadataRepairState(
            relocation,
            audiobook,
            skippedItem,
            sourceSemantics,
            targetSemantics);
    }

    private static RootFolderMetadataRepairDetails BuildMetadataRepairDetails(
        ListenArrDbContext db,
        RootFolderRelocation relocation,
        Audiobook audiobook,
        RootFolderRelocationSkippedItem skippedItem,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics)
    {
        var reasonCode = ClassifyMetadataSkipReason(skippedItem.Reason);
        var collisionGroups = Array.Empty<RootFolderMetadataRepairCollisionGroup>();
        if (!string.IsNullOrWhiteSpace(audiobook.BasePath))
        {
            var snapshot = CaptureMetadataRewriteSnapshot(audiobook);
            try
            {
                var destination = MapTargetPath(
                    relocation.SourcePath,
                    relocation.TargetPath,
                    audiobook.BasePath,
                    sourceSemantics,
                    targetSemantics);
                AudiobookPathReferenceRewriter.Rewrite(
                    audiobook,
                    audiobook.BasePath,
                    destination,
                    sourceSemantics,
                    targetSemantics,
                    relocation.TargetCaseSensitivityMode);
                var candidateFileIds = (audiobook.Files ?? [])
                    .Select(file => file.Id)
                    .ToHashSet();
                collisionGroups = db.ChangeTracker
                    .Entries<AudiobookFile>()
                    .Where(entry => entry.State != EntityState.Deleted)
                    .Select(entry => entry.Entity)
                    .Where(file =>
                        file.PathIdentityState == PathIdentityState.Valid
                        && !string.IsNullOrWhiteSpace(file.PathOwnershipKey))
                    .GroupBy(file => file.PathOwnershipKey!, StringComparer.Ordinal)
                    .Where(group =>
                        group.Select(file => file.Id).Distinct().Count() > 1
                        && group.Any(file => candidateFileIds.Contains(file.Id)))
                    .Select(group => new RootFolderMetadataRepairCollisionGroup(
                        GetTargetRelativeDisplayPath(
                            group.First().CanonicalPath,
                            relocation.TargetPath,
                            targetSemantics),
                        group.OrderBy(file => file.AudiobookId)
                            .ThenBy(file => file.Id)
                            .Select(file => new RootFolderMetadataRepairCollisionFile(
                                file.Id,
                                file.AudiobookId,
                                GetTargetRelativeDisplayPath(
                                    file.CanonicalPath ?? file.Path,
                                    relocation.TargetPath,
                                    targetSemantics),
                                file.AudiobookId == audiobook.Id))
                            .ToArray()))
                    .OrderBy(group => group.TargetRelativePath, StringComparer.Ordinal)
                    .ToArray();
                if (collisionGroups.Length > 0)
                {
                    reasonCode = RootFolderRelocationSkipReasonCode.TargetIdentityCollision;
                }
                else
                {
                    var allTrackedFiles = db.ChangeTracker
                        .Entries<AudiobookFile>()
                        .Where(entry => entry.State != EntityState.Deleted)
                        .Select(entry => entry.Entity)
                        .ToArray();
                    var unresolvedByLookupKey = allTrackedFiles
                        .Where(file =>
                            file.PathIdentityState != PathIdentityState.Valid
                            && !string.IsNullOrWhiteSpace(file.PathIdentityLookupKey))
                        .GroupBy(file => file.PathIdentityLookupKey!, StringComparer.Ordinal)
                        .ToDictionary(
                            group => group.Key,
                            group => group.ToArray(),
                            StringComparer.Ordinal);
                    var unresolvedGroups = new List<RootFolderMetadataRepairCollisionGroup>();
                    foreach (var candidateFile in (audiobook.Files ?? []).Where(file =>
                        file.PathIdentityState == PathIdentityState.Valid
                        && !string.IsNullOrWhiteSpace(file.PathIdentityLookupKey)
                        && !string.IsNullOrWhiteSpace(file.CanonicalPath)))
                    {
                        if (!unresolvedByLookupKey.TryGetValue(
                                candidateFile.PathIdentityLookupKey!,
                                out var unresolvedCandidates))
                        {
                            continue;
                        }
                        var overlaps = unresolvedCandidates
                            .Where(unresolved =>
                                unresolved.Id != candidateFile.Id
                                && AudiobookFileOwnershipValidator.UnresolvedIdentityOverlaps(
                                    unresolved,
                                    candidateFile.PathSyntax!.Value,
                                    candidateFile.PathCaseSensitivity,
                                    candidateFile.CanonicalPath!))
                            .OrderBy(unresolved => unresolved.AudiobookId)
                            .ThenBy(unresolved => unresolved.Id)
                            .ToArray();
                        if (overlaps.Length == 0)
                        {
                            continue;
                        }

                        unresolvedGroups.Add(new RootFolderMetadataRepairCollisionGroup(
                            GetTargetRelativeDisplayPath(
                                candidateFile.CanonicalPath,
                                relocation.TargetPath,
                                targetSemantics),
                            new[] { candidateFile }
                                .Concat(overlaps)
                                .DistinctBy(file => file.Id)
                                .Select(file => new RootFolderMetadataRepairCollisionFile(
                                    file.Id,
                                    file.AudiobookId,
                                    GetTargetRelativeDisplayPath(
                                        file.CanonicalPath ?? file.Path,
                                        relocation.TargetPath,
                                        targetSemantics),
                                    file.AudiobookId == audiobook.Id))
                                .ToArray()));
                    }
                    collisionGroups = unresolvedGroups
                        .OrderBy(group => group.TargetRelativePath, StringComparer.Ordinal)
                        .ToArray();
                    if (collisionGroups.Length > 0)
                    {
                        reasonCode =
                            RootFolderRelocationSkipReasonCode.TargetIdentityUnresolvedConflict;
                    }
                }
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException)
            {
                collisionGroups = [];
            }
            finally
            {
                RestoreMetadataRewriteSnapshot(snapshot);
            }
        }

        return new RootFolderMetadataRepairDetails(
            relocation.Id,
            audiobook.Id,
            audiobook.Title ?? $"Audiobook {audiobook.Id}",
            reasonCode,
            collisionGroups);
    }

    private static bool StoredFileReferenceMatchesTrackedPath(
        string? storedReference,
        string? trackedPath,
        FileSystemPathSemantics semantics)
    {
        if (string.IsNullOrWhiteSpace(storedReference)
            || string.IsNullOrWhiteSpace(trackedPath))
        {
            return false;
        }
        if (string.Equals(storedReference, trackedPath, StringComparison.Ordinal))
        {
            return true;
        }
        if (!FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                storedReference,
                semantics.Syntax,
                out var referenceSyntax)
            || !FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                trackedPath,
                semantics.Syntax,
                out var trackedSyntax)
            || referenceSyntax != semantics.Syntax
            || trackedSyntax != semantics.Syntax)
        {
            return false;
        }

        try
        {
            return FileSystemPathIdentity.AreEquivalent(
                FileSystemPathIdentity.Canonicalize(storedReference, semantics.Syntax),
                FileSystemPathIdentity.Canonicalize(trackedPath, semantics.Syntax),
                semantics);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string GetTargetRelativeDisplayPath(
        string? storedPath,
        string targetRoot,
        FileSystemPathSemantics targetSemantics)
    {
        if (!string.IsNullOrWhiteSpace(storedPath)
            && FileSystemPathIdentity.TryGetRelativePathWithinBase(
                targetRoot,
                storedPath,
                targetSemantics,
                out var relativePath))
        {
            return relativePath.Length == 0 ? "." : relativePath;
        }

        return "Tracked file";
    }

    private sealed record SkippedMetadataRepairState(
        RootFolderRelocation Relocation,
        Audiobook Audiobook,
        RootFolderRelocationSkippedItem SkippedItem,
        FileSystemPathSemantics SourceSemantics,
        FileSystemPathSemantics TargetSemantics);
}
