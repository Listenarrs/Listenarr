using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    public async Task OnMoveJobStateChangedAsync(
        Guid moveJobId,
        CancellationToken cancellationToken = default)
    {
        await using (var preflightDb = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var shouldReconcile = await preflightDb.MoveJobs
                .AsNoTracking()
                .AnyAsync(
                    candidate => candidate.Id == moveJobId
                        && candidate.RelocationId != null
                        && (candidate.Status == MoveJobStatus.Completed
                            || candidate.Status == MoveJobStatus.NeedsAttention
                            || candidate.Status == MoveJobStatus.Failed
                            || candidate.Status == MoveJobStatus.Superseded),
                    cancellationToken);
            if (!shouldReconcile)
            {
                return;
            }
        }

        var result = await _mutationCoordinator.ExecuteExclusiveAsync(
            token => ExecuteWithAllAudiobookLocksAsync(
                lockedToken => OnMoveJobStateChangedCoreAsync(moveJobId, lockedToken),
                token),
            cancellationToken);
        if (result != null)
        {
            await BroadcastAsync(result, cancellationToken);
        }
    }

    private async Task<RootFolderPathChangeResult?> OnMoveJobStateChangedCoreAsync(
        Guid moveJobId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var job = await db.MoveJobs.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == moveJobId,
            cancellationToken);
        if (job?.RelocationId == null)
        {
            return null;
        }

        var relocation = await db.RootFolderRelocations
            .Include(candidate => candidate.MoveJobs)
            .SingleAsync(candidate => candidate.Id == job.RelocationId, cancellationToken);
        var root = relocation.RootFolderId is int rootFolderId
            ? await db.RootFolders.SingleOrDefaultAsync(
                candidate => candidate.Id == rootFolderId,
                cancellationToken)
            : null;
        relocation.CompletedJobs = relocation.MoveJobs.Count(candidate => candidate.Status == MoveJobStatus.Completed);
        relocation.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        PinnedDirectoryCreation.PinnedDirectoryAnchor? finalizationTargetLease = null;
        try
        {
            if (relocation.MoveJobs.Any(candidate => candidate.Status is
                MoveJobStatus.NeedsAttention or MoveJobStatus.Failed or MoveJobStatus.Superseded))
            {
                relocation.Status = RootFolderRelocationStatus.NeedsAttention;
                relocation.Error = relocation.MoveJobs
                    .First(candidate => candidate.Status is
                        MoveJobStatus.NeedsAttention or MoveJobStatus.Failed or MoveJobStatus.Superseded)
                    .Error
                    ?? "A relocation move job was superseded during queue reconciliation.";
            }
            else if (relocation.MoveJobs.All(candidate => candidate.Status == MoveJobStatus.Completed))
            {
                if (root == null)
                {
                    relocation.Status = RootFolderRelocationStatus.NeedsAttention;
                    relocation.ActiveRootFolderId = null;
                    relocation.Error = "The root folder no longer exists; relocation finalization requires manual review.";
                }
                else
                {
                    var registrationBoundaryConflict =
                        await FindRegistrationBoundaryRecoveryConflictAsync(
                            relocation.SourcePath,
                            relocation.SourceCaseSensitivityMode,
                            relocation.TargetPath,
                            relocation.TargetCaseSensitivityMode,
                            cancellationToken);
                    if (registrationBoundaryConflict != null)
                    {
                        relocation.Status = RootFolderRelocationStatus.NeedsAttention;
                        relocation.Error = registrationBoundaryConflict.PublicMessage;
                    }
                    else
                    {
                        finalizationTargetLease = await FinalizeCompletedRelocationAsync(
                            db,
                            relocation,
                            root,
                            relocation.UpdatedAt ?? timeProvider.GetUtcNow().UtcDateTime,
                            cancellationToken);
                    }
                }
            }
            else
            {
                relocation.Status = RootFolderRelocationStatus.Running;
            }

            await db.SaveChangesAsync(cancellationToken);
            BeforeCompletedRelocationAtomicCommitForTest?.Invoke(relocation.Id);
            if (finalizationTargetLease != null)
            {
                RevalidatePinnedTargetDirectoryGeneration(
                    finalizationTargetLease,
                    relocation,
                    CancellationToken.None);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None);
            return Map(relocation, root?.Path ?? ResolveCurrentPathFallback(relocation));
        }
        finally
        {
            finalizationTargetLease?.Dispose();
        }
    }

    public async Task ReconcileActiveAsync(CancellationToken cancellationToken = default)
    {
        var results = await _mutationCoordinator.ExecuteExclusiveAsync(
            token => ExecuteWithAllAudiobookLocksAsync(
                ReconcileActiveCoreAsync,
                token),
            cancellationToken);
        foreach (var result in results)
        {
            await BroadcastAsync(result, cancellationToken);
        }
    }

    private async Task<List<RootFolderPathChangeResult>> ReconcileActiveCoreAsync(
        CancellationToken cancellationToken)
    {
        var results =
            await ReconcileOwnershipPathMigrationsAsync(cancellationToken);
        results.AddRange(
            await ReconcileCommittedMetadataOnlyRelocationsAsync(
                cancellationToken));
        var failedMetadataRecovery = results.FirstOrDefault(result =>
            result.Mode == RootFolderRelocationMode.MetadataOnly
            && result.Status == RootFolderRelocationStatus.Failed);
        if (failedMetadataRecovery != null)
        {
            throw new InvalidOperationException(
                $"Metadata-only root repair recovery {failedMetadataRecovery.RelocationId} remains failed.");
        }

        await ReconcileRootIdentitiesAsync(cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var recoverableReservationIds = await db.RootFolderRelocations
            .Where(relocation =>
                relocation.Status ==
                    RootFolderRelocationStatus.NeedsAttention
                && relocation.TargetIdentityEnrollmentState ==
                    TargetIdentityEnrollmentState.Unavailable)
            .Select(relocation => relocation.Id)
            .ToListAsync(cancellationToken);
        foreach (var relocationId in recoverableReservationIds)
        {
            await TryRecoverRelocationTargetReservationEnrollmentAsync(
                relocationId,
                cancellationToken);
        }

        var abandonedReservationIds = await db.RootFolderRelocations
            .Where(relocation =>
                relocation.Status == RootFolderRelocationStatus.Failed)
            .Where(relocation =>
                relocation.CreatedDirectories.Any(reservation =>
                    reservation.State ==
                        RootFolderRelocationCreatedDirectoryState.Planned
                    || reservation.State ==
                        RootFolderRelocationCreatedDirectoryState.Created))
            .Select(relocation => relocation.Id)
            .ToListAsync(cancellationToken);
        foreach (var relocationId in abandonedReservationIds)
        {
            try
            {
                await ReconcileRelocationTargetReservationsAsync(
                    relocationId,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not (
                OperationCanceledException
                    or OutOfMemoryException
                    or StackOverflowException))
            {
                await PersistFailedReservationCleanupAttentionAsync(
                    relocationId,
                    exception,
                    CancellationToken.None);
            }
        }

        var activeRelocationIds = await db.RootFolderRelocations
            .Where(relocation => relocation.ActiveRootFolderId != null)
            .Select(relocation => relocation.Id)
            .ToListAsync(cancellationToken);
        var terminalJobs = await db.MoveJobs
            .Where(job => job.RelocationId != null
                && activeRelocationIds.Contains(job.RelocationId.Value))
            .Where(job => job.Status == MoveJobStatus.Completed
                || job.Status == MoveJobStatus.NeedsAttention
                || job.Status == MoveJobStatus.Failed
                || job.Status == MoveJobStatus.Superseded)
            .OrderByDescending(job => job.UpdatedAt)
            .ToListAsync(cancellationToken);
        var terminalJobIds = terminalJobs
            .GroupBy(job => job.RelocationId)
            .Select(group => group.First().Id);
        foreach (var jobId in terminalJobIds)
        {
            var result = await OnMoveJobStateChangedCoreAsync(jobId, cancellationToken);
            if (result != null)
            {
                results.Add(result);
            }
        }

        return results;
    }

    private async Task ReconcileRootIdentitiesAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _rootIdentitiesReconciled)) return;
        await _rootIdentityGate.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _rootIdentitiesReconciled)) return;
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var roots = await db.RootFolders.ToListAsync(cancellationToken);
            foreach (var root in roots) root.PathIdentityKey = null;
            await db.SaveChangesAsync(cancellationToken);

            var resolvedRoots = new List<(RootFolder Root, string Key)>();
            foreach (var root in roots)
            {
                try
                {
                    var persisted = RootFolderPathSemantics.ResolvePersisted(root);
                    if (!persisted.HasValue
                        || persisted.Value.DetectAmbiguousCaseMatches
                        || persisted.Value.Semantics.CaseSensitivity
                            == FileSystemCaseSensitivity.Unknown)
                    {
                        root.ResolvedCaseSensitivity =
                            FileSystemCaseSensitivity.Unknown;
                        root.PathIdentityState = PathIdentityState.Unavailable;
                        root.PathIdentityKey = null;
                        continue;
                    }

                    var canonicalRootPath = FileSystemPathIdentity.Canonicalize(
                        root.Path,
                        persisted.Value.Semantics.Syntax);
                    root.ResolvedCaseSensitivity =
                        persisted.Value.Semantics.CaseSensitivity;
                    resolvedRoots.Add((
                        root,
                        FileSystemPathIdentity.CreateKey(
                            "root",
                            canonicalRootPath,
                            persisted.Value.Semantics)));
                }
                catch (Exception exception) when (exception is
                    ArgumentException or InvalidOperationException
                        or NotSupportedException or PathTooLongException)
                {
                    // Persisted root semantics are normalized without probing the live
                    // filesystem. Invalid or ambiguous stored roots remain unavailable
                    // until an explicit path change or confirmation repairs them.
                    root.ResolvedCaseSensitivity = FileSystemCaseSensitivity.Unknown;
                    root.PathIdentityState = PathIdentityState.Unavailable;
                    root.PathIdentityKey = null;
                }
            }

            foreach (var group in resolvedRoots.GroupBy(item => item.Key, StringComparer.Ordinal))
            {
                if (group.Count() == 1)
                {
                    var item = group.Single();
                    item.Root.PathIdentityKey = item.Key;
                    item.Root.PathIdentityState = PathIdentityState.Valid;
                    continue;
                }

                foreach (var item in group)
                {
                    item.Root.PathIdentityState = PathIdentityState.Conflict;
                    item.Root.PathIdentityKey = null;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None);
            Volatile.Write(ref _rootIdentitiesReconciled, true);
        }
        finally
        {
            _rootIdentityGate.Release();
        }
    }
}
