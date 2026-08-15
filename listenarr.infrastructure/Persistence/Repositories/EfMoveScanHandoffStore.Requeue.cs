using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories;

public sealed partial class EfMoveScanHandoffStore
{
    public async Task<bool> ReleaseClaimAsync(
        Guid handoffId,
        string leaseOwner,
        int leaseGeneration,
        string? error,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var nowUtc = now.UtcDateTime;
            if (db.Database.IsRelational())
            {
                return await db.MoveScanHandoffs
                    .Where(handoff => handoff.Id == handoffId
                        && handoff.Status == MoveScanHandoffStatus.Claimed
                        && handoff.LeaseOwner == leaseOwner
                        && handoff.LeaseGeneration == leaseGeneration)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(handoff => handoff.Status, MoveScanHandoffStatus.Pending)
                        .SetProperty(handoff => handoff.LeaseOwner, (string?)null)
                        .SetProperty(handoff => handoff.LeaseExpiresAt, (DateTime?)null)
                        .SetProperty(handoff => handoff.NextAttemptAt, (DateTime?)null)
                        .SetProperty(handoff => handoff.ActiveScanJobId, (Guid?)null)
                        .SetProperty(handoff => handoff.LastError, error)
                        .SetProperty(handoff => handoff.UpdatedAt, nowUtc),
                        cancellationToken) == 1;
            }

            var handoff = await db.MoveScanHandoffs.SingleOrDefaultAsync(
                candidate => candidate.Id == handoffId
                    && candidate.Status == MoveScanHandoffStatus.Claimed
                    && candidate.LeaseOwner == leaseOwner
                    && candidate.LeaseGeneration == leaseGeneration,
                cancellationToken);
            if (handoff == null)
            {
                return false;
            }

            ApplyRequeueState(handoff, error, nowUtc);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            throw new PersistenceException(
                "Failed to release a move scan handoff claim.",
                exception);
        }
    }

    public async Task<bool> RequeueAsync(
        Guid handoffId,
        Guid expectedScanJobId,
        int expectedAttemptGeneration,
        string? error,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var nowUtc = now.UtcDateTime;
            if (db.Database.IsRelational())
            {
                return await db.MoveScanHandoffs
                    .Where(handoff => handoff.Id == handoffId
                        && handoff.Status == MoveScanHandoffStatus.Failed
                        && handoff.ActiveScanJobId == expectedScanJobId
                        && handoff.AttemptGeneration == expectedAttemptGeneration)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(handoff => handoff.Status, MoveScanHandoffStatus.Pending)
                        .SetProperty(handoff => handoff.LeaseOwner, (string?)null)
                        .SetProperty(handoff => handoff.LeaseExpiresAt, (DateTime?)null)
                        .SetProperty(handoff => handoff.NextAttemptAt, (DateTime?)null)
                        .SetProperty(handoff => handoff.ActiveScanJobId, (Guid?)null)
                        .SetProperty(handoff => handoff.LastError, error)
                        .SetProperty(handoff => handoff.UpdatedAt, nowUtc),
                        cancellationToken) == 1;
            }

            var handoff = await db.MoveScanHandoffs.SingleOrDefaultAsync(
                candidate => candidate.Id == handoffId
                    && candidate.Status == MoveScanHandoffStatus.Failed
                    && candidate.ActiveScanJobId == expectedScanJobId
                    && candidate.AttemptGeneration == expectedAttemptGeneration,
                cancellationToken);
            if (handoff == null)
            {
                return false;
            }

            ApplyRequeueState(handoff, error, nowUtc);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            throw new PersistenceException(
                "Failed to requeue a move scan handoff.",
                exception);
        }
    }

    private static void ApplyRequeueState(
        MoveScanHandoff handoff,
        string? error,
        DateTime nowUtc)
    {
        handoff.Status = MoveScanHandoffStatus.Pending;
        handoff.LeaseOwner = null;
        handoff.LeaseExpiresAt = null;
        handoff.NextAttemptAt = null;
        handoff.ActiveScanJobId = null;
        handoff.LastError = error;
        handoff.UpdatedAt = nowUtc;
    }
}
