/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed class MoveBackgroundService(
    IMoveQueueService moveQueueService,
    IMoveJobProcessor processor,
    ILibraryFilesystemReadiness filesystemReadiness,
    ILogger<MoveBackgroundService> logger,
    IAppMetricsService? metrics = null,
    TimeSpan? heartbeatInterval = null,
    TimeSpan? ownershipDuration = null) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("MoveBackgroundService waiting for library filesystem initialization");
        await filesystemReadiness.WaitUntilReadyAsync(stoppingToken);
        logger.LogInformation("MoveBackgroundService filesystem gate opened");

        var leaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        var retryDelay = TimeSpan.FromSeconds(1);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await moveQueueService.RecoverActiveJobsAsync(stoppingToken);
                if (metrics != null)
                {
                    var health = await moveQueueService.GetQueueHealthAsync(stoppingToken);
                    metrics.Gauge("worker.move.queue.depth", health.QueueDepth);
                    metrics.Gauge("worker.move.queue.oldest_age_seconds", health.OldestQueuedAgeSeconds);
                    metrics.Gauge("worker.move.queue.retries", health.RetryCount);
                    metrics.Gauge("worker.move.queue.expired_leases", health.ExpiredLeaseCount);
                    metrics.Gauge("worker.move.queue.needs_attention", health.NeedsAttentionCount);
                }
                while (moveQueueService.Reader.TryRead(out var job))
                {
                    var leaseGeneration = await moveQueueService.TryClaimJobAsync(
                        job.Id,
                        leaseOwner,
                        stoppingToken);
                    if (leaseGeneration == null)
                    {
                        continue;
                    }

                    job.LeaseGeneration = leaseGeneration.Value;
                    job.LeaseOwner = leaseOwner;

                    try
                    {
                        using var processingCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        using var heartbeatCancellation = new CancellationTokenSource();
                        var leaseLost = new TaskCompletionSource<MoveLeaseLostException>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        using var ownershipDeadline = new CancellationTokenSource(
                            ownershipDuration ?? MoveTimingPolicy.OwnershipDuration);
                        using var deadlineRegistration = ownershipDeadline.Token.Register(static state =>
                        {
                            var context = (OwnershipDeadlineContext)state!;
                            context.Logger.LogWarning(
                                "Move job {JobId} locally confirmed ownership expired for generation {LeaseGeneration}; canceling processing",
                                context.JobId,
                                context.Generation);
                            var exception = new MoveLeaseLostException(context.JobId, context.Generation);
                            context.Lost.TrySetResult(exception);
                            _ = context.Processing.CancelAsync();
                        }, new OwnershipDeadlineContext(job.Id, leaseGeneration.Value, processingCancellation, leaseLost, logger));
                        var heartbeatTask = RunHeartbeatAsync(
                            job.Id,
                            leaseOwner,
                            leaseGeneration.Value,
                            processingCancellation,
                            leaseLost,
                            ownershipDeadline,
                            heartbeatCancellation.Token);
                        MovePostCommitContext? postCommit = null;
                        var phasedProcessor = processor as IMoveJobProcessorPhases;
                        try
                        {
                            if (phasedProcessor != null)
                            {
                                postCommit = await phasedProcessor.ProcessDurableJobAsync(
                                    job,
                                    processingCancellation.Token);
                                ownershipDeadline.CancelAfter(Timeout.InfiniteTimeSpan);
                            }
                            else
                            {
                                await processor.ProcessJobAsync(job, processingCancellation.Token);
                            }
                        }
                        catch (OperationCanceledException) when (leaseLost.Task.IsCompletedSuccessfully)
                        {
                            throw leaseLost.Task.Result;
                        }
                        finally
                        {
                            ownershipDeadline.CancelAfter(Timeout.InfiniteTimeSpan);
                            await heartbeatCancellation.CancelAsync();
                            await ObserveHeartbeatExitAsync(heartbeatTask, stoppingToken);
                        }

                        if (postCommit != null && phasedProcessor != null)
                        {
                            try
                            {
                                await phasedProcessor.RunPostCompletionEffectsAsync(
                                    postCommit,
                                    stoppingToken);
                            }
                            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                            {
                                logger.LogWarning(
                                    exception,
                                    "Move job {JobId} completed durably but an optional post-completion effect failed",
                                    job.Id);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (MoveLeaseLostException exception)
                    {
                        logger.LogWarning(exception, "Move job {JobId} lost its lease and stopped", job.Id);
                    }
                    catch (PersistenceException exception)
                    {
                        logger.LogWarning(exception, "Move job {JobId} stopped because persistence is unavailable", job.Id);
                        throw;
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                    {
                        logger.LogError(exception, "Unexpected error processing move job {JobId}", job.Id);
                        await moveQueueService.UpdateJobStatusAsync(
                            job.Id,
                            leaseOwner,
                            leaseGeneration.Value,
                            MoveJobStatus.Failed,
                            exception.Message,
                            stoppingToken);
                    }
                }

                retryDelay = TimeSpan.FromSeconds(1);
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                logger.LogError(exception, "Move queue poll failed; the worker will retry");
                await Task.Delay(retryDelay, stoppingToken);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
            }
        }

        logger.LogInformation("MoveBackgroundService stopping due to host shutdown");
    }

    private async Task ObserveHeartbeatExitAsync(Task heartbeatTask, CancellationToken stoppingToken)
    {
        var completed = await Task.WhenAny(
            heartbeatTask,
            Task.Delay(TimeSpan.FromSeconds(1), stoppingToken));
        if (completed != heartbeatTask)
        {
            logger.LogWarning("Move heartbeat did not stop promptly after cancellation; leaving it detached");
            _ = heartbeatTask.ContinueWith(
                static task => _ = task.Exception,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            return;
        }

        try
        {
            await heartbeatTask;
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Move heartbeat task canceled after processing stopped");
        }
    }

    private async Task RunHeartbeatAsync(
        Guid jobId,
        string leaseOwner,
        int leaseGeneration,
        CancellationTokenSource processingCancellation,
        TaskCompletionSource<MoveLeaseLostException> leaseLost,
        CancellationTokenSource ownershipDeadline,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(
            heartbeatInterval ?? TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                var outcome = await moveQueueService.HeartbeatJobAsync(
                    jobId,
                    leaseOwner,
                    leaseGeneration,
                    cancellationToken);
                if (outcome == MoveHeartbeatOutcome.Terminal)
                {
                    ownershipDeadline.CancelAfter(Timeout.InfiniteTimeSpan);
                    break;
                }

                if (outcome == MoveHeartbeatOutcome.Lost)
                {
                    await CancelForLostOwnershipAsync(
                        jobId,
                        leaseGeneration,
                        processingCancellation,
                        leaseLost,
                        null,
                        "Move job {JobId} lost ownership generation {LeaseGeneration}; canceling processing");
                    break;
                }

                ownershipDeadline.CancelAfter(ownershipDuration ?? MoveTimingPolicy.OwnershipDuration);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                await CancelForLostOwnershipAsync(
                    jobId,
                    leaseGeneration,
                    processingCancellation,
                    leaseLost,
                    exception,
                    "Failed to renew ownership for move job {JobId}; canceling processing");
                break;
            }
        }
    }

    private async Task CancelForLostOwnershipAsync(
        Guid jobId,
        int generation,
        CancellationTokenSource processingCancellation,
        TaskCompletionSource<MoveLeaseLostException> leaseLost,
        Exception? exception,
        string message)
    {
        if (exception == null)
        {
            logger.LogWarning(message, jobId, generation);
        }
        else
        {
            logger.LogWarning(exception, message, jobId, generation);
        }

        leaseLost.TrySetResult(new MoveLeaseLostException(jobId, generation));
        await processingCancellation.CancelAsync();
    }

    private sealed record OwnershipDeadlineContext(
        Guid JobId,
        int Generation,
        CancellationTokenSource Processing,
        TaskCompletionSource<MoveLeaseLostException> Lost,
        ILogger<MoveBackgroundService> Logger);
}
