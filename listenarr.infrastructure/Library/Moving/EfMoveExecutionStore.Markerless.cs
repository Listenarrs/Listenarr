using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class EfMoveExecutionStore
{
    public Task<MarkerlessMoveEndpointState> GetEndpointObjectIdentitiesAsync(
        Guid jobId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "load markerless move endpoint identities",
            async () =>
            {
                await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                return await db.MoveJobs
                    .Where(job => job.Id == jobId)
                    .Select(job => new MarkerlessMoveEndpointState(
                        job.SourceDirectoryObjectIdentity,
                        job.TargetDirectoryObjectIdentity,
                        job.SourceDirectoryCleanupState))
                    .SingleAsync(cancellationToken);
            },
            cancellationToken);

    public Task<MarkerlessMoveBoundaryAuthorizationState> GetBoundaryAuthorizationsAsync(
        Guid jobId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "load markerless move boundary authorizations",
            async () =>
            {
                await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                var job = await db.MoveJobs
                    .AsNoTracking()
                    .SingleAsync(candidate => candidate.Id == jobId, cancellationToken);
                var sourceAuthorizationBoundary = job.SourceCleanupBoundary
                    ?? job.SourceIdentityBoundary;
                if (string.IsNullOrWhiteSpace(sourceAuthorizationBoundary)
                    || string.IsNullOrWhiteSpace(job.TargetIdentityBoundary))
                {
                    throw new MoveNeedsAttentionException(
                        "The move lacks persisted source or target path boundaries.");
                }

                return new MarkerlessMoveBoundaryAuthorizationState(
                    sourceAuthorizationBoundary,
                    job.TargetIdentityBoundary);
            },
            cancellationToken);

    public Task UpdateEndpointObjectIdentitiesAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string? sourceDirectoryObjectIdentity,
        string? targetDirectoryObjectIdentity,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "persist markerless move endpoint identities",
            async () =>
            {
                EnsureLeaseTokenProvided(jobId, leaseToken);
                var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
                await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                var job = await db.MoveJobs.SingleOrDefaultAsync(
                    candidate => candidate.Id == jobId
                        && candidate.Status == MoveJobStatus.Running
                        && candidate.LeaseOwner == leaseToken.Owner
                        && candidate.LeaseGeneration == leaseToken.Generation
                        && candidate.LeaseExpiresAt != null
                        && candidate.LeaseExpiresAt > nowUtc,
                    cancellationToken);
                if (job == null)
                {
                    throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                }

                var observedSourceIdentity = job.SourceDirectoryObjectIdentity;
                var observedTargetIdentity = job.TargetDirectoryObjectIdentity;
                // Endpoint identities are current-operation diagnostics only.
                // A remount may legitimately change them between attempts.
                var desiredSourceIdentity = sourceDirectoryObjectIdentity
                    ?? observedSourceIdentity;
                var desiredTargetIdentity = targetDirectoryObjectIdentity
                    ?? observedTargetIdentity;
                var forceRetain = job.ForceCopyAndRetainSource
                    || sourceDirectoryObjectIdentity == string.Empty
                    || targetDirectoryObjectIdentity == string.Empty;
                var cleanupMode = forceRetain
                    ? MoveSourceCleanupMode.RetainSource : job.SourceCleanupMode;
                if (!db.Database.IsRelational())
                {
                    job.ForceCopyAndRetainSource = forceRetain;
                    job.SourceCleanupMode = cleanupMode;
                    job.SourceDirectoryObjectIdentity = desiredSourceIdentity;
                    job.TargetDirectoryObjectIdentity = desiredTargetIdentity;
                    job.UpdatedAt = nowUtc;
                    await db.SaveChangesAsync(cancellationToken);
                    return;
                }

                db.Entry(job).State = EntityState.Detached;
                var affected = await db.MoveJobs
                    .Where(candidate => candidate.Id == jobId
                        && candidate.Status == MoveJobStatus.Running
                        && candidate.LeaseOwner == leaseToken.Owner
                        && candidate.LeaseGeneration == leaseToken.Generation
                        && candidate.LeaseExpiresAt != null
                        && candidate.LeaseExpiresAt > nowUtc
                        && candidate.SourceDirectoryObjectIdentity
                            == observedSourceIdentity
                        && candidate.TargetDirectoryObjectIdentity
                            == observedTargetIdentity
                        && candidate.ForceCopyAndRetainSource == job.ForceCopyAndRetainSource
                        && candidate.SourceCleanupMode == job.SourceCleanupMode)
                    .ExecuteUpdateAsync(
                        updates => updates
                            .SetProperty(
                                candidate => candidate.SourceDirectoryObjectIdentity,
                                desiredSourceIdentity)
                            .SetProperty(
                                candidate => candidate.TargetDirectoryObjectIdentity,
                                desiredTargetIdentity)
                            .SetProperty(candidate => candidate.ForceCopyAndRetainSource, forceRetain)
                            .SetProperty(candidate => candidate.SourceCleanupMode, cleanupMode)
                            .SetProperty(candidate => candidate.UpdatedAt, nowUtc),
                        cancellationToken);
                if (affected != 1)
                {
                    throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                }
            },
            cancellationToken);

    public Task UpdateSourceDirectoryCleanupStateAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        MoveJobEntryCleanupState cleanupState,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "persist markerless source-directory cleanup state",
            async () =>
            {
                EnsureLeaseTokenProvided(jobId, leaseToken);
                var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
                await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                var job = await db.MoveJobs.SingleOrDefaultAsync(
                    candidate => candidate.Id == jobId
                        && candidate.Status == MoveJobStatus.Running
                        && candidate.LeaseOwner == leaseToken.Owner
                        && candidate.LeaseGeneration == leaseToken.Generation
                        && candidate.LeaseExpiresAt != null
                        && candidate.LeaseExpiresAt > nowUtc,
                    cancellationToken);
                if (job == null)
                {
                    throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                }

                var observedState = job.SourceDirectoryCleanupState;
                var desiredState = AdvanceCleanupState(observedState, cleanupState);
                if (!db.Database.IsRelational())
                {
                    job.SourceDirectoryCleanupState = desiredState;
                    job.UpdatedAt = nowUtc;
                    await db.SaveChangesAsync(cancellationToken);
                    return;
                }

                db.Entry(job).State = EntityState.Detached;
                var affected = await db.MoveJobs
                    .Where(candidate => candidate.Id == jobId
                        && candidate.Status == MoveJobStatus.Running
                        && candidate.LeaseOwner == leaseToken.Owner
                        && candidate.LeaseGeneration == leaseToken.Generation
                        && candidate.LeaseExpiresAt != null
                        && candidate.LeaseExpiresAt > nowUtc
                        && candidate.SourceDirectoryCleanupState == observedState)
                    .ExecuteUpdateAsync(
                        updates => updates
                            .SetProperty(
                                candidate => candidate.SourceDirectoryCleanupState,
                                desiredState)
                            .SetProperty(candidate => candidate.UpdatedAt, nowUtc),
                        cancellationToken);
                if (affected != 1)
                {
                    throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                }
            },
            cancellationToken);

    public Task UpdateTargetEntryStateAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string relativePath,
        MoveJobEntryCopyState copyState,
        string? targetPhysicalObjectIdentity,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "persist markerless target-file state",
            async () =>
            {
                EnsureLeaseTokenProvided(jobId, leaseToken);
                ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
                if (copyState < MoveJobEntryCopyState.Staged)
                {
                    throw new ArgumentOutOfRangeException(nameof(copyState));
                }
                var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
                await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                var entry = await db.MoveJobEntries
                    .Include(candidate => candidate.MoveJob)
                    .SingleOrDefaultAsync(
                        candidate => candidate.MoveJobId == jobId
                            && candidate.RelativePath == relativePath,
                        cancellationToken);
                if (entry == null
                    || entry.MoveJob.Status != MoveJobStatus.Running
                    || !string.Equals(
                        entry.MoveJob.LeaseOwner,
                        leaseToken.Owner,
                        StringComparison.Ordinal)
                    || entry.MoveJob.LeaseGeneration != leaseToken.Generation
                    || entry.MoveJob.LeaseExpiresAt == null
                    || entry.MoveJob.LeaseExpiresAt <= nowUtc)
                {
                    throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                }

                if (AfterMarkerlessStateLoadedForTestAsync != null)
                {
                    await AfterMarkerlessStateLoadedForTestAsync();
                }

                if (targetPhysicalObjectIdentity == string.Empty)
                {
                    if (!db.Database.IsRelational())
                    {
                        entry.MoveJob.ForceCopyAndRetainSource = true;
                        entry.MoveJob.SourceCleanupMode = MoveSourceCleanupMode.RetainSource;
                    }
                    else
                    {
                        var retained = await db.MoveJobs.Where(job => job.Id == jobId
                            && job.Status == MoveJobStatus.Running
                            && job.LeaseOwner == leaseToken.Owner
                            && job.LeaseGeneration == leaseToken.Generation
                            && job.LeaseExpiresAt != null && job.LeaseExpiresAt > nowUtc)
                            .ExecuteUpdateAsync(updates => updates
                                .SetProperty(job => job.ForceCopyAndRetainSource, true)
                                .SetProperty(job => job.SourceCleanupMode, MoveSourceCleanupMode.RetainSource),
                                cancellationToken);
                        if (retained != 1) throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                    }
                }
                var observedIdentity = entry.TargetPhysicalObjectIdentity;
                var observedCopyState = entry.CopyState;
                // Target kernel identity is diagnostic only. Refresh it from
                // the currently pinned publication instead of treating a remount
                // or another client namespace as an authority mismatch.
                var desiredIdentity = targetPhysicalObjectIdentity
                    ?? observedIdentity;
                var desiredCopyState = observedCopyState < copyState
                    ? copyState
                    : observedCopyState;
                if (!db.Database.IsRelational())
                {
                    entry.TargetPhysicalObjectIdentity = desiredIdentity;
                    entry.CopyState = desiredCopyState;
                    entry.MoveJob.UpdatedAt = nowUtc;
                    await db.SaveChangesAsync(cancellationToken);
                    return;
                }

                db.Entry(entry).State = EntityState.Detached;
                db.Entry(entry.MoveJob).State = EntityState.Detached;
                var affected = await db.MoveJobEntries
                    .Where(candidate => candidate.MoveJobId == jobId
                        && candidate.RelativePath == relativePath
                        && candidate.TargetPhysicalObjectIdentity == observedIdentity
                        && candidate.CopyState == observedCopyState
                        && candidate.MoveJob.Status == MoveJobStatus.Running
                        && candidate.MoveJob.LeaseOwner == leaseToken.Owner
                        && candidate.MoveJob.LeaseGeneration == leaseToken.Generation
                        && candidate.MoveJob.LeaseExpiresAt != null
                        && candidate.MoveJob.LeaseExpiresAt > nowUtc)
                    .ExecuteUpdateAsync(
                        updates => updates
                            .SetProperty(
                                candidate => candidate.TargetPhysicalObjectIdentity,
                                desiredIdentity)
                            .SetProperty(
                                candidate => candidate.CopyState,
                                desiredCopyState),
                        cancellationToken);
                if (affected != 1)
                {
                    throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                }

                _ = await db.MoveJobs
                    .Where(candidate => candidate.Id == jobId
                        && candidate.Status == MoveJobStatus.Running
                        && candidate.LeaseOwner == leaseToken.Owner
                        && candidate.LeaseGeneration == leaseToken.Generation
                        && candidate.LeaseExpiresAt != null
                        && candidate.LeaseExpiresAt > nowUtc)
                    .ExecuteUpdateAsync(
                        updates => updates.SetProperty(
                            candidate => candidate.UpdatedAt,
                            nowUtc),
                        cancellationToken);
            },
            cancellationToken);

    private static void EnsureSameOrUnassigned(
        string? persisted,
        string? current,
        string message)
    {
        if (!string.IsNullOrWhiteSpace(persisted)
            && !string.IsNullOrWhiteSpace(current)
            && !string.Equals(persisted, current, StringComparison.Ordinal))
        {
            throw new MoveNeedsAttentionException(message);
        }
    }

    public Task UpdateCreatedDirectoryPublicationAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string path,
        MoveCreatedDirectoryState state,
        string directoryObjectIdentity,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "persist markerless target-directory state",
            async () =>
            {
                EnsureLeaseTokenProvided(jobId, leaseToken);
                ArgumentException.ThrowIfNullOrWhiteSpace(path);
                ArgumentNullException.ThrowIfNull(directoryObjectIdentity);
                var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
                await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                var directory = await db.MoveJobCreatedDirectories
                    .Include(candidate => candidate.MoveJob)
                    .SingleOrDefaultAsync(
                        candidate => candidate.MoveJobId == jobId
                            && candidate.Path == path,
                        cancellationToken);
                if (directory == null
                    || directory.MoveJob.Status != MoveJobStatus.Running
                    || !string.Equals(
                        directory.MoveJob.LeaseOwner,
                        leaseToken.Owner,
                        StringComparison.Ordinal)
                    || directory.MoveJob.LeaseGeneration != leaseToken.Generation
                    || directory.MoveJob.LeaseExpiresAt == null
                    || directory.MoveJob.LeaseExpiresAt <= nowUtc)
                {
                    throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                }

                var observedIdentity = directory.DirectoryObjectIdentity;
                var observedState = directory.State;
                // Directory identity is an operation-local diagnostic. Durable
                // creation provenance is the move-created-directory row itself.
                var desiredIdentity = directoryObjectIdentity;
                var desiredState = AdvanceCreatedDirectoryState(observedState, state);
                if (!db.Database.IsRelational())
                {
                    directory.DirectoryObjectIdentity = desiredIdentity;
                    directory.State = desiredState;
                    directory.MoveJob.UpdatedAt = nowUtc;
                    await db.SaveChangesAsync(cancellationToken);
                    return;
                }

                db.Entry(directory).State = EntityState.Detached;
                db.Entry(directory.MoveJob).State = EntityState.Detached;
                var affected = await db.MoveJobCreatedDirectories
                    .Where(candidate => candidate.MoveJobId == jobId
                        && candidate.Path == path
                        && candidate.DirectoryObjectIdentity == observedIdentity
                        && candidate.State == observedState
                        && candidate.MoveJob.Status == MoveJobStatus.Running
                        && candidate.MoveJob.LeaseOwner == leaseToken.Owner
                        && candidate.MoveJob.LeaseGeneration == leaseToken.Generation
                        && candidate.MoveJob.LeaseExpiresAt != null
                        && candidate.MoveJob.LeaseExpiresAt > nowUtc)
                    .ExecuteUpdateAsync(
                        updates => updates
                            .SetProperty(
                                candidate => candidate.DirectoryObjectIdentity,
                                desiredIdentity)
                            .SetProperty(candidate => candidate.State, desiredState),
                        cancellationToken);
                if (affected != 1)
                {
                    throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                }

                _ = await db.MoveJobs
                    .Where(candidate => candidate.Id == jobId
                        && candidate.Status == MoveJobStatus.Running
                        && candidate.LeaseOwner == leaseToken.Owner
                        && candidate.LeaseGeneration == leaseToken.Generation
                        && candidate.LeaseExpiresAt != null
                        && candidate.LeaseExpiresAt > nowUtc)
                    .ExecuteUpdateAsync(
                        updates => updates.SetProperty(
                            candidate => candidate.UpdatedAt,
                            nowUtc),
                        cancellationToken);
            },
            cancellationToken);
}
