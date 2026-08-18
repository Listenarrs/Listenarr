using Listenarr.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving;

internal partial class MoveJobProcessor
{
    private async Task ProcessJobCoreAsync(
        MoveJob job,
        Action<MovePostCommitContext> registerPostCommit,
        CancellationToken stoppingToken)
    {
        using var logScope = logger.BeginScope(new Dictionary<string, object?> { ["JobId"] = job.Id, ["AudiobookId"] = job.AudiobookId });
        metrics.Increment("worker.move.job.started");
        stoppingToken.ThrowIfCancellationRequested();
        try
        {
            logger.LogInformation("Processing move job {JobId} for audiobook {AudiobookId} to {Path}", job.Id, job.AudiobookId, LogRedaction.SanitizeFilePath(job.RequestedPath));

            if (!await EnsureNoExternalRecoveryOwnerAsync(job, stoppingToken))
            {
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var audiobookRepository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var audiobook = await audiobookRepository.GetByIdAsync(job.AudiobookId);
            if (audiobook == null)
            {
                await UpdateJobStatusAsync(job, MoveJobStatus.Failed, "Audiobook not found", stoppingToken);
                metrics.Increment("worker.move.job.failed");
                return;
            }

            var requested = job.RequestedPath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(requested))
            {
                await UpdateJobStatusAsync(job, MoveJobStatus.Failed, "Target path not provided", stoppingToken);
                metrics.Increment("worker.move.job.failed");
                return;
            }
            var target = await ResolvePersistedEndpointAsync(
                job,
                requested,
                "target",
                stoppingToken);
            if (target == null)
            {
                return;
            }

            PathIdentitySnapshot targetIdentity;
            try
            {
                targetIdentity = await GetRequiredIdentityAsync(job, target, target: true, stoppingToken);
            }
            catch (MoveNeedsAttentionException exception)
            {
                await UpdateJobStatusAsync(job, MoveJobStatus.NeedsAttention, exception.Message, stoppingToken);
                metrics.Increment("worker.move.job.needs_attention");
                return;
            }
            catch (Exception exception) when (IsTransientFilesystemException(exception))
            {
                await ScheduleTransientRetryAsync(
                    job,
                    $"Move target identity verification will be retried: {exception.Message}",
                    exception,
                    "Move job {JobId} could not verify its target filesystem identity",
                    stoppingToken);
                return;
            }

            var targetSemantics = targetIdentity.Semantics;
            var source = job.SourcePath;
            AudiobookContentMoveResult? recoveredMove = null;
            MoveCleanupBoundaryResolution? cleanupBoundaryResolution = null;
            var hasFilesystemExecutionEvidence = false;
            PathIdentitySnapshot? recoverySourceIdentity = null;
            FileSystemPathSemantics? recoverySourceSemantics = null;
            if (!string.IsNullOrWhiteSpace(source))
            {
                source = await ResolvePersistedEndpointAsync(
                    job,
                    source,
                    "source",
                    stoppingToken);
                if (source == null)
                {
                    return;
                }

                PathIdentitySnapshot resolvedSourceIdentity;
                try
                {
                    resolvedSourceIdentity = await GetRequiredIdentityAsync(job, source, target: false, stoppingToken);
                    recoverySourceIdentity = resolvedSourceIdentity;
                }
                catch (MoveNeedsAttentionException exception)
                {
                    await UpdateJobStatusAsync(job, MoveJobStatus.NeedsAttention, exception.Message, stoppingToken);
                    metrics.Increment("worker.move.job.needs_attention");
                    return;
                }
                catch (Exception exception) when (IsTransientFilesystemException(exception))
                {
                    await ScheduleTransientRetryAsync(
                        job,
                        $"Move source identity verification will be retried: {exception.Message}",
                        exception,
                        "Move job {JobId} could not verify its source filesystem identity",
                        stoppingToken);
                    return;
                }
                recoverySourceSemantics = resolvedSourceIdentity.Semantics;
                if (!await EnsureNoExternalRecoveryBoundaryOwnerAsync(
                        job,
                        source,
                        resolvedSourceIdentity,
                        target,
                        targetIdentity,
                        stoppingToken))
                {
                    return;
                }
                if (await TryHandleIdenticalEndpointsAsync(
                        job,
                        source,
                        resolvedSourceIdentity,
                        target,
                        targetIdentity,
                        contentMoveService,
                        stoppingToken))
                {
                    return;
                }

                cleanupBoundaryResolution = GetPersistedCleanupBoundary(job);
                var recoveryRequest = new AudiobookContentMoveRequest(
                    source,
                    target,
                    job.Id,
                    job.DeleteEmptySource,
                    resolvedSourceIdentity.Semantics,
                    targetSemantics,
                    CreateLeaseToken(job),
                    cleanupBoundaryResolution.Boundary);
                try
                {
                    var resumedMove = await contentMoveService.GetRecoverableMoveAsync(recoveryRequest, stoppingToken);
                    if (resumedMove != null)
                    {
                        recoveredMove = resumedMove;
                        logger.LogInformation("Resuming move job {JobId} after its filesystem phase completed", job.Id);
                    }
                }
                catch (MoveNeedsAttentionException exception)
                {
                    await UpdateJobStatusAsync(job, MoveJobStatus.NeedsAttention, exception.Message, stoppingToken);
                    metrics.Increment("worker.move.job.needs_attention");
                    logger.LogWarning(exception, "Move job {JobId} has ambiguous or invalid recovery artifacts", job.Id);
                    return;
                }
                catch (Exception exception) when (IsTransientFilesystemException(exception))
                {
                    await ScheduleTransientRetryAsync(
                        job,
                        $"Move recovery verification will be retried: {exception.Message}",
                        exception,
                        "Move job {JobId} could not verify its recovery target",
                        stoppingToken);
                    return;
                }
            }

            if (recoveredMove == null
                && job.Phase <= MoveJobPhase.Planned
                && !string.IsNullOrWhiteSpace(source)
                && recoverySourceIdentity.HasValue)
            {
                var executionEvidence = await HasFilesystemExecutionEvidenceAsync(
                    job,
                    source,
                    recoverySourceIdentity.Value,
                    target,
                    targetIdentity,
                    cleanupBoundaryResolution?.Boundary,
                    contentMoveService,
                    stoppingToken);
                if (!executionEvidence.HasValue)
                {
                    return;
                }

                hasFilesystemExecutionEvidence = executionEvidence.Value;
                if (!hasFilesystemExecutionEvidence
                    && await ValidateSourceStateBeforeMutationAsync(
                        job,
                        source,
                        recoverySourceIdentity.Value,
                        target,
                        targetIdentity,
                        recoveredMove: null,
                        hasFilesystemExecutionEvidence: false,
                        stoppingToken) == null)
                {
                    return;
                }
            }

            if (recoveredMove == null)
            {
                var finalizedRecovery = await TryRecoverFinalizedMoveAsync(
                    job,
                    audiobook,
                    source,
                    target,
                    recoverySourceSemantics,
                    targetSemantics,
                    cleanupBoundaryResolution,
                    contentMoveService,
                    stoppingToken);
                if (finalizedRecovery.Handled)
                {
                    return;
                }

                recoveredMove = finalizedRecovery.MoveResult;
            }

            if (string.IsNullOrWhiteSpace(source))
            {
                await UpdateJobStatusAsync(
                    job,
                    MoveJobStatus.NeedsAttention,
                    "The move job has no persisted source path.",
                    stoppingToken);
                metrics.Increment("worker.move.job.needs_attention");
                return;
            }

            source = recoveredMove?.Source ?? source;
            PathIdentitySnapshot sourceIdentity;
            try
            {
                sourceIdentity = await GetRequiredIdentityAsync(job, source, target: false, stoppingToken);
            }
            catch (MoveNeedsAttentionException exception)
            {
                await UpdateJobStatusAsync(job, MoveJobStatus.NeedsAttention, exception.Message, stoppingToken);
                metrics.Increment("worker.move.job.needs_attention");
                return;
            }
            catch (Exception exception) when (IsTransientFilesystemException(exception))
            {
                await ScheduleTransientRetryAsync(
                    job,
                    $"Move source identity verification will be retried: {exception.Message}",
                    exception,
                    "Move job {JobId} could not verify its source filesystem identity",
                    stoppingToken);
                return;
            }

            var currentAudiobook = await ValidateSourceStateBeforeMutationAsync(
                job,
                source,
                sourceIdentity,
                target,
                targetIdentity,
                recoveredMove,
                hasFilesystemExecutionEvidence,
                stoppingToken);
            if (currentAudiobook == null)
            {
                return;
            }

            audiobook = currentAudiobook;
            IReadOnlyDictionary<string, string>? sourcePhysicalObjectIdentities = null;
            if (recoveredMove?.SourceCleanupCompleted != true)
            {
                try
                {
                    sourcePhysicalObjectIdentities = BuildSourcePhysicalObjectIdentities(
                        audiobook,
                        job,
                        source,
                        sourceIdentity.Semantics);
                }
                catch (MoveNeedsAttentionException exception)
                {
                    await MarkSourceStateNeedsAttentionAsync(
                        job,
                        exception.Message,
                        stoppingToken);
                    return;
                }
            }

            var sourceSemantics = sourceIdentity.Semantics;
            cleanupBoundaryResolution ??= GetPersistedCleanupBoundary(job);
            LogCleanupBoundary(job, cleanupBoundaryResolution);

            if (IsFilesystemRoot(source, sourceSemantics)
                || IsFilesystemRoot(target, targetSemantics))
            {
                await UpdateJobStatusAsync(job, MoveJobStatus.Failed, "Refused to move a filesystem root", stoppingToken);
                metrics.Increment("worker.move.job.failed");
                logger.LogWarning(
                    "Blocked move job {JobId}: source or target is a filesystem root. Source={Source}, Target={Target}",
                    job.Id,
                    LogRedaction.SanitizeFilePath(source),
                    LogRedaction.SanitizeFilePath(target));
                return;
            }

            await ExecuteFilesystemMoveAsync(
                job,
                audiobook,
                source,
                target,
                sourceSemantics,
                targetSemantics,
                cleanupBoundaryResolution,
                recoveredMove,
                sourcePhysicalObjectIdentities,
                scope,
                registerPostCommit,
                stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(ex, "Move job {JobId} canceled/timed out", job.Id);
        }
        catch (Exception ex) when (ex is PersistenceException or MoveLeaseLostException)
        {
            logger.LogWarning(ex, "Move job {JobId} stopped because durable coordination failed", job.Id);
            throw;
        }
        catch (Exception ex) when (WorkerExceptionClassifier.IsNonFatal(ex))
        {
            logger.LogError(ex, "Unexpected error processing move job {JobId}", job.Id);
            await UpdateJobStatusAsync(job, MoveJobStatus.Failed, ex.Message, stoppingToken);
            metrics.Increment("worker.move.job.failed");
        }
    }

    private async Task ExecuteFilesystemMoveAsync(
        MoveJob job,
        Audiobook audiobook,
        string source,
        string target,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        MoveCleanupBoundaryResolution cleanupBoundaryResolution,
        AudiobookContentMoveResult? recoveredMove,
        IReadOnlyDictionary<string, string>? sourcePhysicalObjectIdentities,
        IServiceScope scope,
        Action<MovePostCommitContext> registerPostCommit,
        CancellationToken stoppingToken)
    {
        AudiobookContentMoveRequest? moveRequest = null;
        AudiobookContentMoveResult? moveResult = recoveredMove;
        try
        {
            moveRequest = new AudiobookContentMoveRequest(
                source,
                target,
                job.Id,
                job.DeleteEmptySource,
                sourceSemantics,
                targetSemantics,
                CreateLeaseToken(job),
                cleanupBoundaryResolution.Boundary,
                SourcePhysicalObjectIdentities: sourcePhysicalObjectIdentities,
                ProgressReporter: (progress, phase, token) =>
                    moveQueueService.PublishProgressAsync(
                        job.Id,
                        progress,
                        phase,
                        token));
            moveResult ??= await contentMoveService.MoveContentsAsync(moveRequest, stoppingToken);
            moveResult = await contentMoveService.ResumeSourceCleanupAsync(moveRequest, moveResult, stoppingToken);
            source = moveResult.Source;
            target = moveResult.Target;
            if (AfterSourceCleanupBeforeMetadataRewriteForTest != null)
            {
                await AfterSourceCleanupBeforeMetadataRewriteForTest(job);
            }
            await contentMoveService.EnsureMutationAuthorizedAsync(
                moveRequest,
                stoppingToken);
            await contentMoveService.VerifyTargetBeforeMetadataRewriteAsync(
                moveRequest,
                moveResult,
                stoppingToken);

            using (var rewriteScope = scopeFactory.CreateScope())
            {
                var rewriteRepository = rewriteScope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
                var targetCaseSensitivityMode = job.TryGetTargetIdentity(out var targetIdentity)
                    ? targetIdentity.RequestedMode
                    : FileSystemCaseSensitivityMode.Auto;
                await MovedAudiobookPathRewriter.RewriteAsync(
                    audiobook.Id,
                    source,
                    target,
                    moveRequest.SourceSemantics,
                    moveRequest.TargetSemantics,
                    rewriteRepository,
                    logger,
                    stoppingToken,
                    moveResult.TargetPhysicalObjectIdentities,
                    targetCaseSensitivityMode);
            }

            using var currentAudiobookScope = scopeFactory.CreateScope();
            var currentAudiobookRepository = currentAudiobookScope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            audiobook = await currentAudiobookRepository.GetByIdAsync(audiobook.Id)
                ?? throw new MoveNeedsAttentionException(
                    "The audiobook disappeared after its moved path references were persisted.");
            if (!await TryFinalizeMoveAsync(job, contentMoveService, moveRequest, moveResult, stoppingToken)
                || !await TryCleanupCompletedMoveArtifactsAsync(job, contentMoveService, moveRequest, moveResult, stoppingToken))
            {
                return;
            }

            await contentMoveService.MarkCompletionRecordingAsync(moveRequest, stoppingToken);
            if (!await TryRecordMoveCompletionAsync(
                    job,
                    audiobook,
                    source,
                    target,
                    contentMoveService,
                    moveRequest,
                    moveResult.TargetVerificationLease,
                    registerPostCommit,
                    stoppingToken))
            {
                return;
            }

            metrics.Increment("worker.move.job.completed");
            logger.LogInformation(
                "Move job {JobId} completed: {Source} -> {Target}",
                job.Id,
                LogRedaction.SanitizeFilePath(source),
                LogRedaction.SanitizeFilePath(target));
        }
        catch (Exception ex) when (ex is PersistenceException or MoveLeaseLostException)
        {
            throw;
        }
        catch (MoveNeedsAttentionException ex)
        {
            var cleanupError = moveRequest == null
                ? null
                : await TryCleanupTerminalTargetScaffoldingAsync(job, contentMoveService, moveRequest, stoppingToken);
            var error = cleanupError == null
                ? ex.Message
                : $"{ex.Message} Target scaffold cleanup also requires attention: {cleanupError}";
            await UpdateJobStatusAsync(job, MoveJobStatus.NeedsAttention, error, stoppingToken);
            metrics.Increment("worker.move.job.needs_attention");
            logger.LogWarning(ex, "Move job {JobId} requires operator attention", job.Id);
        }
        catch (Exception ex) when (IsTransientFilesystemException(ex))
        {
            await ScheduleTransientRetryAsync(
                job,
                $"Move filesystem work will be retried: {ex.Message}",
                ex,
                "Move job {JobId} encountered a transient filesystem failure",
                stoppingToken,
                contentMoveService,
                moveRequest);
        }
        catch (Exception ex) when (WorkerExceptionClassifier.IsNonFatal(ex))
        {
            await RecordTerminalMoveFailureAsync(
                job,
                audiobook,
                contentMoveService,
                moveRequest,
                scope,
                ex,
                stoppingToken);
        }
        finally
        {
            moveResult?.TargetVerificationLease?.Dispose();
        }
    }

}
