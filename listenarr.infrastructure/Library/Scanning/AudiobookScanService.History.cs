using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning;

internal sealed partial class AudiobookScanService
{
    private async Task TryAddHistoryAsync(
        History history,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(history);
        try
        {
            await historyRepository.AddAsync(history, cancellationToken);
        }
        catch (Exception exception) when (
            WorkerExceptionClassifier.IsNonFatal(exception))
        {
            logger.LogWarning(
                exception,
                "Scan history write failed after state processing for audiobook {AudiobookId}, event {EventType}, correlation {CorrelationId}",
                history.AudiobookId,
                LogRedaction.SanitizeText(history.EventType),
                LogRedaction.SanitizeText(history.CorrelationId));
        }
    }
}
