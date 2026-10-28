/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories;

public sealed partial class EfMoveQueuePersistence(
    IDbContextFactory<ListenArrDbContext> dbFactory,
    IFileSystemSemanticsResolver semanticsResolver)
    : IMoveQueuePersistence
{
    public async Task<MoveJob?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            return await db.MoveJobs
                .AsNoTracking()
                .AsSplitQuery()
                .Include(job => job.Entries)
                .Include(job => job.CreatedDirectories)
                .SingleOrDefaultAsync(job => job.Id == id, cancellationToken);
        }
        catch (DbException ex)
        {
            throw new PersistenceException("Failed to query move job persistence.", ex);
        }
    }

    public async Task<MoveJob?> GetActiveByKeyAsync(
        string deduplicationKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            return await db.MoveJobs
                .AsNoTracking()
                .Include(job => job.Entries)
                .SingleOrDefaultAsync(
                    job => job.ActiveDeduplicationKey == deduplicationKey,
                    cancellationToken);
        }
        catch (DbException ex)
        {
            throw new PersistenceException("Failed to query move job persistence.", ex);
        }
    }

    public async Task<IReadOnlyList<MoveJob>> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            return await db.MoveJobs
                .AsNoTracking()
                .Include(job => job.Entries)
                .Where(job => job.ActiveDeduplicationKey != null
                    && (job.Status == MoveJobStatus.Queued
                        || job.Status == MoveJobStatus.Running
                        || job.Status == MoveJobStatus.RetryScheduled))
                .OrderBy(job => job.EnqueuedAt)
                .ToListAsync(cancellationToken);
        }
        catch (DbException ex)
        {
            throw new PersistenceException("Failed to query active move job persistence.", ex);
        }
    }

    public async Task<IReadOnlyList<MoveJob>> GetRecoveryCandidatesByAudiobookAsync(
        int audiobookId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            return await db.MoveJobs
                .AsNoTracking()
                .AsSplitQuery()
                .Include(job => job.Entries)
                .Include(job => job.CreatedDirectories)
                .Where(job => job.AudiobookId == audiobookId
                    && (job.Status == MoveJobStatus.Queued
                        || job.Status == MoveJobStatus.Running
                        || job.Status == MoveJobStatus.RetryScheduled
                        || job.Status == MoveJobStatus.Failed
                        || job.Status == MoveJobStatus.NeedsAttention))
                .OrderBy(job => job.EnqueuedAt)
                .ThenBy(job => job.Id)
                .ToListAsync(cancellationToken);
        }
        catch (DbException ex)
        {
            throw new PersistenceException("Failed to query move recovery candidates.", ex);
        }
    }

    public async Task<IReadOnlyList<MoveJob>> GetRecoveryCandidatesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            return await db.MoveJobs
                .AsNoTracking()
                .AsSplitQuery()
                .Include(job => job.Entries)
                .Include(job => job.CreatedDirectories)
                .Where(job => job.Status == MoveJobStatus.Queued
                    || job.Status == MoveJobStatus.Running
                    || job.Status == MoveJobStatus.RetryScheduled
                    || job.Status == MoveJobStatus.Failed
                    || job.Status == MoveJobStatus.NeedsAttention)
                .OrderBy(job => job.EnqueuedAt)
                .ThenBy(job => job.Id)
                .ToListAsync(cancellationToken);
        }
        catch (DbException ex)
        {
            throw new PersistenceException("Failed to query move recovery candidates.", ex);
        }
    }

    public async Task<MoveQueueHealthSnapshot> GetHealthAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var jobs = await db.MoveJobs
                .AsNoTracking()
                .Where(job => job.Status == MoveJobStatus.Queued
                    || job.Status == MoveJobStatus.Running
                    || job.Status == MoveJobStatus.RetryScheduled
                    || job.Status == MoveJobStatus.NeedsAttention)
                .Select(job => new
                {
                    job.Status,
                    job.EnqueuedAt,
                    job.AttemptCount,
                    job.LeaseExpiresAt
                })
                .ToListAsync(cancellationToken);
            var queued = jobs.Where(job => job.Status is MoveJobStatus.Queued or MoveJobStatus.RetryScheduled).ToList();
            var oldestAge = queued.Count == 0
                ? 0
                : Math.Max(0, (now.UtcDateTime - queued.Min(job => job.EnqueuedAt)).TotalSeconds);
            return new MoveQueueHealthSnapshot(
                queued.Count,
                oldestAge,
                jobs.Sum(job => job.AttemptCount),
                jobs.Count(job => job.Status == MoveJobStatus.Running
                    && job.LeaseExpiresAt != null
                    && job.LeaseExpiresAt <= now.UtcDateTime),
                jobs.Count(job => job.Status == MoveJobStatus.NeedsAttention));
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException)
        {
            throw new PersistenceException("Failed to query move queue health.", ex);
        }
    }

    public async Task AddAsync(MoveJob job, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.MoveJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> MarkNeedsAttentionAsync(
        Guid id,
        MoveJobStatus expectedStatus,
        string error,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            if (!db.Database.IsRelational())
            {
                var trackedJob = await db.MoveJobs.SingleOrDefaultAsync(
                    job => job.Id == id && job.Status == expectedStatus,
                    cancellationToken);
                if (trackedJob == null)
                {
                    return false;
                }

                trackedJob.Status = MoveJobStatus.NeedsAttention;
                trackedJob.Error = error;
                trackedJob.FailureKind = MoveFailureKind.Verification;
                trackedJob.ActiveDeduplicationKey = null;
                trackedJob.LeaseOwner = null;
                trackedJob.LeaseExpiresAt = null;
                trackedJob.UpdatedAt = updatedAt.UtcDateTime;
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }

            var affected = await db.MoveJobs
                .Where(job => job.Id == id && job.Status == expectedStatus)
                .ExecuteUpdateAsync(
                    updates => updates
                        .SetProperty(job => job.Status, MoveJobStatus.NeedsAttention)
                        .SetProperty(job => job.Error, error)
                        .SetProperty(job => job.FailureKind, MoveFailureKind.Verification)
                        .SetProperty(job => job.ActiveDeduplicationKey, (string?)null)
                        .SetProperty(job => job.LeaseOwner, (string?)null)
                        .SetProperty(job => job.LeaseExpiresAt, (DateTime?)null)
                        .SetProperty(job => job.UpdatedAt, updatedAt.UtcDateTime),
                    cancellationToken);
            return affected == 1;
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException)
        {
            throw new PersistenceException("Failed to mark move job as needing attention.", ex);
        }
    }

    public async Task<bool> UpdateStatusAsync(
        Guid id,
        string leaseOwner,
        int leaseGeneration,
        MoveJobStatus status,
        MoveJobPhase phase,
        string? error,
        MoveFailureKind failureKind,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            if (!db.Database.IsRelational())
            {
                var trackedJob = await db.MoveJobs.SingleOrDefaultAsync(
                    job => job.Id == id
                        && job.Status == MoveJobStatus.Running
                        && job.LeaseOwner == leaseOwner
                        && job.LeaseGeneration == leaseGeneration
                        && job.LeaseExpiresAt != null
                        && job.LeaseExpiresAt > updatedAt.UtcDateTime,
                    cancellationToken);
                if (trackedJob == null) return false;
                trackedJob.Status = status;
                trackedJob.Phase = phase;
                trackedJob.Error = error;
                trackedJob.FailureKind = failureKind;
                trackedJob.UpdatedAt = updatedAt.UtcDateTime;
                if (!status.IsActive())
                {
                    trackedJob.ActiveDeduplicationKey = null;
                    trackedJob.LeaseOwner = null;
                    trackedJob.LeaseExpiresAt = null;
                }
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }

            var active = status.IsActive();
            var affected = await db.MoveJobs
                .Where(job => job.Id == id
                    && job.Status == MoveJobStatus.Running
                    && job.LeaseOwner == leaseOwner
                    && job.LeaseGeneration == leaseGeneration
                    && job.LeaseExpiresAt != null
                    && job.LeaseExpiresAt > updatedAt.UtcDateTime)
                .ExecuteUpdateAsync(
                    updates => updates
                        .SetProperty(job => job.Status, status)
                        .SetProperty(job => job.Phase, phase)
                        .SetProperty(job => job.Error, error)
                        .SetProperty(job => job.FailureKind, failureKind)
                        .SetProperty(job => job.UpdatedAt, updatedAt.UtcDateTime)
                        .SetProperty(job => job.ActiveDeduplicationKey, job => active ? job.ActiveDeduplicationKey : null)
                        .SetProperty(job => job.LeaseOwner, job => active ? job.LeaseOwner : null)
                        .SetProperty(job => job.LeaseExpiresAt, job => active ? job.LeaseExpiresAt : null),
                    cancellationToken);
            return affected == 1;
        }
        catch (DbException ex)
        {
            throw new PersistenceException("Failed to update move job persistence.", ex);
        }
    }

    public async Task<int?> TryClaimAsync(
        Guid id,
        string leaseOwner,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var nowUtc = now.UtcDateTime;
            if (db.Database.IsRelational())
            {
                var affected = await db.MoveJobs
                    .Where(job => job.Id == id
                        && job.ActiveDeduplicationKey != null
                        && (job.Status == MoveJobStatus.Queued
                            || job.Status == MoveJobStatus.RetryScheduled
                            || (job.Status == MoveJobStatus.Running
                                && (job.LeaseExpiresAt == null || job.LeaseExpiresAt <= nowUtc)))
                        && (job.NextAttemptAt == null || job.NextAttemptAt <= nowUtc))
                    .ExecuteUpdateAsync(
                        updates => updates
                            .SetProperty(job => job.Status, MoveJobStatus.Running)
                            .SetProperty(job => job.LeaseOwner, leaseOwner)
                            .SetProperty(job => job.LeaseGeneration, job => job.LeaseGeneration + 1)
                            .SetProperty(job => job.LeaseExpiresAt, leaseExpiresAt.UtcDateTime)
                            .SetProperty(job => job.NextAttemptAt, (DateTime?)null)
                            .SetProperty(job => job.UpdatedAt, nowUtc),
                        cancellationToken);
                if (affected != 1) return null;
                return await db.MoveJobs
                    .Where(job => job.Id == id && job.LeaseOwner == leaseOwner)
                    .Select(job => (int?)job.LeaseGeneration)
                    .SingleAsync(cancellationToken);
            }

            var job = await db.MoveJobs.SingleOrDefaultAsync(
                candidate => candidate.Id == id,
                cancellationToken);
            if (job == null
                || job.ActiveDeduplicationKey == null
                || (job.NextAttemptAt != null && job.NextAttemptAt > nowUtc)
                || (job.Status == MoveJobStatus.Running
                    && job.LeaseExpiresAt != null
                    && job.LeaseExpiresAt > nowUtc)
                || job.Status is not (MoveJobStatus.Queued or MoveJobStatus.RetryScheduled or MoveJobStatus.Running))
            {
                return null;
            }

            job.Status = MoveJobStatus.Running;
            job.LeaseOwner = leaseOwner;
            job.LeaseGeneration++;
            job.LeaseExpiresAt = leaseExpiresAt.UtcDateTime;
            job.NextAttemptAt = null;
            job.UpdatedAt = nowUtc;
            await db.SaveChangesAsync(cancellationToken);
            return job.LeaseGeneration;
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException)
        {
            throw new PersistenceException("Failed to claim move job persistence.", ex);
        }
    }

    public async Task<bool> TryIncrementAttemptAsync(
        Guid id,
        string leaseOwner,
        int leaseGeneration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var nowUtc = now.UtcDateTime;
            if (!db.Database.IsRelational())
            {
                var trackedJob = await db.MoveJobs.SingleOrDefaultAsync(
                    job => job.Id == id
                        && job.Status == MoveJobStatus.Running
                        && job.LeaseOwner == leaseOwner
                        && job.LeaseGeneration == leaseGeneration
                        && job.LeaseExpiresAt != null
                        && job.LeaseExpiresAt > nowUtc,
                    cancellationToken);
                if (trackedJob == null) return false;
                trackedJob.AttemptCount++;
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }

            var affected = await db.MoveJobs
                .Where(job => job.Id == id
                    && job.Status == MoveJobStatus.Running
                    && job.LeaseOwner == leaseOwner
                    && job.LeaseGeneration == leaseGeneration
                    && job.LeaseExpiresAt != null
                    && job.LeaseExpiresAt > nowUtc)
                .ExecuteUpdateAsync(
                    updates => updates.SetProperty(
                        job => job.AttemptCount,
                        job => job.AttemptCount + 1),
                    cancellationToken);
            return affected == 1;
        }
        catch (DbException ex)
        {
            throw new PersistenceException("Failed to increment move job attempt count.", ex);
        }
    }

    public async Task<MoveHeartbeatOutcome> HeartbeatAsync(
        Guid id,
        string leaseOwner,
        int leaseGeneration,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var nowUtc = now.UtcDateTime;
            if (!db.Database.IsRelational())
            {
                var trackedJob = await db.MoveJobs.SingleOrDefaultAsync(
                    candidate => candidate.Id == id
                        && candidate.Status == MoveJobStatus.Running
                        && candidate.LeaseOwner == leaseOwner
                        && candidate.LeaseGeneration == leaseGeneration
                        && candidate.LeaseExpiresAt != null
                        && candidate.LeaseExpiresAt > nowUtc,
                    cancellationToken);
                if (trackedJob != null)
                {
                    trackedJob.LeaseExpiresAt = leaseExpiresAt.UtcDateTime;
                    await db.SaveChangesAsync(cancellationToken);
                    return MoveHeartbeatOutcome.Renewed;
                }

                return await ResolveHeartbeatMissAsync(db, id, cancellationToken);
            }

            var affected = await db.MoveJobs
                .Where(candidate => candidate.Id == id
                    && candidate.Status == MoveJobStatus.Running
                    && candidate.LeaseOwner == leaseOwner
                    && candidate.LeaseGeneration == leaseGeneration
                    && candidate.LeaseExpiresAt != null
                    && candidate.LeaseExpiresAt > nowUtc)
                .ExecuteUpdateAsync(
                    updates => updates.SetProperty(
                        job => job.LeaseExpiresAt,
                        leaseExpiresAt.UtcDateTime),
                    cancellationToken);
            return affected == 1
                ? MoveHeartbeatOutcome.Renewed
                : await ResolveHeartbeatMissAsync(db, id, cancellationToken);
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException)
        {
            throw new PersistenceException("Failed to heartbeat move job persistence.", ex);
        }
    }

    private static async Task<MoveHeartbeatOutcome> ResolveHeartbeatMissAsync(
        ListenArrDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        var currentStatus = await db.MoveJobs
            .AsNoTracking()
            .Where(candidate => candidate.Id == id)
            .Select(candidate => (MoveJobStatus?)candidate.Status)
            .SingleOrDefaultAsync(cancellationToken);
        return currentStatus.HasValue && !currentStatus.Value.IsActive()
            ? MoveHeartbeatOutcome.Terminal
            : MoveHeartbeatOutcome.Lost;
    }
}
