using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving;

internal partial class MoveJobProcessor
{
    private async Task RecordTerminalMoveFailureAsync(
        MoveJob job,
        Audiobook audiobook,
        AudiobookContentMoveService moveService,
        AudiobookContentMoveRequest? request,
        IServiceScope scope,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var cleanupError = request == null
            ? null
            : await TryCleanupTerminalTargetScaffoldingAsync(
                job,
                moveService,
                request,
                cancellationToken);
        var terminalError = cleanupError == null
            ? exception.Message
            : $"{exception.Message} Target scaffold cleanup also failed: {cleanupError}";

        await moveQueueService.IncrementAttemptAsync(
            job.Id,
            job.LeaseOwner!,
            job.LeaseGeneration,
            cancellationToken);
        await UpdateJobStatusAsync(
            job,
            MoveJobStatus.Failed,
            terminalError,
            cancellationToken);
        metrics.Increment("worker.move.job.failed");

        var publicError = MoveJobPublicProjection.ToError(
            terminalError,
            MoveFailureKind.Unknown)
            ?? "The move failed. Review the server logs for details.";
        try
        {
            var historyEntry = new History
            {
                AudiobookId = audiobook.Id,
                AudiobookTitle = audiobook.Title,
                EventType = "MoveFailed",
                Message = $"Move failed: {publicError}",
                Source = "Move",
                Timestamp = timeProvider.GetUtcNow().UtcDateTime,
                NotificationSent = false,
                Data = System.Text.Json.JsonSerializer.Serialize(new
                {
                    JobId = job.Id,
                    Error = publicError
                })
            };
            var historyRepository = scope.ServiceProvider
                .GetRequiredService<IHistoryRepository>();
            await historyRepository.AddAsync(
                historyEntry,
                CancellationToken.None);
            await TryPublishFailureToastAsync(
                job,
                audiobook,
                publicError);
        }
        catch (Exception historyException) when (
            WorkerExceptionClassifier.IsNonFatal(historyException))
        {
            logger.LogWarning(
                historyException,
                "Failed to add history entry for failed move job {JobId}",
                job.Id);
        }

        logger.LogError(exception, "Move job {JobId} failed", job.Id);
    }

    private async Task TryPublishFailureToastAsync(
        MoveJob job,
        Audiobook audiobook,
        string error)
    {
        try
        {
            var message = !string.IsNullOrEmpty(audiobook.Title)
                ? $"Failed to move {audiobook.Title}: {error}"
                : $"Move failed: {error}";
            await toastService.PublishToastAsync(
                "error",
                "Move Failed",
                message,
                timeoutMs: 15000);
            logger.LogDebug(
                "Sent toast notification for failed move job {JobId}",
                job.Id);
        }
        catch (Exception exception) when (
            WorkerExceptionClassifier.IsNonFatal(exception))
        {
            logger.LogDebug(
                exception,
                "Failed to send toast notification for failed move job {JobId}",
                job.Id);
        }
    }
}
