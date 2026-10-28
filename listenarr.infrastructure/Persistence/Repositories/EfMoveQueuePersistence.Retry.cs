using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories;

public sealed partial class EfMoveQueuePersistence
{
    public async Task<MoveRetryScheduleResult?> ScheduleRetryAsync(
        Guid id,
        string leaseOwner,
        int leaseGeneration,
        int expectedAttemptCount,
        DateTimeOffset updatedAt,
        DateTimeOffset nextAttemptAt,
        int maxAttempts,
        string error,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var attemptCount = expectedAttemptCount + 1;
            var terminal = attemptCount >= maxAttempts;
            var status = terminal
                ? MoveJobStatus.NeedsAttention
                : MoveJobStatus.RetryScheduled;
            var persistedNextAttemptAt = terminal
                ? (DateTime?)null
                : nextAttemptAt.UtcDateTime;
            var persistedError = terminal
                ? $"{error} Automatic retry limit exhausted; operator attention is required."
                : error;
            var nowUtc = updatedAt.UtcDateTime;
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            if (!db.Database.IsRelational())
            {
                var trackedJob = await db.MoveJobs.SingleOrDefaultAsync(
                    job => job.Id == id
                        && job.Status == MoveJobStatus.Running
                        && job.LeaseOwner == leaseOwner
                        && job.LeaseGeneration == leaseGeneration
                        && job.LeaseExpiresAt != null
                        && job.LeaseExpiresAt > nowUtc
                        && job.AttemptCount == expectedAttemptCount,
                    cancellationToken);
                if (trackedJob == null) return null;
                trackedJob.AttemptCount = attemptCount;
                trackedJob.Status = status;
                trackedJob.Error = persistedError;
                trackedJob.FailureKind = MoveFailureKind.Transient;
                trackedJob.NextAttemptAt = persistedNextAttemptAt;
                trackedJob.UpdatedAt = nowUtc;
                trackedJob.LeaseOwner = null;
                trackedJob.LeaseExpiresAt = null;
                if (terminal)
                {
                    trackedJob.ActiveDeduplicationKey = null;
                }

                await db.SaveChangesAsync(cancellationToken);
                return new MoveRetryScheduleResult(
                    status,
                    attemptCount,
                    terminal ? null : nextAttemptAt);
            }

            var affected = await db.MoveJobs
                .Where(job => job.Id == id
                    && job.Status == MoveJobStatus.Running
                    && job.LeaseOwner == leaseOwner
                    && job.LeaseGeneration == leaseGeneration
                    && job.LeaseExpiresAt != null
                    && job.LeaseExpiresAt > nowUtc
                    && job.AttemptCount == expectedAttemptCount)
                .ExecuteUpdateAsync(
                    updates => updates
                        .SetProperty(job => job.AttemptCount, attemptCount)
                        .SetProperty(job => job.Status, status)
                        .SetProperty(job => job.Error, persistedError)
                        .SetProperty(job => job.FailureKind, MoveFailureKind.Transient)
                        .SetProperty(job => job.NextAttemptAt, persistedNextAttemptAt)
                        .SetProperty(job => job.UpdatedAt, nowUtc)
                        .SetProperty(
                            job => job.ActiveDeduplicationKey,
                            job => terminal ? null : job.ActiveDeduplicationKey)
                        .SetProperty(job => job.LeaseOwner, (string?)null)
                        .SetProperty(job => job.LeaseExpiresAt, (DateTime?)null),
                    cancellationToken);
            return affected == 1
                ? new MoveRetryScheduleResult(
                    status,
                    attemptCount,
                    terminal ? null : nextAttemptAt)
                : null;
        }
        catch (DbException ex)
        {
            throw new PersistenceException("Failed to schedule move job retry.", ex);
        }
    }
}
