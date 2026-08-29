using Listenarr.Application.Common;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Jobs;

public partial class MoveQueueService
{
    public async Task<Guid?> RequeueMoveAsync(
        Guid jobId,
        CancellationToken requestCancellationToken = default)
    {
        await EnsureIdentityKeysReconciledAsync(requestCancellationToken);
        MoveJob? jobToSchedule = null;
        MoveJob? jobToNotify = null;
        var result = await _mutationCoordinator.ExecuteExclusiveAsync<Guid?>(async cancellationToken =>
        {
            MoveJob? job;
            try
            {
                job = await _persistence.GetByIdAsync(jobId, cancellationToken);
            }
            catch (Exception ex) when (WorkerExceptionClassifier.IsNonFatal(ex))
            {
                _logger.LogWarning(ex, "Failed to read move job from DB while requeueing {JobId}", jobId);
                return null;
            }

            if (job == null)
            {
                _logger.LogWarning("Attempted to requeue unknown move job {JobId}", jobId);
                return null;
            }

            if (!CanRequeueJobStatus(job.Status))
            {
                _logger.LogInformation("Move job {JobId} has status {Status} and cannot be requeued", jobId, job.Status);
                return null;
            }

            await EnsureNonRelocationRecoveryAllowsMutationAsync(
                job.AudiobookId,
                allowActiveDeletionIntent: false,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(job.SourcePath)
                || string.IsNullOrWhiteSpace(job.RequestedPath))
            {
                _logger.LogWarning(
                    "Move job {JobId} has no complete source and target paths and cannot be requeued safely",
                    jobId);
                return null;
            }

            var sourcePath = job.SourcePath;
            PathIdentitySnapshot sourceIdentity;
            if (!job.TryGetSourceIdentity(out sourceIdentity))
            {
                if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    sourcePath,
                    out sourcePath,
                    out var sourceReason))
                {
                    if (await MarkUnsafeStoredPathNeedsAttentionAsync(
                            job,
                            $"Move source path cannot be requeued safely: {sourceReason}",
                            cancellationToken))
                    {
                        jobToNotify = job;
                    }
                    return null;
                }

                sourceIdentity = await ResolveIdentitySnapshotAsync(
                    sourcePath,
                    cancellationToken: cancellationToken);
            }
            else if (!FileSystemPathIdentity.TryCanonicalizeStoredPathWithIdentityForHost(
                sourcePath,
                sourceIdentity,
                out sourcePath,
                out var sourceReason))
            {
                if (await MarkUnsafeStoredPathNeedsAttentionAsync(
                        job,
                        $"Move source path cannot be requeued safely: {sourceReason}",
                        cancellationToken))
                {
                    jobToNotify = job;
                }
                return null;
            }

            var targetPath = job.RequestedPath;
            PathIdentitySnapshot targetIdentity;
            if (!job.TryGetTargetIdentity(out targetIdentity))
            {
                if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    targetPath,
                    out targetPath,
                    out var targetReason))
                {
                    if (await MarkUnsafeStoredPathNeedsAttentionAsync(
                            job,
                            $"Move target path cannot be requeued safely: {targetReason}",
                            cancellationToken))
                    {
                        jobToNotify = job;
                    }
                    return null;
                }

                targetIdentity = await ResolveIdentitySnapshotAsync(
                    targetPath,
                    cancellationToken: cancellationToken);
            }
            else if (!FileSystemPathIdentity.TryCanonicalizeStoredPathWithIdentityForHost(
                targetPath,
                targetIdentity,
                out targetPath,
                out var targetReason))
            {
                if (await MarkUnsafeStoredPathNeedsAttentionAsync(
                        job,
                        $"Move target path cannot be requeued safely: {targetReason}",
                        cancellationToken))
                {
                    jobToNotify = job;
                }
                return null;
            }

            await ThrowIfRelocationBoundaryProtectedAsync(
                sourcePath,
                sourceIdentity,
                targetPath,
                targetIdentity,
                cancellationToken);

            if (FileSystemPathIdentity.AreEquivalentEndpoints(
                    sourcePath,
                    sourceIdentity,
                    targetPath,
                    targetIdentity))
            {
                _logger.LogInformation(
                    "Move job {JobId} cannot be manually requeued because its source and target endpoints are identical",
                    job.Id);
                return null;
            }

            if (job.Entries.Count == 0
                || job.Entries.All(entry => entry.EntryType != MoveJobEntryType.File))
            {
                await MarkUnsafeStoredPathNeedsAttentionAsync(
                    job,
                    "The move job has no persisted tracked-file source manifest and cannot be requeued safely.",
                    cancellationToken);
                jobToNotify = job;
                return null;
            }
            if (job.Status != MoveJobStatus.Queued
                && MoveRecoveryPolicy.GetDisposition(job) != MoveRecoveryDisposition.RetryAvailable)
            {
                _logger.LogInformation(
                    "Move job {JobId} requires operator repair and cannot be manually requeued",
                    jobId);
                return null;
            }

            if (!MoveBoundaryAuthorization.TryResolveSourceBoundary(
                    sourcePath,
                    sourceIdentity,
                    job.SourceCleanupBoundary,
                    job.DeleteEmptySource,
                    out _,
                    out var sourceBoundaryReason))
            {
                if (await MarkUnsafeStoredPathNeedsAttentionAsync(
                        job,
                        $"Move source mutation boundary cannot be requeued safely: {sourceBoundaryReason}",
                        cancellationToken))
                {
                    jobToNotify = job;
                }
                return null;
            }

            if (!MoveManifestIdentity.TryGetSourceBoundaryAuthorization(
                    job.Entries,
                    out _,
                    out _,
                    out _)
                || !MoveManifestIdentity.TryGetTargetBoundaryAuthorization(
                    job.Entries,
                    out _,
                    out _,
                    out _))
            {
                await MarkUnsafeStoredPathNeedsAttentionAsync(
                    job,
                    "The move job has no durable source- or target-boundary physical-generation authorization and cannot be requeued safely.",
                    cancellationToken);
                jobToNotify = job;
                return null;
            }

            var deduplicationKey = MoveManifestIdentity.CreateDeduplicationKey(
                job.AudiobookId,
                sourcePath,
                sourceIdentity,
                targetPath,
                targetIdentity,
                job.Entries);
            cancellationToken.ThrowIfCancellationRequested();
            var requeue = await _persistence.RequeueAsync(
                new RequeueMoveCommand(
                    job.Id,
                    job.Status,
                    sourcePath,
                    sourceIdentity,
                    targetPath,
                    targetIdentity,
                    deduplicationKey,
                    _timeProvider.GetUtcNow()),
                CancellationToken.None);
            switch (requeue.Outcome)
            {
                case MoveRequeueOutcome.Requeued:
                case MoveRequeueOutcome.AlreadyQueuedWithMatchingIdentity:
                    jobToSchedule = requeue.Job;
                    _logger.LogInformation(
                        "Requeued move job {JobId} for audiobook {AudiobookId}",
                        jobId,
                        job.AudiobookId);
                    return requeue.Job?.Id;
                case MoveRequeueOutcome.ConflictingActiveJob:
                    if (requeue.Job == null
                        || !ActiveExecutionOptionsMatch(
                            requeue.Job,
                            sourceIdentity,
                            targetIdentity,
                            job.DeleteEmptySource,
                            job.RelocationId,
                            job.SourceCleanupBoundary,
                            job.SourceCleanupMode,
                            job.SourceRootFolderId,
                            job.SourcePolicyRevision,
                            job.TargetRootFolderId,
                            job.TargetPolicyRevision,
                            job.SourceStorageContractRevision,
                            job.TargetStorageContractRevision,
                            job.ForceCopyAndRetainSource))
                    {
                        _logger.LogWarning(
                            "Move job {JobId} cannot be requeued because the conflicting active job uses different execution options",
                            job.Id);
                        return null;
                    }

                    jobToSchedule = requeue.Job;
                    return requeue.Job.Id;
                case MoveRequeueOutcome.StaleState:
                    _logger.LogInformation(
                        "Move job {JobId} changed state while it was being requeued",
                        jobId);
                    return null;
                case MoveRequeueOutcome.NotFound:
                    _logger.LogWarning("Move job {JobId} disappeared while it was being requeued", jobId);
                    return null;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported move requeue outcome {requeue.Outcome}.");
            }
        }, requestCancellationToken);

        if (jobToSchedule != null)
        {
            await ScheduleAsync(jobToSchedule);
            await NotifyCommittedJobStateAsync(
                jobToSchedule.Id,
                jobToSchedule.Status,
                jobToSchedule.Error,
                CancellationToken.None);
        }
        else if (jobToNotify != null)
        {
            await NotifyCommittedJobStateAsync(
                jobToNotify.Id,
                jobToNotify.Status,
                jobToNotify.Error,
                CancellationToken.None);
        }

        return result;
    }

    private async Task ScheduleAsync(MoveJob job, CancellationToken cancellationToken = default)
    {
        await _channel.Writer.WriteAsync(job, cancellationToken);
    }

    private static bool CanRequeueJobStatus(MoveJobStatus status) =>
        status is MoveJobStatus.Failed or
            MoveJobStatus.NeedsAttention or
            MoveJobStatus.Queued;

    private async Task<bool> MarkUnsafeStoredPathNeedsAttentionAsync(
        MoveJob job,
        string error,
        CancellationToken cancellationToken)
    {
        var updated = await _persistence.MarkNeedsAttentionAsync(
            job.Id,
            job.Status,
            error,
            _timeProvider.GetUtcNow(),
            cancellationToken);
        if (!updated)
        {
            _logger.LogInformation(
                "Move job {JobId} changed state while an unsafe persisted path was being rejected",
                job.Id);
            return false;
        }

        job.Status = MoveJobStatus.NeedsAttention;
        job.Error = error;
        job.FailureKind = MoveFailureKind.Verification;
        job.ActiveDeduplicationKey = null;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        _logger.LogWarning("Move job {JobId} requires attention: {Error}", job.Id, error);
        return true;
    }

    private async Task<PathIdentitySnapshot> ResolveIdentitySnapshotAsync(
        string path,
        FileSystemCaseSensitivityMode mode = FileSystemCaseSensitivityMode.Auto,
        CancellationToken cancellationToken = default)
    {
        var absolutePath = FileSystemPathIdentity.ResolveNativeAbsolutePath(path);
        var resolution = await _semanticsResolver.ResolveAsync(
            absolutePath,
            mode,
            cancellationToken);
        if (resolution.State != PathIdentityState.Valid)
        {
            throw new InvalidOperationException(
                resolution.Reason ?? "Filesystem identity is unavailable.");
        }

        return PathIdentitySnapshot.FromResolution(
            resolution.Semantics,
            mode,
            resolution.BoundaryPath,
            absolutePath);
    }

    private async Task EnsureIdentityKeysReconciledAsync(CancellationToken cancellationToken)
    {
        if (_identityKeysReconciled)
        {
            return;
        }

        await _identityReconciliationGate.WaitAsync(cancellationToken);
        try
        {
            if (_identityKeysReconciled)
            {
                return;
            }

            await _persistence.ReconcileIdentityKeysAsync(cancellationToken);
            _identityKeysReconciled = true;
        }
        finally
        {
            _identityReconciliationGate.Release();
        }
    }

    private async Task ThrowIfRelocationBoundaryProtectedAsync(
        string sourcePath,
        PathIdentitySnapshot sourceIdentity,
        string targetPath,
        PathIdentitySnapshot targetIdentity,
        CancellationToken cancellationToken = default)
    {
        await ThrowIfEndpointProtectedAsync(
            targetPath,
            targetIdentity,
            "target",
            cancellationToken);
        await ThrowIfEndpointProtectedAsync(
            sourcePath,
            sourceIdentity,
            "source",
            cancellationToken);
    }

    private async Task ThrowIfEndpointProtectedAsync(
        string path,
        PathIdentitySnapshot identity,
        string endpoint,
        CancellationToken cancellationToken)
    {
        identity.ValidateForPath(path);
        if (await _relocationService.IsBoundaryProtectedAsync(
            path,
            identity.Semantics,
            cancellationToken))
        {
            throw new MoveRelocationConflictException(
                $"Move {endpoint} overlaps an active root folder relocation boundary.");
        }
    }

    private async Task<bool> PersistWithRetryAsync(
        Func<Task<bool>> operation,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (PersistenceException) when (attempt < maxAttempts)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(100 * attempt * attempt),
                    cancellationToken);
            }
        }
    }
}

public sealed class MoveRelocationConflictException(string message) : InvalidOperationException(message);
