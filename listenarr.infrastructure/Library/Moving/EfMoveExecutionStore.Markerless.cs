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
                if (string.IsNullOrWhiteSpace(sourceDirectoryObjectIdentity)
                    && string.IsNullOrWhiteSpace(targetDirectoryObjectIdentity))
                {
                    throw new ArgumentException(
                        "At least one endpoint physical identity is required.");
                }

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

                EnsureSameOrUnassigned(
                    job.SourceDirectoryObjectIdentity,
                    sourceDirectoryObjectIdentity,
                    "The source root changed physical generation.");
                EnsureSameOrUnassigned(
                    job.TargetDirectoryObjectIdentity,
                    targetDirectoryObjectIdentity,
                    "The target root changed physical generation.");
                var observedSourceIdentity = job.SourceDirectoryObjectIdentity;
                var observedTargetIdentity = job.TargetDirectoryObjectIdentity;
                var desiredSourceIdentity = observedSourceIdentity
                    ?? sourceDirectoryObjectIdentity;
                var desiredTargetIdentity = observedTargetIdentity
                    ?? targetDirectoryObjectIdentity;
                if (!db.Database.IsRelational())
                {
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
                            == observedTargetIdentity)
                    .ExecuteUpdateAsync(
                        updates => updates
                            .SetProperty(
                                candidate => candidate.SourceDirectoryObjectIdentity,
                                desiredSourceIdentity)
                            .SetProperty(
                                candidate => candidate.TargetDirectoryObjectIdentity,
                                desiredTargetIdentity)
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
                if (copyState == MoveJobEntryCopyState.Staged
                    && string.IsNullOrWhiteSpace(targetPhysicalObjectIdentity))
                {
                    throw new ArgumentException(
                        "A staged target file requires a physical object identity.",
                        nameof(targetPhysicalObjectIdentity));
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

                if (!string.IsNullOrWhiteSpace(entry.TargetPhysicalObjectIdentity)
                    && !string.IsNullOrWhiteSpace(targetPhysicalObjectIdentity)
                    && !string.Equals(
                        entry.TargetPhysicalObjectIdentity,
                        targetPhysicalObjectIdentity,
                        StringComparison.Ordinal))
                {
                    throw new MoveNeedsAttentionException(
                        "The target file generation changed after markerless publication began.");
                }

                if (AfterMarkerlessStateLoadedForTestAsync != null)
                {
                    await AfterMarkerlessStateLoadedForTestAsync();
                }

                var observedIdentity = entry.TargetPhysicalObjectIdentity;
                var observedCopyState = entry.CopyState;
                var desiredIdentity = observedIdentity
                    ?? targetPhysicalObjectIdentity;
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
                ArgumentException.ThrowIfNullOrWhiteSpace(directoryObjectIdentity);
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

                if (!string.IsNullOrWhiteSpace(directory.DirectoryObjectIdentity)
                    && !string.Equals(
                        directory.DirectoryObjectIdentity,
                        directoryObjectIdentity,
                        StringComparison.Ordinal))
                {
                    throw new MoveNeedsAttentionException(
                        "A move-created target directory changed physical generation.");
                }
                var observedIdentity = directory.DirectoryObjectIdentity;
                var observedState = directory.State;
                var desiredIdentity = observedIdentity ?? directoryObjectIdentity;
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
