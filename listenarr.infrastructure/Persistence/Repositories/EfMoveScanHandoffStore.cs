using System.Data.Common;
using System.Text.Json;
using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories;

public sealed partial class EfMoveScanHandoffStore(
    IDbContextFactory<ListenArrDbContext> dbFactory) : IMoveScanHandoffStore
{
    public Task<MoveCompletionCommitResult> CommitMoveCompletionAsync(
        MoveCompletionCommit command,
        CancellationToken cancellationToken = default) =>
        CommitMoveCompletionCoreAsync(
            command,
            commitValidation: null,
            cancellationToken);

    public Task<MoveCompletionCommitResult> CommitMoveCompletionAsync(
        MoveCompletionCommit command,
        Func<CancellationToken, Task<RegistrationPublicationMatchOutcome>>
            commitValidation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commitValidation);
        return CommitMoveCompletionCoreAsync(
            command,
            commitValidation,
            cancellationToken);
    }

    private async Task<MoveCompletionCommitResult> CommitMoveCompletionCoreAsync(
        MoveCompletionCommit command,
        Func<CancellationToken, Task<RegistrationPublicationMatchOutcome>>?
            commitValidation,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(cancellationToken)
                : null;
            var now = command.Now.UtcDateTime;
            var correlationId = $"move:{command.MoveJobId:N}";
            var moveIdempotencyKey = $"move:{command.MoveJobId:N}:moved";
            var job = await db.MoveJobs.SingleOrDefaultAsync(
                candidate => candidate.Id == command.MoveJobId,
                cancellationToken);
            if (job?.Status == MoveJobStatus.Completed)
            {
                var committedHistory = await db.History.AsNoTracking()
                    .SingleOrDefaultAsync(
                        history => history.IdempotencyKey == moveIdempotencyKey,
                        cancellationToken);
                var committedHandoff = await db.MoveScanHandoffs.AsNoTracking()
                    .SingleOrDefaultAsync(
                        candidate => candidate.MoveJobId == command.MoveJobId,
                        cancellationToken);
                if (committedHistory == null
                    || committedHandoff == null
                    || committedHandoff.AudiobookId != command.AudiobookId
                    || !string.Equals(
                        committedHandoff.TargetPath,
                        command.Target,
                        StringComparison.Ordinal))
                {
                    throw new MoveNeedsAttentionException(
                        "The move is marked completed, but its durable completion records are missing or inconsistent.");
                }

                return new MoveCompletionCommitResult(
                    committedHandoff,
                    committedHistory,
                    MoveHistoryCreated: false,
                    HandoffCreated: false);
            }

            if (job == null
                || job.Status != MoveJobStatus.Running
                || job.LeaseOwner != command.LeaseOwner
                || job.LeaseGeneration != command.LeaseGeneration
                || job.LeaseExpiresAt == null
                || job.LeaseExpiresAt <= now)
            {
                throw new MoveLeaseLostException(command.MoveJobId, command.LeaseGeneration);
            }

            var moveHistory = await db.History.SingleOrDefaultAsync(
                history => history.IdempotencyKey == moveIdempotencyKey,
                cancellationToken);
            var moveHistoryCreated = moveHistory == null;
            if (moveHistory == null)
            {
                moveHistory = new History
                {
                    AudiobookId = command.AudiobookId,
                    AudiobookTitle = command.AudiobookTitle,
                    SourceTitle = command.AudiobookTitle,
                    EventType = "Moved",
                    Outcome = HistoryOutcome.Succeeded,
                    Source = "Move",
                    Message = $"Moved audiobook files from {command.Source} to {command.Target}",
                    Timestamp = now,
                    CorrelationId = correlationId,
                    IdempotencyKey = moveIdempotencyKey,
                    Data = JsonSerializer.Serialize(new
                    {
                        JobId = command.MoveJobId,
                        Source = command.Source,
                        Target = command.Target
                    })
                };
                db.History.Add(moveHistory);
            }

            var handoff = await db.MoveScanHandoffs.SingleOrDefaultAsync(
                candidate => candidate.MoveJobId == command.MoveJobId,
                cancellationToken);
            var handoffCreated = handoff == null;
            if (handoff == null)
            {
                handoff = new MoveScanHandoff
                {
                    MoveJobId = command.MoveJobId,
                    AudiobookId = command.AudiobookId,
                    TargetPath = command.Target,
                    Status = MoveScanHandoffStatus.Pending,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.MoveScanHandoffs.Add(handoff);
            }
            else if (handoff.Status is not (MoveScanHandoffStatus.Succeeded or MoveScanHandoffStatus.Failed))
            {
                handoff.AudiobookId = command.AudiobookId;
                handoff.TargetPath = command.Target;
                handoff.UpdatedAt = now;
            }

            if (db.Database.IsRelational())
            {
                await db.MoveJobCreatedDirectories
                    .Where(directory => directory.MoveJobId == command.MoveJobId
                        && directory.State == MoveCreatedDirectoryState.Created)
                    .ExecuteUpdateAsync(
                        updates => updates.SetProperty(
                            directory => directory.State,
                            MoveCreatedDirectoryState.Retained),
                        cancellationToken);
            }
            else
            {
                var createdDirectories = await db.MoveJobCreatedDirectories
                    .Where(directory => directory.MoveJobId == command.MoveJobId
                        && directory.State == MoveCreatedDirectoryState.Created)
                    .ToListAsync(cancellationToken);
                foreach (var directory in createdDirectories)
                {
                    directory.State = MoveCreatedDirectoryState.Retained;
                }
            }

            job.Status = MoveJobStatus.Completed;
            if (job.Phase < MoveJobPhase.RecordingCompletion)
            {
                job.Phase = MoveJobPhase.RecordingCompletion;
            }
            job.Error = null;
            job.FailureKind = MoveFailureKind.None;
            job.NextAttemptAt = null;
            job.ActiveDeduplicationKey = null;
            job.LeaseOwner = null;
            job.LeaseExpiresAt = null;
            job.UpdatedAt = now;

            if (commitValidation != null && transaction == null)
            {
                var validation = await commitValidation(CancellationToken.None);
                if (validation == RegistrationPublicationMatchOutcome.Unavailable)
                {
                    throw new IOException(
                        "The finalized move target is temporarily unavailable while durable completion is being committed.");
                }
                if (validation != RegistrationPublicationMatchOutcome.Match)
                {
                    throw new MoveNeedsAttentionException(
                        "The finalized move target changed before durable completion could be committed.");
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            if (commitValidation != null && transaction != null)
            {
                var validation = await commitValidation(CancellationToken.None);
                if (validation == RegistrationPublicationMatchOutcome.Unavailable)
                {
                    if (transaction != null)
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                    }
                    throw new IOException(
                        "The finalized move target is temporarily unavailable while durable completion is being committed.");
                }
                if (validation != RegistrationPublicationMatchOutcome.Match)
                {
                    if (transaction != null)
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                    }
                    throw new MoveNeedsAttentionException(
                        "The finalized move target changed before durable completion could be committed.");
                }
            }
            if (transaction != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await transaction.CommitAsync(CancellationToken.None);
            }

            return new MoveCompletionCommitResult(
                handoff,
                moveHistory,
                moveHistoryCreated,
                handoffCreated);
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            throw new PersistenceException("Failed to commit move completion and its scan handoff.", exception);
        }
    }

    public async Task<IReadOnlyList<Guid>> GetClaimableIdsAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var nowUtc = now.UtcDateTime;
            return await db.MoveScanHandoffs
                .AsNoTracking()
                .Where(handoff =>
                    (handoff.Status == MoveScanHandoffStatus.Pending
                        && (handoff.NextAttemptAt == null || handoff.NextAttemptAt <= nowUtc))
                    || (handoff.Status == MoveScanHandoffStatus.Claimed
                        && (handoff.LeaseExpiresAt == null || handoff.LeaseExpiresAt <= nowUtc)))
                .OrderBy(handoff => handoff.CreatedAt)
                .Select(handoff => handoff.Id)
                .Take(Math.Clamp(limit, 1, 500))
                .ToListAsync(cancellationToken);
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            throw new PersistenceException("Failed to query claimable move scan handoffs.", exception);
        }
    }

    public async Task<MoveScanHandoffClaim?> TryClaimAsync(
        Guid handoffId,
        string leaseOwner,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var nowUtc = now.UtcDateTime;
            var leaseExpiresAtUtc = leaseExpiresAt.UtcDateTime;
            if (db.Database.IsRelational())
            {
                var affected = await db.MoveScanHandoffs
                    .Where(handoff => handoff.Id == handoffId
                        && ((handoff.Status == MoveScanHandoffStatus.Pending
                                && (handoff.NextAttemptAt == null || handoff.NextAttemptAt <= nowUtc))
                            || (handoff.Status == MoveScanHandoffStatus.Claimed
                                && (handoff.LeaseExpiresAt == null || handoff.LeaseExpiresAt <= nowUtc))))
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(handoff => handoff.Status, MoveScanHandoffStatus.Claimed)
                        .SetProperty(handoff => handoff.LeaseOwner, leaseOwner)
                        .SetProperty(handoff => handoff.LeaseGeneration, handoff => handoff.LeaseGeneration + 1)
                        .SetProperty(handoff => handoff.AttemptGeneration, handoff => handoff.AttemptGeneration + 1)
                        .SetProperty(handoff => handoff.LeaseExpiresAt, leaseExpiresAtUtc)
                        .SetProperty(handoff => handoff.NextAttemptAt, (DateTime?)null)
                        .SetProperty(handoff => handoff.ActiveScanJobId, (Guid?)null)
                        .SetProperty(handoff => handoff.UpdatedAt, nowUtc),
                        cancellationToken);
                if (affected != 1)
                {
                    return null;
                }
            }
            else
            {
                var handoff = await db.MoveScanHandoffs.SingleOrDefaultAsync(candidate =>
                    candidate.Id == handoffId
                    && ((candidate.Status == MoveScanHandoffStatus.Pending
                            && (candidate.NextAttemptAt == null || candidate.NextAttemptAt <= nowUtc))
                        || (candidate.Status == MoveScanHandoffStatus.Claimed
                            && (candidate.LeaseExpiresAt == null || candidate.LeaseExpiresAt <= nowUtc))),
                    cancellationToken);
                if (handoff == null)
                {
                    return null;
                }

                handoff.Status = MoveScanHandoffStatus.Claimed;
                handoff.LeaseOwner = leaseOwner;
                handoff.LeaseGeneration++;
                handoff.AttemptGeneration++;
                handoff.LeaseExpiresAt = leaseExpiresAtUtc;
                handoff.NextAttemptAt = null;
                handoff.ActiveScanJobId = null;
                handoff.UpdatedAt = nowUtc;
                await db.SaveChangesAsync(cancellationToken);
            }

            var claimed = await db.MoveScanHandoffs
                .AsNoTracking()
                .Where(handoff => handoff.Id == handoffId
                    && handoff.Status == MoveScanHandoffStatus.Claimed
                    && handoff.LeaseOwner == leaseOwner)
                .Select(handoff => new
                {
                    handoff.Id,
                    handoff.MoveJobId,
                    handoff.AudiobookId,
                    handoff.TargetPath,
                    handoff.AttemptGeneration,
                    handoff.LeaseOwner,
                    handoff.LeaseGeneration,
                    handoff.MoveJob.TargetPathSyntax,
                    handoff.MoveJob.TargetCaseSensitivity,
                    handoff.MoveJob.TargetCaseSensitivityMode,
                    handoff.MoveJob.TargetIdentityBoundary
                })
                .SingleOrDefaultAsync(cancellationToken);
            if (claimed == null)
            {
                return null;
            }

            if (!claimed.TargetPathSyntax.HasValue
                || !claimed.TargetCaseSensitivity.HasValue
                || !claimed.TargetCaseSensitivityMode.HasValue
                || string.IsNullOrWhiteSpace(claimed.TargetIdentityBoundary))
            {
                await FailUndispatchableClaimAsync(
                    claimed.Id,
                    claimed.AttemptGeneration,
                    claimed.TargetPath,
                    "The completed move has no authoritative target filesystem identity.",
                    now,
                    cancellationToken);
                return null;
            }

            var targetIdentity = new PathIdentitySnapshot(
                claimed.TargetPathSyntax.Value,
                claimed.TargetCaseSensitivity.Value,
                claimed.TargetCaseSensitivityMode.Value,
                claimed.TargetIdentityBoundary);
            try
            {
                targetIdentity.ValidateForPath(claimed.TargetPath);
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
            {
                await FailUndispatchableClaimAsync(
                    claimed.Id,
                    claimed.AttemptGeneration,
                    claimed.TargetPath,
                    $"The completed move target filesystem identity is invalid: {exception.Message}",
                    now,
                    cancellationToken);
                return null;
            }
            var persistedEntries = await db.MoveJobEntries
                .AsNoTracking()
                .Where(entry => entry.MoveJobId == claimed.MoveJobId)
                .OrderBy(entry => entry.Id)
                .ToListAsync(cancellationToken);
            var targetManifest = persistedEntries
                .Where(entry =>
                    !MoveManifestIdentity.IsBoundaryAuthorization(entry))
                .ToList();
            if (!targetManifest.Any(entry =>
                    entry.EntryType == MoveJobEntryType.File))
            {
                await FailUndispatchableClaimAsync(
                    claimed.Id,
                    claimed.AttemptGeneration,
                    claimed.TargetPath,
                    "The completed move has no durable target manifest with tracked-file evidence.",
                    now,
                    cancellationToken);
                return null;
            }

            return new MoveScanHandoffClaim(
                claimed.Id,
                claimed.MoveJobId,
                claimed.AudiobookId,
                claimed.TargetPath,
                targetIdentity,
                targetManifest,
                claimed.AttemptGeneration,
                claimed.LeaseOwner!,
                claimed.LeaseGeneration);
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            throw new PersistenceException("Failed to claim a move scan handoff.", exception);
        }
    }

    private async Task FailUndispatchableClaimAsync(
        Guid handoffId,
        int attemptGeneration,
        string targetPath,
        string error,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var completion = await CompleteAttemptAsync(
            handoffId,
            attemptGeneration,
            scanJobId: null,
            MoveScanTerminalOutcome.Failed,
            error,
            found: 0,
            created: 0,
            scanPath: targetPath,
            now,
            cancellationToken);
        if (completion.Outcome != MoveScanAttemptOutcome.Failed)
        {
            throw new InvalidOperationException(
                "The undispatchable move scan handoff changed state before it could be failed safely.");
        }
    }

    private static bool IsProviderFailure(Exception exception)
    {
        if (exception is PersistenceException)
        {
            return false;
        }

        if (exception is DbException or DbUpdateException)
        {
            return true;
        }

        return exception.InnerException != null
            && IsProviderFailure(exception.InnerException);
    }

}
