using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories;

public sealed partial class EfMoveQueuePersistence
{
    public async Task<MoveRequeueResult> RequeueAsync(
        RequeueMoveCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.SourceIdentity.ValidateForPath(command.SourcePath);
        command.TargetIdentity.ValidateForPath(command.TargetPath);

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var current = await db.MoveJobs
                .AsNoTracking()
                .Include(job => job.Entries)
                .SingleOrDefaultAsync(job => job.Id == command.JobId, cancellationToken);
            if (current == null)
            {
                return new MoveRequeueResult(MoveRequeueOutcome.NotFound);
            }

            if (current.Status != command.ExpectedStatus)
            {
                return new MoveRequeueResult(MoveRequeueOutcome.StaleState, current);
            }

            var conflicting = await db.MoveJobs
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    job => job.Id != command.JobId
                        && job.ActiveDeduplicationKey == command.DeduplicationKey,
                    cancellationToken);
            if (conflicting != null)
            {
                return new MoveRequeueResult(
                    MoveRequeueOutcome.ConflictingActiveJob,
                    conflicting);
            }

            var wasMatchingQueuedRepair = IsMatchingQueuedRepair(current, command);

            if (!db.Database.IsRelational())
            {
                var tracked = await db.MoveJobs
                    .Include(job => job.CreatedDirectories)
                    .SingleOrDefaultAsync(
                        job => job.Id == command.JobId
                            && job.Status == command.ExpectedStatus,
                        cancellationToken);
                if (tracked == null)
                {
                    return new MoveRequeueResult(MoveRequeueOutcome.StaleState);
                }

                ResetRemovedTargetScaffolding(tracked);
                ApplyRequeue(tracked, command);
                await db.SaveChangesAsync(cancellationToken);
            }
            else
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var affected = await db.MoveJobs
                    .Where(job => job.Id == command.JobId
                        && job.Status == command.ExpectedStatus)
                    .ExecuteUpdateAsync(
                        updates => updates
                            .SetProperty(job => job.SourcePath, command.SourcePath)
                            .SetProperty(job => job.RequestedPath, command.TargetPath)
                            .SetProperty(job => job.SourcePathSyntax, command.SourceIdentity.Syntax)
                            .SetProperty(job => job.SourceCaseSensitivity, command.SourceIdentity.CaseSensitivity)
                            .SetProperty(job => job.SourceCaseSensitivityMode, command.SourceIdentity.RequestedMode)
                            .SetProperty(job => job.SourceIdentityBoundary, command.SourceIdentity.BoundaryPath)
                            .SetProperty(job => job.TargetPathSyntax, command.TargetIdentity.Syntax)
                            .SetProperty(job => job.TargetCaseSensitivity, command.TargetIdentity.CaseSensitivity)
                            .SetProperty(job => job.TargetCaseSensitivityMode, command.TargetIdentity.RequestedMode)
                            .SetProperty(job => job.TargetIdentityBoundary, command.TargetIdentity.BoundaryPath)
                            .SetProperty(job => job.IdentityKeyVersion, MoveManifestIdentity.Version)
                            .SetProperty(job => job.ActiveDeduplicationKey, command.DeduplicationKey)
                            .SetProperty(job => job.Status, MoveJobStatus.Queued)
                            .SetProperty(job => job.FailureKind, MoveFailureKind.None)
                            .SetProperty(job => job.AttemptCount, 0)
                            .SetProperty(job => job.Error, (string?)null)
                            .SetProperty(job => job.NextAttemptAt, (DateTime?)null)
                            .SetProperty(job => job.LeaseOwner, (string?)null)
                            .SetProperty(job => job.LeaseExpiresAt, (DateTime?)null)
                            .SetProperty(job => job.UpdatedAt, command.UpdatedAt.UtcDateTime),
                        cancellationToken);
                if (affected != 1)
                {
                    var exists = await db.MoveJobs
                        .AsNoTracking()
                        .AnyAsync(job => job.Id == command.JobId, cancellationToken);
                    return new MoveRequeueResult(
                        exists ? MoveRequeueOutcome.StaleState : MoveRequeueOutcome.NotFound);
                }

                _ = await db.MoveJobCreatedDirectories
                    .Where(directory => directory.MoveJobId == command.JobId
                        && directory.State == MoveCreatedDirectoryState.Removed)
                    .ExecuteUpdateAsync(
                        updates => updates
                            .SetProperty(
                                directory => directory.State,
                                MoveCreatedDirectoryState.Planned)
                            .SetProperty(
                                directory => directory.DirectoryObjectIdentity,
                                (string?)null),
                        cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                await transaction.CommitAsync(CancellationToken.None);
            }

            var repaired = await db.MoveJobs
                .AsNoTracking()
                .Include(job => job.Entries)
                .SingleAsync(job => job.Id == command.JobId, cancellationToken);
            if (!IsMatchingQueuedRepair(repaired, command))
            {
                return new MoveRequeueResult(MoveRequeueOutcome.StaleState, repaired);
            }

            return new MoveRequeueResult(
                wasMatchingQueuedRepair
                    ? MoveRequeueOutcome.AlreadyQueuedWithMatchingIdentity
                    : MoveRequeueOutcome.Requeued,
                repaired);
        }
        catch (Exception exception) when (exception is DbException or DbUpdateException or UniqueConstraintViolationException)
        {
            var conflicting = await TryGetConflictingJobAsync(command, cancellationToken);
            if (conflicting != null)
            {
                return new MoveRequeueResult(
                    MoveRequeueOutcome.ConflictingActiveJob,
                    conflicting);
            }

            throw new PersistenceException("Failed to requeue move job persistence.", exception);
        }
    }

    private async Task<MoveJob?> TryGetConflictingJobAsync(
        RequeueMoveCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            return await db.MoveJobs
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    job => job.Id != command.JobId
                        && job.ActiveDeduplicationKey == command.DeduplicationKey,
                    cancellationToken);
        }
        catch (Exception exception) when (exception is DbException or DbUpdateException)
        {
            return null;
        }
    }

    private static bool IsMatchingQueuedRepair(
        MoveJob job,
        RequeueMoveCommand command) =>
        job.Status == MoveJobStatus.Queued
        && job.SourcePath == command.SourcePath
        && job.RequestedPath == command.TargetPath
        && job.SourcePathSyntax == command.SourceIdentity.Syntax
        && job.SourceCaseSensitivity == command.SourceIdentity.CaseSensitivity
        && job.SourceCaseSensitivityMode == command.SourceIdentity.RequestedMode
        && job.SourceIdentityBoundary == command.SourceIdentity.BoundaryPath
        && job.TargetPathSyntax == command.TargetIdentity.Syntax
        && job.TargetCaseSensitivity == command.TargetIdentity.CaseSensitivity
        && job.TargetCaseSensitivityMode == command.TargetIdentity.RequestedMode
        && job.TargetIdentityBoundary == command.TargetIdentity.BoundaryPath
        && job.IdentityKeyVersion == MoveManifestIdentity.Version
        && job.ActiveDeduplicationKey == command.DeduplicationKey
        && job.FailureKind == MoveFailureKind.None
        && job.AttemptCount == 0
        && job.Error == null
        && job.NextAttemptAt == null
        && job.LeaseOwner == null
        && job.LeaseExpiresAt == null;

    private static void ResetRemovedTargetScaffolding(MoveJob job)
    {
        foreach (var directory in job.CreatedDirectories.Where(directory =>
                     directory.State == MoveCreatedDirectoryState.Removed))
        {
            directory.State = MoveCreatedDirectoryState.Planned;
            directory.DirectoryObjectIdentity = null;
        }
    }

    private static void ApplyRequeue(MoveJob job, RequeueMoveCommand command)
    {
        job.SourcePath = command.SourcePath;
        job.RequestedPath = command.TargetPath;
        job.SetSourceIdentity(command.SourceIdentity);
        job.SetTargetIdentity(command.TargetIdentity);
        job.IdentityKeyVersion = MoveManifestIdentity.Version;
        job.ActiveDeduplicationKey = command.DeduplicationKey;
        job.Status = MoveJobStatus.Queued;
        job.Error = null;
        job.FailureKind = MoveFailureKind.None;
        job.AttemptCount = 0;
        job.NextAttemptAt = null;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        job.UpdatedAt = command.UpdatedAt.UtcDateTime;
    }
}
