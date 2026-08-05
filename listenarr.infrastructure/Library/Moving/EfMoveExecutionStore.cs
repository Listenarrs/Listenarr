using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class EfMoveExecutionStore(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    TimeProvider timeProvider) : IMoveExecutionStore
{
    public Task EnsureLeaseOwnedAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "validate the active move lease",
            async () =>
            {
                EnsureLeaseTokenProvided(jobId, leaseToken);
                var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
                await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                if (!await IsLeaseActiveAsync(
                        db,
                        jobId,
                        leaseToken,
                        nowUtc,
                        cancellationToken))
                {
                    throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                }
            },
            cancellationToken);

    public Task ValidateOrAdoptIdentityAsync(
        Guid jobId,
        string source,
        string target,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        MoveLeaseToken leaseToken,
        bool hasFilesystemRecoveryArtifacts,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "validate the persisted move identity",
            async () =>
            {
                EnsureLeaseTokenProvided(jobId, leaseToken);
                await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                var identity = await db.MoveJobs
                    .AsNoTracking()
                    .Where(job => job.Id == jobId)
                    .Select(job => new { job.SourcePath, job.RequestedPath })
                    .SingleOrDefaultAsync(cancellationToken);
                if (identity == null || string.IsNullOrWhiteSpace(identity.RequestedPath))
                {
                    throw new MoveNeedsAttentionException(
                        "Persisted move target identity is required before filesystem recovery.");
                }

                EnsureEquivalentIdentity(
                    identity.RequestedPath,
                    target,
                    targetSemantics,
                    "Persisted move target identity does not match the requested filesystem operation.",
                    "Persisted move target identity is invalid.");

                var persistedSource = identity.SourcePath;
                if (string.IsNullOrWhiteSpace(persistedSource))
                {
                    var hasManifest = await db.MoveJobEntries.AnyAsync(
                        entry => entry.MoveJobId == jobId,
                        cancellationToken);
                    if (hasManifest || hasFilesystemRecoveryArtifacts)
                    {
                        throw new MoveNeedsAttentionException(
                            "A legacy move without a persisted source cannot own existing recovery artifacts.");
                    }

                    var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
                    if (!db.Database.IsRelational())
                    {
                        var job = await db.MoveJobs.SingleOrDefaultAsync(
                            candidate => candidate.Id == jobId
                                && candidate.Status == MoveJobStatus.Running
                                && candidate.LeaseOwner == leaseToken.Owner
                                && candidate.LeaseGeneration == leaseToken.Generation
                                && candidate.LeaseExpiresAt != null
                                && candidate.LeaseExpiresAt > nowUtc,
                            cancellationToken);
                        if (job == null || !string.IsNullOrWhiteSpace(job.SourcePath))
                        {
                            throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                        }

                        job.SourcePath = source;
                        await db.SaveChangesAsync(cancellationToken);
                    }
                    else
                    {
                        var affected = await db.MoveJobs
                            .Where(candidate => candidate.Id == jobId
                                && candidate.SourcePath == identity.SourcePath
                                && candidate.Status == MoveJobStatus.Running
                                && candidate.LeaseOwner == leaseToken.Owner
                                && candidate.LeaseGeneration == leaseToken.Generation
                                && candidate.LeaseExpiresAt != null
                                && candidate.LeaseExpiresAt > nowUtc)
                            .ExecuteUpdateAsync(
                                updates => updates.SetProperty(job => job.SourcePath, source),
                                cancellationToken);
                        if (affected != 1)
                        {
                            throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                        }
                    }

                    persistedSource = source;
                }

                EnsureEquivalentIdentity(
                    persistedSource,
                    source,
                    sourceSemantics,
                    "Persisted move source identity does not match the requested filesystem operation.",
                    "Persisted move source identity is invalid.");
            },
            cancellationToken);

    public Task EnsureMutationAuthorizedAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string source,
        string target,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "authorize a move filesystem mutation",
            async () =>
            {
                EnsureLeaseTokenProvided(jobId, leaseToken);
                var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
                await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                var state = await db.MoveJobs
                    .AsNoTracking()
                    .Where(job => job.Id == jobId
                        && job.Status == MoveJobStatus.Running
                        && job.LeaseOwner == leaseToken.Owner
                        && job.LeaseGeneration == leaseToken.Generation
                        && job.LeaseExpiresAt != null
                        && job.LeaseExpiresAt > nowUtc)
                    .Select(job => new
                    {
                        job.SourcePath,
                        job.RequestedPath,
                        job.TargetIdentityBoundary,
                        job.RelocationId
                    })
                    .SingleOrDefaultAsync(cancellationToken);
                if (state == null)
                {
                    throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                }

                if (string.IsNullOrWhiteSpace(state.SourcePath)
                    || string.IsNullOrWhiteSpace(state.RequestedPath))
                {
                    throw new MoveNeedsAttentionException(
                        "Persisted source and target identities are required before a filesystem mutation.");
                }

                EnsureEquivalentIdentity(
                    state.SourcePath,
                    source,
                    sourceSemantics,
                    "Persisted move identity changed before a filesystem mutation.",
                    "Persisted move identity became invalid before a filesystem mutation.");
                EnsureEquivalentIdentity(
                    state.RequestedPath,
                    target,
                    targetSemantics,
                    "Persisted move identity changed before a filesystem mutation.",
                    "Persisted move identity became invalid before a filesystem mutation.");
                if (string.IsNullOrWhiteSpace(state.TargetIdentityBoundary))
                {
                    throw new MoveNeedsAttentionException(
                        "The move target has no durable authorization boundary.");
                }
                await EnsureTargetBoundaryGenerationAuthorizedAsync(
                    db,
                    jobId,
                    state.TargetIdentityBoundary,
                    cancellationToken);
                if (state.RelocationId.HasValue)
                {
                    await EnsureRelocationTargetGenerationAuthorizedAsync(
                        db,
                        state.RelocationId.Value,
                        target,
                        targetSemantics,
                        cancellationToken);
                }
            },
            cancellationToken);

    public Task<List<MoveJobEntry>> LoadManifestAsync(
        Guid jobId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "load the move manifest",
            async () =>
            {
                await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                var entries = await db.MoveJobEntries
                    .AsNoTracking()
                    .Where(entry => entry.MoveJobId == jobId)
                    .OrderBy(entry => entry.Id)
                    .ToListAsync(cancellationToken);
                return entries
                    .Where(entry =>
                        !MoveManifestIdentity.IsTargetBoundaryAuthorization(entry))
                    .ToList();
            },
            cancellationToken);

    public Task UpdateCleanupStateAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string relativePath,
        MoveJobEntryCleanupState cleanupState,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "persist move source cleanup state",
            async () =>
            {
                EnsureLeaseTokenProvided(jobId, leaseToken);
                var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
                await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                if (!db.Database.IsRelational())
                {
                    var entry = await db.MoveJobEntries.SingleOrDefaultAsync(
                        candidate => candidate.MoveJobId == jobId
                            && candidate.RelativePath == relativePath
                            && candidate.MoveJob.Status == MoveJobStatus.Running
                            && candidate.MoveJob.LeaseOwner == leaseToken.Owner
                            && candidate.MoveJob.LeaseGeneration == leaseToken.Generation
                            && candidate.MoveJob.LeaseExpiresAt != null
                            && candidate.MoveJob.LeaseExpiresAt > nowUtc,
                        cancellationToken);
                    if (entry == null)
                    {
                        throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                    }

                    entry.CleanupState = cleanupState;
                    await db.SaveChangesAsync(cancellationToken);
                    return;
                }

                var affected = await db.MoveJobEntries
                    .Where(entry => entry.MoveJobId == jobId
                        && entry.RelativePath == relativePath
                        && entry.MoveJob.Status == MoveJobStatus.Running
                        && entry.MoveJob.LeaseOwner == leaseToken.Owner
                        && entry.MoveJob.LeaseGeneration == leaseToken.Generation
                        && entry.MoveJob.LeaseExpiresAt != null
                        && entry.MoveJob.LeaseExpiresAt > nowUtc)
                    .ExecuteUpdateAsync(
                        updates => updates.SetProperty(entry => entry.CleanupState, cleanupState),
                        cancellationToken);
                if (affected != 1)
                {
                    throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                }
            },
            cancellationToken);

    public Task UpdateCleanupProtectionVersionAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string relativePath,
        int cleanupProtectionVersion,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "persist move source cleanup protection version",
            async () =>
            {
                EnsureLeaseTokenProvided(jobId, leaseToken);
                if (cleanupProtectionVersion < 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(cleanupProtectionVersion));
                }

                var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
                await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                if (!db.Database.IsRelational())
                {
                    var entry = await db.MoveJobEntries.SingleOrDefaultAsync(
                        candidate => candidate.MoveJobId == jobId
                            && candidate.RelativePath == relativePath
                            && candidate.MoveJob.Status == MoveJobStatus.Running
                            && candidate.MoveJob.LeaseOwner == leaseToken.Owner
                            && candidate.MoveJob.LeaseGeneration == leaseToken.Generation
                            && candidate.MoveJob.LeaseExpiresAt != null
                            && candidate.MoveJob.LeaseExpiresAt > nowUtc,
                        cancellationToken);
                    if (entry == null)
                    {
                        throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                    }

                    entry.CleanupProtectionVersion = cleanupProtectionVersion;
                    await db.SaveChangesAsync(cancellationToken);
                    return;
                }

                var affected = await db.MoveJobEntries
                    .Where(entry => entry.MoveJobId == jobId
                        && entry.RelativePath == relativePath
                        && entry.MoveJob.Status == MoveJobStatus.Running
                        && entry.MoveJob.LeaseOwner == leaseToken.Owner
                        && entry.MoveJob.LeaseGeneration == leaseToken.Generation
                        && entry.MoveJob.LeaseExpiresAt != null
                        && entry.MoveJob.LeaseExpiresAt > nowUtc)
                    .ExecuteUpdateAsync(
                        updates => updates.SetProperty(
                            entry => entry.CleanupProtectionVersion,
                            cleanupProtectionVersion),
                        cancellationToken);
                if (affected != 1)
                {
                    throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                }
            },
            cancellationToken);

    public Task UpdateCopyStateAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "persist move copy verification state",
            async () =>
            {
                EnsureLeaseTokenProvided(jobId, leaseToken);
                var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
                await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                if (!await IsLeaseActiveAsync(db, jobId, leaseToken, nowUtc, cancellationToken))
                {
                    throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                }

                if (!db.Database.IsRelational())
                {
                    var entries = await db.MoveJobEntries
                        .Where(entry => entry.MoveJobId == jobId
                            && entry.RelativePath != string.Empty)
                        .ToListAsync(cancellationToken);
                    foreach (var entry in entries)
                    {
                        entry.CopyState = MoveJobEntryCopyState.Verified;
                    }
                    await db.SaveChangesAsync(cancellationToken);
                    return;
                }

                var affected = await db.MoveJobEntries
                    .Where(entry => entry.MoveJobId == jobId
                        && entry.RelativePath != string.Empty
                        && entry.MoveJob.Status == MoveJobStatus.Running
                        && entry.MoveJob.LeaseOwner == leaseToken.Owner
                        && entry.MoveJob.LeaseGeneration == leaseToken.Generation
                        && entry.MoveJob.LeaseExpiresAt != null
                        && entry.MoveJob.LeaseExpiresAt > nowUtc)
                    .ExecuteUpdateAsync(
                        updates => updates.SetProperty(
                            entry => entry.CopyState,
                            MoveJobEntryCopyState.Verified),
                        cancellationToken);
                var expected = await db.MoveJobEntries.CountAsync(
                    entry => entry.MoveJobId == jobId
                        && entry.RelativePath != string.Empty,
                    cancellationToken);
                if (affected != expected)
                {
                    throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                }
            },
            cancellationToken);

    public Task UpdateJobPhaseAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        MoveJobPhase phase,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "advance the move phase",
            async () =>
            {
                EnsureLeaseTokenProvided(jobId, leaseToken);
                var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
                await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                if (!db.Database.IsRelational())
                {
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

                    if (job.Phase < phase)
                    {
                        job.Phase = phase;
                    }
                    job.UpdatedAt = nowUtc;
                    await db.SaveChangesAsync(cancellationToken);
                    return;
                }

                var affected = await db.MoveJobs
                    .Where(candidate => candidate.Id == jobId
                        && candidate.Status == MoveJobStatus.Running
                        && candidate.LeaseOwner == leaseToken.Owner
                        && candidate.LeaseGeneration == leaseToken.Generation
                        && candidate.LeaseExpiresAt != null
                        && candidate.LeaseExpiresAt > nowUtc)
                    .ExecuteUpdateAsync(
                        updates => updates
                            .SetProperty(
                                job => job.Phase,
                                job => job.Phase < phase ? phase : job.Phase)
                            .SetProperty(job => job.UpdatedAt, nowUtc),
                        cancellationToken);
                if (affected != 1)
                {
                    throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                }
            },
            cancellationToken);

}
