using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving;

internal partial class MoveJobProcessor
{
    private async Task RecordMoveCompletionAsync(
        MoveJob job,
        Audiobook audiobook,
        string source,
        string target,
        AudiobookContentMoveService contentMoveService,
        AudiobookContentMoveRequest moveRequest,
        MarkerlessTargetVerificationLease? targetVerificationLease,
        bool sourceRetained,
        Action<MovePostCommitContext> registerPostCommit,
        CancellationToken cancellationToken)
    {
        await contentMoveService.MarkCompletionRecordingAsync(moveRequest, cancellationToken);
        contentMoveService.OnCompletionHandoff(
            job.Id,
            CompletionHandoffFaultPoint.BeforeHistoryPersist);
        await contentMoveService.VerifyFinalizedMoveAsync(
            moveRequest,
            cancellationToken,
            targetVerificationLease);

        var now = timeProvider.GetUtcNow();
        if (targetVerificationLease == null)
        {
            throw new MoveNeedsAttentionException(
                "Durable move completion requires a pinned target-generation verification lease.");
        }

        var completion = await moveScanHandoffStore.CommitMoveCompletionAsync(
            new MoveCompletionCommit(
                job.Id,
                job.LeaseOwner!,
                job.LeaseGeneration,
                audiobook.Id,
                audiobook.Title,
                source,
                target,
                now,
                sourceRetained),
            validationToken =>
            {
                contentMoveService.OnCompletionHandoff(
                    job.Id,
                    CompletionHandoffFaultPoint.BeforeCompletionCommitValidation);
                return targetVerificationLease.ProbeCurrentPublicationsAsync(
                    validationToken);
            },
            cancellationToken);

        job.Status = MoveJobStatus.Completed;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        job.ActiveDeduplicationKey = null;
        registerPostCommit(new MovePostCommitContext(
            job.Id,
            audiobook.Id,
            audiobook.Title,
            source,
            target,
            completion.Handoff.Id,
            completion.MoveHistory.Id,
            completion.MoveHistoryCreated,
            sourceRetained));
    }

    public async Task RunPostCompletionEffectsAsync(
        MovePostCommitContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await moveQueueService.NotifyPersistedJobStateAsync(
                context.JobId,
                MoveJobStatus.Completed,
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                exception,
                "Move job {JobId} completed durably but its current state publication was canceled internally",
                context.JobId);
        }
        catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
        {
            logger.LogWarning(
                exception,
                "Move job {JobId} completed durably but its current state could not be published",
                context.JobId);
        }

        await TryDispatchMoveScanHandoffAsync(context, cancellationToken);
        await TryBroadcastAudiobookUpdateAsync(context, cancellationToken);

        if (!context.MoveHistoryCreated)
        {
            return;
        }

        var notificationSent = await TrySendMoveWebhooksAsync(context);
        if (notificationSent)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var historyRepository = scope.ServiceProvider
                    .GetRequiredService<IHistoryRepository>();
                await historyRepository.MarkNotificationSentAsync(
                    context.MoveHistoryId,
                    cancellationToken);
            }
            catch (OperationCanceledException exception) when (
                !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    exception,
                    "Move completion was committed but its notification flag update was canceled internally for job {JobId}",
                    context.JobId);
            }
            catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
            {
                logger.LogWarning(
                    exception,
                    "Move completion was committed but its notification flag could not be updated for job {JobId}",
                    context.JobId);
            }
        }

        await TryPublishMoveToastAsync(context);
    }

    private async Task TryDispatchMoveScanHandoffAsync(
        MovePostCommitContext context,
        CancellationToken cancellationToken)
    {
        var audiobook = new Audiobook
        {
            Id = context.AudiobookId,
            Title = context.AudiobookTitle
        };
        var result = await MoveScanHandoffDispatchWorkflow.TryDispatchPendingAsync(
            context.HandoffId,
            ownerPrefix: "move-scan",
            knownAudiobook: audiobook,
            beforeEnqueue: _ => contentMoveService.OnCompletionHandoff(
                context.JobId,
                CompletionHandoffFaultPoint.BeforeScanEnqueue),
            scanQueueService,
            moveScanHandoffStore,
            scopeFactory,
            timeProvider,
            logger,
            cancellationToken);
        if (result.Outcome == MoveScanDispatchOutcome.Failed)
        {
            logger.LogWarning(
                "Move job {JobId} completed, but immediate scan handoff dispatch failed; recovery will retry it",
                context.JobId);
        }
    }

    private async Task<bool> TrySendMoveWebhooksAsync(MovePostCommitContext context)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var notificationService = scope.ServiceProvider
                .GetRequiredService<INotificationService>();
            await notificationService.SendNotificationAsync(
                NotificationTriggers.LibraryMoved,
                new
                {
                    context.AudiobookTitle,
                    Source = context.Source,
                    Target = context.Target,
                    context.SourceRetained,
                    Timestamp = timeProvider.GetUtcNow().UtcDateTime
                });

            return true;
        }
        catch (OperationCanceledException exception)
        {
            logger.LogWarning(
                exception,
                "Move notification was canceled internally for {JobId}",
                context.JobId);
            return false;
        }
        catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
        {
            logger.LogWarning(
                exception,
                "Failed to send move notification for {JobId}",
                context.JobId);
            return false;
        }
    }

    private async Task TryPublishMoveToastAsync(MovePostCommitContext context)
    {
        try
        {
            var message = context.SourceRetained
                ? !string.IsNullOrEmpty(context.AudiobookTitle)
                    ? $"Copied {context.AudiobookTitle} to {context.Target}; source retained"
                    : $"Copied audiobook to {context.Target}; source retained"
                : !string.IsNullOrEmpty(context.AudiobookTitle)
                    ? $"Moved {context.AudiobookTitle} to {context.Target}"
                    : $"Moved audiobook to {context.Target}";
            await toastService.PublishToastAsync(
                "success",
                context.SourceRetained ? "Copy Complete" : "Move Complete",
                message,
                timeoutMs: 5000);
            logger.LogDebug("Sent toast notification for move job {JobId}", context.JobId);
        }
        catch (OperationCanceledException exception)
        {
            logger.LogDebug(
                exception,
                "Toast notification was canceled internally for move job {JobId}",
                context.JobId);
        }
        catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
        {
            logger.LogDebug(
                exception,
                "Failed to send toast notification for move job {JobId}",
                context.JobId);
        }
    }

    private async Task TryBroadcastAudiobookUpdateAsync(
        MovePostCommitContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            if (audiobookUpdatePublisher == null)
            {
                return;
            }

            await audiobookUpdatePublisher.PublishCurrentAsync(
                context.AudiobookId,
                cancellationToken);
            logger.LogInformation(
                "Broadcasted full AudiobookUpdate for AudiobookId {AudiobookId} after move job {JobId}",
                context.AudiobookId,
                context.JobId);
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                exception,
                "AudiobookUpdate broadcast was canceled internally after move job {JobId}",
                context.JobId);
        }
        catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
        {
            logger.LogWarning(
                exception,
                "Failed to broadcast AudiobookUpdate after move job {JobId}",
                context.JobId);
        }
    }
}
