/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving
{
    internal partial class MoveJobProcessor
    {
        private async Task<PathIdentitySnapshot> GetRequiredIdentityAsync(
            MoveJob job,
            string path,
            bool target,
            CancellationToken cancellationToken)
        {
            var hasIdentity = target
                ? job.TryGetTargetIdentity(out var identity)
                : job.TryGetSourceIdentity(out identity);
            if (!hasIdentity)
            {
                throw new MoveNeedsAttentionException(
                    $"The move job has no authoritative {(target ? "target" : "source")} filesystem identity.");
            }

            try
            {
                identity.ValidateForPath(path);
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
            {
                throw new MoveNeedsAttentionException(
                    $"The persisted {(target ? "target" : "source")} filesystem identity is invalid: {exception.Message}");
            }

            if (identity.RequestedMode == FileSystemCaseSensitivityMode.Auto)
            {
                var current = await semanticsResolver.ResolveAsync(
                    identity.BoundaryPath,
                    FileSystemCaseSensitivityMode.Auto,
                    cancellationToken);
                if (current.State != PathIdentityState.Valid
                    || current.Semantics.Syntax != identity.Syntax
                    || current.Semantics.CaseSensitivity != identity.CaseSensitivity)
                {
                    throw new MoveNeedsAttentionException(
                        $"The {(target ? "target" : "source")} filesystem identity changed after the move was queued.");
                }
            }

            return identity;
        }

        private static MoveLeaseToken CreateLeaseToken(MoveJob job)
        {
            if (string.IsNullOrWhiteSpace(job.LeaseOwner) || job.LeaseGeneration <= 0)
            {
                throw new MoveLeaseLostException(job.Id, job.LeaseGeneration);
            }

            return new MoveLeaseToken(job.LeaseOwner, job.LeaseGeneration);
        }

        private async Task UpdateJobStatusAsync(
            MoveJob job,
            MoveJobStatus status,
            string? error = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(job.LeaseOwner))
            {
                throw new MoveLeaseLostException(job.Id, job.LeaseGeneration);
            }

            await moveQueueService.UpdateJobStatusWithoutNotificationAsync(
                job.Id,
                job.LeaseOwner,
                job.LeaseGeneration,
                status,
                error,
                cancellationToken);
            job.Status = status;
            job.Error = error;
        }

        private async Task<FinalizedMoveRecoveryOutcome> TryRecoverFinalizedMoveAsync(
            MoveJob job,
            Audiobook audiobook,
            string? source,
            string target,
            FileSystemPathSemantics? sourceSemantics,
            FileSystemPathSemantics targetSemantics,
            MoveCleanupBoundaryResolution? cleanupBoundaryResolution,
            AudiobookContentMoveService contentMoveService,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(source)
                || !sourceSemantics.HasValue
                || FileSystemPathIdentity.AreEquivalentEndpoints(
                    source,
                    sourceSemantics.Value,
                    target,
                    targetSemantics)
                || !HasFinalizedMoveEvidence(job, audiobook, target, targetSemantics)
                || !AudiobookContentMoveService.CanAttemptFinalizedMoveVerification(
                    source,
                    target,
                    sourceSemantics.Value))
            {
                return FinalizedMoveRecoveryOutcome.NotAttempted;
            }

            var finalizedRequest = new AudiobookContentMoveRequest(
                source,
                target,
                job.Id,
                job.DeleteEmptySource,
                sourceSemantics.Value,
                targetSemantics,
                CreateLeaseToken(job),
                cleanupBoundaryResolution?.Boundary);
            try
            {
                await contentMoveService.VerifyFinalizedMoveAsync(
                    finalizedRequest,
                    cancellationToken);
            }
            catch (MoveNeedsAttentionException exception)
            {
                await UpdateJobStatusAsync(
                    job,
                    MoveJobStatus.NeedsAttention,
                    exception.Message,
                    cancellationToken);
                metrics.Increment("worker.move.job.needs_attention");
                logger.LogWarning(
                    exception,
                    "Move job {JobId} could not prove markerless completion",
                    job.Id);
                return FinalizedMoveRecoveryOutcome.HandledFailure;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await ScheduleTransientRetryAsync(
                    job,
                    $"Finalized move verification will be retried: {exception.Message}",
                    exception,
                    "Move job {JobId} could not verify its published target",
                    cancellationToken);
                return FinalizedMoveRecoveryOutcome.HandledFailure;
            }

            var targetInsideSource = FileSystemPathIdentity.IsSameOrInside(
                target,
                source,
                sourceSemantics.Value);
            var sourceInsideTarget = FileSystemPathIdentity.IsSameOrInside(
                source,
                target,
                targetSemantics);
            var targetPhysicalObjectIdentities =
                await contentMoveService.CapturePublishedTargetPhysicalIdentitiesAsync(
                    job.Id,
                    target,
                    targetSemantics,
                    cancellationToken);
            return new FinalizedMoveRecoveryOutcome(
                Handled: false,
                new AudiobookContentMoveResult(
                    source,
                    target,
                    targetInsideSource,
                    sourceInsideTarget,
                    SourceCleanupCompleted: true,
                    targetPhysicalObjectIdentities));
        }

        private static bool HasFinalizedMoveEvidence(
            MoveJob job,
            Audiobook audiobook,
            string target,
            FileSystemPathSemantics targetSemantics)
        {
            if (job.Phase >= MoveJobPhase.Published)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(audiobook.BasePath))
            {
                return false;
            }

            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    audiobook.BasePath,
                    out var currentBasePath,
                    out _))
            {
                return false;
            }

            try
            {
                return FileSystemPathIdentity.AreEquivalent(
                    currentBasePath,
                    target,
                    targetSemantics);
            }
            catch (Exception exception) when (exception is
                ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
            {
                return false;
            }
        }

        private sealed record FinalizedMoveRecoveryOutcome(
            bool Handled,
            AudiobookContentMoveResult? MoveResult)
        {
            public static FinalizedMoveRecoveryOutcome NotAttempted { get; } = new(false, null);
            public static FinalizedMoveRecoveryOutcome HandledFailure { get; } = new(true, null);
        }

        private async Task<bool> TryFinalizeMoveAsync(
            MoveJob job,
            AudiobookContentMoveService contentMoveService,
            AudiobookContentMoveRequest request,
            AudiobookContentMoveResult result,
            CancellationToken cancellationToken)
        {
            try
            {
                await contentMoveService.FinalizeMoveAsync(
                    request,
                    result,
                    cancellationToken);
                return true;
            }
            catch (Exception exception) when (exception is
                MoveNeedsAttentionException or MoveLeaseLostException or PersistenceException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await ScheduleTransientRetryAsync(
                    job,
                    $"Move finalization will be retried: {exception.Message}",
                    exception,
                    "Move job {JobId} could not finish source-boundary finalization",
                    cancellationToken,
                    contentMoveService,
                    request);
                return false;
            }
        }

        private async Task<bool> TryCleanupCompletedMoveArtifactsAsync(
            MoveJob job,
            AudiobookContentMoveService contentMoveService,
            AudiobookContentMoveRequest request,
            AudiobookContentMoveResult result,
            CancellationToken cancellationToken)
        {
            try
            {
                await contentMoveService.CleanupCompletedMoveArtifactsAsync(
                    request,
                    result,
                    cancellationToken);
                return true;
            }
            catch (Exception exception) when (exception is
                MoveNeedsAttentionException or MoveLeaseLostException or PersistenceException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await ScheduleTransientRetryAsync(
                    job,
                    $"Owned move artifact cleanup will be retried: {exception.Message}",
                    exception,
                    "Move job {JobId} could not remove its owned recovery artifacts",
                    cancellationToken,
                    contentMoveService,
                    request);
                return false;
            }
        }

        private async Task<bool> TryRecordMoveCompletionAsync(
            MoveJob job,
            Audiobook audiobook,
            string source,
            string target,
            AudiobookContentMoveService contentMoveService,
            AudiobookContentMoveRequest moveRequest,
            Action<MovePostCommitContext> registerPostCommit,
            CancellationToken cancellationToken)
        {
            try
            {
                await RecordMoveCompletionAsync(
                    job,
                    audiobook,
                    source,
                    target,
                    contentMoveService,
                    moveRequest,
                    registerPostCommit,
                    cancellationToken);
                return true;
            }
            catch (Exception exception) when (exception is
                MoveNeedsAttentionException or MoveLeaseLostException or PersistenceException)
            {
                throw;
            }
            catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
            {
                await ScheduleTransientRetryAsync(
                    job,
                    $"Move completion handoff will be retried: {exception.Message}",
                    exception,
                    "Move job {JobId} could not persist its required completion handoffs",
                    cancellationToken,
                    contentMoveService,
                    moveRequest);
                return false;
            }
        }

        private async Task ScheduleTransientRetryAsync(
            MoveJob job,
            string error,
            Exception exception,
            string logMessage,
            CancellationToken cancellationToken,
            AudiobookContentMoveService? contentMoveService = null,
            AudiobookContentMoveRequest? moveRequest = null)
        {
            var terminalCleanupCompleted = false;
            if (contentMoveService != null && moveRequest != null)
            {
                var persistedBeforeRetry = await moveQueueService.GetJobAsync(
                    job.Id,
                    cancellationToken)
                    ?? throw new MoveLeaseLostException(
                        job.Id,
                        job.LeaseGeneration);
                if (persistedBeforeRetry.Status == MoveJobStatus.Running
                    && string.Equals(
                        persistedBeforeRetry.LeaseOwner,
                        job.LeaseOwner,
                        StringComparison.Ordinal)
                    && persistedBeforeRetry.LeaseGeneration == job.LeaseGeneration
                    && persistedBeforeRetry.AttemptCount + 1
                        >= MoveTimingPolicy.MaxTransientAttempts)
                {
                    await TryCleanupTerminalTargetScaffoldingAsync(
                        job,
                        contentMoveService,
                        moveRequest,
                        cancellationToken);
                    terminalCleanupCompleted = true;
                }
            }

            var result = await moveQueueService.ScheduleRetryWithoutNotificationAsync(
                job.Id,
                job.LeaseOwner!,
                job.LeaseGeneration,
                error,
                cancellationToken);
            job.Status = result.Status;
            job.Error = result.Status == MoveJobStatus.NeedsAttention
                ? $"{error} Automatic retry limit exhausted; operator attention is required."
                : error;
            if (result.Status == MoveJobStatus.NeedsAttention)
            {
                if (contentMoveService != null
                    && moveRequest != null
                    && !terminalCleanupCompleted)
                {
                    logger.LogWarning(
                        "Move job {JobId} reached the retry limit without terminal scaffolding cleanup under its active lease",
                        job.Id);
                }

                metrics.Increment("worker.move.job.needs_attention");
                logger.LogWarning(
                    exception,
                    logMessage + " and exhausted its transient retry limit",
                    job.Id);
                return;
            }

            metrics.Increment("worker.move.job.retry_scheduled");
            logger.LogWarning(
                exception,
                logMessage + " and was scheduled for retry at {NextAttemptAt}",
                job.Id,
                result.NextAttemptAt);
        }

        private async Task<string?> TryCleanupTerminalTargetScaffoldingAsync(
            MoveJob job,
            AudiobookContentMoveService contentMoveService,
            AudiobookContentMoveRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                await contentMoveService.CleanupTerminalTargetScaffoldingAsync(
                    request,
                    cancellationToken);
                return null;
            }
            catch (Exception exception) when (exception is
                MoveLeaseLostException or PersistenceException)
            {
                throw;
            }
            catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
            {
                logger.LogWarning(
                    exception,
                    "Move job {JobId} could not completely clean its owned target scaffolding",
                    job.Id);
                return exception.Message;
            }
        }

        private static bool IsFilesystemRoot(string path, FileSystemPathSemantics semantics)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            return !string.IsNullOrWhiteSpace(root)
                && FileSystemPathIdentity.AreEquivalent(fullPath, root, semantics);
        }
    }
}
