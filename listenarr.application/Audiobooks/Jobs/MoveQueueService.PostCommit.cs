using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Jobs;

public partial class MoveQueueService
{
    private async Task NotifyCommittedJobStateAsync(
        Guid id,
        MoveJobStatus status,
        string? error,
        CancellationToken cancellationToken)
    {
        try
        {
            await NotifyPersistedJobStateAsync(
                id,
                status,
                error,
                cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            _logger.LogDebug(
                exception,
                "Move job {JobId} state was committed before notification cancellation",
                id);
        }
    }
}
