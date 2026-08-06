using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning;

public partial class ScanJobProcessor
{
    private async Task<bool> ValidateScanRootSafetyAsync(
        string scanRoot,
        ScanJob job,
        Audiobook audiobook,
        IHistoryRepository historyRepository,
        CancellationToken cancellationToken)
    {
        string? failure = null;
        try
        {
            if ((File.GetAttributes(scanRoot) & FileAttributes.ReparsePoint) != 0)
            {
                failure = "Scan path is a symbolic link or reparse point.";
            }
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            _logger.LogWarning(
                exception,
                "Scan job {JobId} could not verify the link status of scan root {Path}",
                job.Id,
                LogRedaction.SanitizeFilePath(scanRoot));
            failure = "Scan path link status could not be verified safely.";
        }

        if (failure == null)
        {
            return true;
        }

        _logger.LogWarning(
            "Scan job {JobId} blocked because its scan root is unsafe: {Reason}",
            job.Id,
            failure);
        await RecordMoveScanFailureAsync(
            historyRepository,
            job,
            audiobook,
            failure,
            cancellationToken);
        _metrics.Increment("worker.scan.job.failed");
        return false;
    }

    private async Task HandleUnexpectedScanFailureAsync(
        ScanJob job,
        Exception exception,
        Action<Func<CancellationToken, Task>> registerPostCompletionEffects,
        CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Error processing scan job {JobId}", job.Id);
        ScanTerminalDecision? terminalDecision = null;
        try
        {
            using var historyScope = _scopeFactory.CreateScope();
            var historyRepository = historyScope.ServiceProvider.GetRequiredService<IHistoryRepository>();
            var audiobookRepository = historyScope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var audiobook = await audiobookRepository.GetForScanAsync(
                job.AudiobookId,
                cancellationToken);
            terminalDecision = await CommitTerminalDecisionAsync(
                job,
                commitToken => RecordScanFailureHistoryAsync(
                    historyRepository,
                    job,
                    audiobook,
                    exception.Message,
                    commitToken),
                cancellationToken);
        }
        catch (Exception historyException) when (historyException is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            _logger.LogDebug(
                historyException,
                "Unable to record failed scan history for job {JobId}",
                job.Id);
        }

        if (terminalDecision == null)
        {
            try
            {
                _queue.UpdateJobStatus(job.Id, "Failed", exception.Message);
            }
            catch (Exception updateException) when (WorkerExceptionClassifier.IsNonFatal(updateException))
            {
                _logger.LogDebug(
                    updateException,
                    "Unable to update failed scan job {JobId}",
                    job.Id);
            }
        }

        var broadcastStatus = terminalDecision?.Status ?? "Failed";
        var broadcastError = terminalDecision?.Error ?? exception.Message;
        registerPostCompletionEffects(token => BroadcastFailedScanAsync(
            job,
            broadcastStatus,
            broadcastError,
            token));
        _metrics.Increment("worker.scan.job.failed");
    }
}
