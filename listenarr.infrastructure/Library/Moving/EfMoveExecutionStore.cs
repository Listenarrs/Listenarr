using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class EfMoveExecutionStore(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    TimeProvider timeProvider,
    IFileSystemSemanticsResolver? semanticsResolver = null) : IMoveExecutionStore
{
    private readonly IFileSystemSemanticsResolver _semanticsResolver =
        semanticsResolver ?? new FileSystemSemanticsResolver();
    internal Func<Task>? AfterMarkerlessStateLoadedForTestAsync { get; set; }

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

    public Task<int> GetExecutionProtocolVersionAsync(
        Guid jobId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "load the move execution protocol",
            async () =>
            {
                await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                return await db.MoveJobs
                    .Where(job => job.Id == jobId)
                    .Select(job => job.ExecutionProtocolVersion)
                    .SingleAsync(cancellationToken);
            },
            cancellationToken);

    public Task ValidateIdentityAsync(
        Guid jobId,
        string source,
        string target,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        MoveLeaseToken leaseToken,
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
                    throw new MoveNeedsAttentionException(
                        "Persisted move source identity is required before filesystem mutation.");
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
                        job.SourceIdentityBoundary,
                        job.SourceCaseSensitivityMode,
                        job.TargetIdentityBoundary,
                        job.TargetCaseSensitivityMode,
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
                if (string.IsNullOrWhiteSpace(state.SourceIdentityBoundary)
                    || !state.SourceCaseSensitivityMode.HasValue
                    || string.IsNullOrWhiteSpace(state.TargetIdentityBoundary)
                    || !state.TargetCaseSensitivityMode.HasValue)
                {
                    throw new MoveNeedsAttentionException(
                        "The move lacks durable source or target filesystem semantics authorization.");
                }
                await EnsureLiveFilesystemSemanticsAsync(
                    state.SourceIdentityBoundary,
                    state.SourceCaseSensitivityMode.Value,
                    sourceSemantics,
                    "source",
                    cancellationToken);
                await EnsureLiveFilesystemSemanticsAsync(
                    state.TargetIdentityBoundary,
                    state.TargetCaseSensitivityMode.Value,
                    targetSemantics,
                    "target",
                    cancellationToken);
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
                        !MoveManifestIdentity.IsBoundaryAuthorization(entry))
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

                var observedState = entry.CleanupState;
                var desiredState = AdvanceCleanupState(observedState, cleanupState);
                if (!db.Database.IsRelational())
                {
                    entry.CleanupState = desiredState;
                    await db.SaveChangesAsync(cancellationToken);
                    return;
                }

                db.Entry(entry).State = EntityState.Detached;
                db.Entry(entry.MoveJob).State = EntityState.Detached;
                var affected = await db.MoveJobEntries
                    .Where(candidate => candidate.MoveJobId == jobId
                        && candidate.RelativePath == relativePath
                        && candidate.CleanupState == observedState
                        && candidate.MoveJob.Status == MoveJobStatus.Running
                        && candidate.MoveJob.LeaseOwner == leaseToken.Owner
                        && candidate.MoveJob.LeaseGeneration == leaseToken.Generation
                        && candidate.MoveJob.LeaseExpiresAt != null
                        && candidate.MoveJob.LeaseExpiresAt > nowUtc)
                    .ExecuteUpdateAsync(
                        updates => updates.SetProperty(
                            candidate => candidate.CleanupState,
                            desiredState),
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
