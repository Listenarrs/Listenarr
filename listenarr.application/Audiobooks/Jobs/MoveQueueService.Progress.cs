using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Jobs;

public partial class MoveQueueService
{
    public async Task PublishProgressAsync(
        Guid id,
        double progress,
        string phase,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        var publicationGate = AcquirePublicationGate(id);
        var enteredPublicationGate = false;
        try
        {
            await publicationGate.Gate.WaitAsync(cancellationToken);
            enteredPublicationGate = true;

            MoveJob? dbJob;
            try
            {
                dbJob = await _persistence.GetByIdAsync(id, cancellationToken);
            }
            catch (Exception ex) when (ex is not (
                OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                _logger.LogDebug(
                    ex,
                    "Skipped transient move progress publication for job {JobId} because persisted state could not be reloaded",
                    id);
                return;
            }

            if (dbJob == null || dbJob.Status != MoveJobStatus.Running)
            {
                return;
            }

            try
            {
                await _hubBroadcaster.BroadcastAsync(
                    "MoveJobUpdate",
                    MoveJobPublicProjection.CreateUpdate(
                        id,
                        dbJob.Status,
                        dbJob.Error,
                        _timeProvider.GetUtcNow().UtcDateTime,
                        dbJob,
                        progress,
                        phase),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not (
                OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                _logger.LogDebug(
                    ex,
                    "Non-fatal: failed to broadcast progress for move job {JobId}",
                    id);
            }
        }
        finally
        {
            if (enteredPublicationGate)
            {
                publicationGate.Gate.Release();
            }

            ReleasePublicationGate(id, publicationGate);
        }
    }
}
