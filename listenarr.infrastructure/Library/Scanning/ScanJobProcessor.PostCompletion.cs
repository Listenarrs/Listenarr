using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning;

public partial class ScanJobProcessor
{
    private async Task RunSuccessfulPostCompletionEffectsAsync(
        ScanJob job,
        Audiobook audiobook,
        int found,
        int created,
        CancellationToken cancellationToken)
    {
        await NotifyAvailableAsync(audiobook, created);
        try
        {
            if (_audiobookUpdatePublisher != null)
            {
                await _audiobookUpdatePublisher.PublishCurrentAsync(
                    audiobook.Id,
                    cancellationToken);
            }

            await _hubContext.Clients.All.SendAsync("ScanJobUpdate", new
            {
                jobId = job.Id.ToString(),
                audiobookId = job.AudiobookId,
                status = "Completed",
                found,
                created,
                completedAt = _timeProvider.GetUtcNow().UtcDateTime
            }, cancellationToken);
            _logger.LogInformation(
                "Broadcasted AudiobookUpdate for AudiobookId {AudiobookId} after scan job {JobId}",
                audiobook.Id,
                job.Id);
        }
        catch (Exception broadcastException) when (WorkerExceptionClassifier.IsNonFatal(broadcastException))
        {
            _logger.LogWarning(
                broadcastException,
                "Scan job {JobId} completed durably but its client update could not be broadcast",
                job.Id);
        }
    }

    private async Task BroadcastFailedScanAsync(
        ScanJob job,
        string status,
        string? error,
        CancellationToken cancellationToken)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync("ScanJobUpdate", new
            {
                jobId = job.Id.ToString(),
                audiobookId = job.AudiobookId,
                status,
                error = ScanJobPublicError.FromInternal(error),
                failedAt = _timeProvider.GetUtcNow().UtcDateTime
            }, cancellationToken);
        }
        catch (Exception broadcastException) when (WorkerExceptionClassifier.IsNonFatal(broadcastException))
        {
            _logger.LogDebug(
                broadcastException,
                "Unable to broadcast terminal scan state for job {JobId}",
                job.Id);
        }
    }

    private async Task BroadcastFilesRemovedAsync(
        int audiobookId,
        IReadOnlyCollection<object> removed,
        CancellationToken cancellationToken)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync(
                "FilesRemoved",
                new { audiobookId, removed },
                cancellationToken);
        }
        catch (Exception broadcastException) when (WorkerExceptionClassifier.IsNonFatal(broadcastException))
        {
            _logger.LogDebug(
                broadcastException,
                "Failed to broadcast FilesRemoved event for audiobook {AudiobookId}",
                audiobookId);
        }
    }
}
