using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories;

public sealed partial class EfMoveScanHandoffStore
{
    public async Task<bool> MarkDispatchedAsync(
        Guid handoffId,
        string leaseOwner,
        int leaseGeneration,
        Guid scanJobId,
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
                        && handoff.Status == MoveScanHandoffStatus.Claimed
                        && handoff.LeaseOwner == leaseOwner
                        && handoff.LeaseGeneration == leaseGeneration
                        && handoff.LeaseExpiresAt != null
                        && handoff.LeaseExpiresAt > nowUtc)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(handoff => handoff.ActiveScanJobId, scanJobId)
                        .SetProperty(handoff => handoff.UpdatedAt, nowUtc),
                        cancellationToken) == 1;
            }

            var handoff = await db.MoveScanHandoffs.SingleOrDefaultAsync(candidate =>
                candidate.Id == handoffId
                && candidate.Status == MoveScanHandoffStatus.Claimed
                && candidate.LeaseOwner == leaseOwner
                && candidate.LeaseGeneration == leaseGeneration
                && candidate.LeaseExpiresAt != null
                && candidate.LeaseExpiresAt > nowUtc,
                cancellationToken);
            if (handoff == null)
            {
                return false;
            }

            handoff.ActiveScanJobId = scanJobId;
            handoff.UpdatedAt = nowUtc;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            throw new PersistenceException("Failed to persist move scan dispatch.", exception);
        }
    }

    public async Task<MoveScanLeaseRenewalResult> RenewAttemptLeaseAsync(
        Guid handoffId,
        int attemptGeneration,
        Guid scanJobId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var nowUtc = now.UtcDateTime;
            var leaseExpiresAtUtc = leaseExpiresAt.UtcDateTime;
            if (db.Database.IsRelational())
            {
                var affected = await db.MoveScanHandoffs
                    .Where(handoff => handoff.Id == handoffId
                        && handoff.Status == MoveScanHandoffStatus.Claimed
                        && handoff.AttemptGeneration == attemptGeneration
                        && handoff.ActiveScanJobId == scanJobId
                        && handoff.LeaseExpiresAt != null
                        && handoff.LeaseExpiresAt > nowUtc)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(handoff => handoff.LeaseExpiresAt, leaseExpiresAtUtc)
                        .SetProperty(handoff => handoff.UpdatedAt, nowUtc),
                        cancellationToken);
                if (affected == 1)
                {
                    return new MoveScanLeaseRenewalResult(
                        MoveScanLeaseRenewalOutcome.Renewed);
                }
            }
            else
            {
                var handoff = await db.MoveScanHandoffs.SingleOrDefaultAsync(candidate =>
                    candidate.Id == handoffId
                    && candidate.Status == MoveScanHandoffStatus.Claimed
                    && candidate.AttemptGeneration == attemptGeneration
                    && candidate.ActiveScanJobId == scanJobId
                    && candidate.LeaseExpiresAt != null
                    && candidate.LeaseExpiresAt > nowUtc,
                    cancellationToken);
                if (handoff != null)
                {
                    handoff.LeaseExpiresAt = leaseExpiresAtUtc;
                    handoff.UpdatedAt = nowUtc;
                    await db.SaveChangesAsync(cancellationToken);
                    return new MoveScanLeaseRenewalResult(
                        MoveScanLeaseRenewalOutcome.Renewed);
                }
            }

            var current = await db.MoveScanHandoffs
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == handoffId, cancellationToken);
            if (current == null
                || current.AttemptGeneration != attemptGeneration
                || current.ActiveScanJobId != scanJobId)
            {
                return new MoveScanLeaseRenewalResult(
                    MoveScanLeaseRenewalOutcome.Superseded);
            }

            return current.Status switch
            {
                MoveScanHandoffStatus.Succeeded => new MoveScanLeaseRenewalResult(
                    MoveScanLeaseRenewalOutcome.Completed),
                MoveScanHandoffStatus.Failed => new MoveScanLeaseRenewalResult(
                    MoveScanLeaseRenewalOutcome.Failed,
                    current.LastError),
                MoveScanHandoffStatus.Superseded => new MoveScanLeaseRenewalResult(
                    MoveScanLeaseRenewalOutcome.Superseded,
                    current.LastError),
                _ => new MoveScanLeaseRenewalResult(
                    MoveScanLeaseRenewalOutcome.Superseded)
            };
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            throw new PersistenceException("Failed to renew a move scan attempt lease.", exception);
        }
    }

    public async Task<MoveScanAttemptResult> CompleteAttemptAsync(
        Guid handoffId,
        int attemptGeneration,
        Guid? scanJobId,
        MoveScanTerminalOutcome outcome,
        string? error,
        int found,
        int created,
        string? scanPath,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(cancellationToken)
                : null;
            var snapshot = await db.MoveScanHandoffs
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == handoffId, cancellationToken);
            if (snapshot == null
                || snapshot.AttemptGeneration != attemptGeneration
                || snapshot.ActiveScanJobId != scanJobId)
            {
                return new MoveScanAttemptResult(MoveScanAttemptOutcome.Superseded, null);
            }
            if (snapshot.Status != MoveScanHandoffStatus.Claimed)
            {
                return ToAttemptResult(snapshot.Status, snapshot.LastError);
            }

            var nowUtc = now.UtcDateTime;
            if (snapshot.LeaseExpiresAt == null
                || snapshot.LeaseExpiresAt <= nowUtc)
            {
                return new MoveScanAttemptResult(
                    MoveScanAttemptOutcome.Superseded,
                    null);
            }
            var terminalStatus = outcome switch
            {
                MoveScanTerminalOutcome.Succeeded => MoveScanHandoffStatus.Succeeded,
                MoveScanTerminalOutcome.Failed => MoveScanHandoffStatus.Failed,
                MoveScanTerminalOutcome.Superseded => MoveScanHandoffStatus.Superseded,
                _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
            };
            var terminalError = outcome == MoveScanTerminalOutcome.Succeeded ? null : error;
            if (db.Database.IsRelational())
            {
                var affected = await db.MoveScanHandoffs
                    .Where(handoff => handoff.Id == handoffId
                        && handoff.AttemptGeneration == attemptGeneration
                        && handoff.ActiveScanJobId == scanJobId
                        && handoff.Status == MoveScanHandoffStatus.Claimed
                        && handoff.LeaseExpiresAt != null
                        && handoff.LeaseExpiresAt > nowUtc)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(handoff => handoff.Status, terminalStatus)
                        .SetProperty(handoff => handoff.LastError, terminalError)
                        .SetProperty(handoff => handoff.LeaseOwner, (string?)null)
                        .SetProperty(handoff => handoff.LeaseExpiresAt, (DateTime?)null)
                        .SetProperty(handoff => handoff.NextAttemptAt, (DateTime?)null)
                        .SetProperty(handoff => handoff.UpdatedAt, nowUtc),
                        cancellationToken);
                if (affected != 1)
                {
                    var current = await db.MoveScanHandoffs
                        .AsNoTracking()
                        .SingleOrDefaultAsync(candidate => candidate.Id == handoffId, cancellationToken);
                    return current == null
                        || current.AttemptGeneration != attemptGeneration
                        || current.ActiveScanJobId != scanJobId
                        ? new MoveScanAttemptResult(MoveScanAttemptOutcome.Superseded, null)
                        : ToAttemptResult(current.Status, current.LastError);
                }
            }
            else
            {
                var tracked = await db.MoveScanHandoffs.SingleOrDefaultAsync(
                    candidate => candidate.Id == handoffId,
                    cancellationToken);
                if (tracked == null
                    || tracked.AttemptGeneration != attemptGeneration
                    || tracked.ActiveScanJobId != scanJobId)
                {
                    return new MoveScanAttemptResult(MoveScanAttemptOutcome.Superseded, null);
                }
                if (tracked.Status != MoveScanHandoffStatus.Claimed)
                {
                    return ToAttemptResult(tracked.Status, tracked.LastError);
                }
                if (tracked.LeaseExpiresAt == null
                    || tracked.LeaseExpiresAt <= nowUtc)
                {
                    return new MoveScanAttemptResult(
                        MoveScanAttemptOutcome.Superseded,
                        null);
                }

                tracked.Status = terminalStatus;
                tracked.LastError = terminalError;
                tracked.LeaseOwner = null;
                tracked.LeaseExpiresAt = null;
                tracked.NextAttemptAt = null;
                tracked.UpdatedAt = nowUtc;
            }

            var idempotencyKey = $"handoff:{handoffId:N}:attempt:{attemptGeneration}:terminal";
            if (!await db.History.AnyAsync(
                    history => history.IdempotencyKey == idempotencyKey,
                    cancellationToken))
            {
                var rootIdempotencyKey = $"move:{snapshot.MoveJobId:N}:moved";
                var parentEventId = await db.History
                    .Where(history => history.IdempotencyKey == rootIdempotencyKey)
                    .Select(history => (int?)history.Id)
                    .SingleOrDefaultAsync(cancellationToken);
                db.History.Add(new History
                {
                    AudiobookId = snapshot.AudiobookId,
                    EventType = outcome switch
                    {
                        MoveScanTerminalOutcome.Succeeded => HistoryEvents.ScanCompleted,
                        MoveScanTerminalOutcome.Failed => HistoryEvents.ScanFailed,
                        MoveScanTerminalOutcome.Superseded => HistoryEvents.FileSkipped,
                        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
                    },
                    Outcome = outcome switch
                    {
                        MoveScanTerminalOutcome.Succeeded => HistoryOutcome.Succeeded,
                        MoveScanTerminalOutcome.Failed => HistoryOutcome.Failed,
                        MoveScanTerminalOutcome.Superseded => HistoryOutcome.Skipped,
                        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
                    },
                    Source = "LibraryScan",
                    Message = outcome switch
                    {
                        MoveScanTerminalOutcome.Succeeded => $"Library scan completed: {found} found, {created} created",
                        MoveScanTerminalOutcome.Failed => "Post-move library scan failed",
                        MoveScanTerminalOutcome.Superseded => "Post-move library scan was superseded by a newer audiobook destination",
                        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
                    },
                    Error = terminalError,
                    Timestamp = nowUtc,
                    CorrelationId = $"move:{snapshot.MoveJobId:N}",
                    IdempotencyKey = idempotencyKey,
                    ParentEventId = parentEventId,
                    Data = JsonSerializer.Serialize(new
                    {
                        HandoffId = handoffId,
                        AttemptGeneration = attemptGeneration,
                        snapshot.ActiveScanJobId,
                        Found = found,
                        Created = created,
                        Path = scanPath
                    })
                });
            }

            await db.SaveChangesAsync(cancellationToken);
            if (transaction != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await transaction.CommitAsync(CancellationToken.None);
            }

            return new MoveScanAttemptResult(
                outcome switch
                {
                    MoveScanTerminalOutcome.Succeeded => MoveScanAttemptOutcome.Completed,
                    MoveScanTerminalOutcome.Failed => MoveScanAttemptOutcome.Failed,
                    MoveScanTerminalOutcome.Superseded => MoveScanAttemptOutcome.Superseded,
                    _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
                },
                terminalError);
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            throw new PersistenceException("Failed to commit a move scan attempt.", exception);
        }
    }

    private static MoveScanAttemptResult ToAttemptResult(
        MoveScanHandoffStatus status,
        string? error) =>
        status switch
        {
            MoveScanHandoffStatus.Succeeded => new MoveScanAttemptResult(
                MoveScanAttemptOutcome.Completed,
                null),
            MoveScanHandoffStatus.Failed => new MoveScanAttemptResult(
                MoveScanAttemptOutcome.Failed,
                error),
            MoveScanHandoffStatus.Superseded => new MoveScanAttemptResult(
                MoveScanAttemptOutcome.Superseded,
                error),
            _ => new MoveScanAttemptResult(MoveScanAttemptOutcome.Superseded, null)
        };
}
