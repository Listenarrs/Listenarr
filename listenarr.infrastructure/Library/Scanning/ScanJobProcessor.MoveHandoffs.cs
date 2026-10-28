using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning;

public partial class ScanJobProcessor
{
    private sealed record ScanTerminalDecision(
        string Status,
        string? Error,
        bool MoveOwned);

    private async Task<ScanTerminalDecision> RecordScanCompletionAsync(
        IHistoryRepository historyRepository,
        ScanJob job,
        Audiobook audiobook,
        int found,
        int created,
        string scanRoot,
        CancellationToken cancellationToken)
    {
        if (job.MoveScanHandoffId.HasValue && _moveScanHandoffStore != null)
        {
            var result = await _moveScanHandoffStore.CompleteAttemptAsync(
                job.MoveScanHandoffId.Value,
                job.MoveScanAttemptGeneration,
                job.Id,
                MoveScanTerminalOutcome.Succeeded,
                error: null,
                found,
                created,
                scanRoot,
                _timeProvider.GetUtcNow(),
                cancellationToken);
            return ToTerminalDecision(result);
        }

        var correlationId = job.CorrelationId ?? job.Id.ToString("N");
        var idempotencyKey = $"scan:{job.Id:N}:completed";
        var existing = await historyRepository.GetByCorrelationIdAsync(
            correlationId,
            cancellationToken);
        if (existing.Any(history => string.Equals(
                history.IdempotencyKey,
                idempotencyKey,
                StringComparison.Ordinal)))
        {
            return new ScanTerminalDecision("Completed", null, MoveOwned: false);
        }

        await historyRepository.AddAsync(new History
        {
            AudiobookId = audiobook.Id,
            AudiobookTitle = audiobook.Title,
            SourceTitle = audiobook.Title,
            DownloadId = job.DownloadId,
            EventType = HistoryEvents.ScanCompleted,
            Outcome = HistoryOutcome.Succeeded,
            Source = "LibraryScan",
            Message = $"Library scan completed: {found} found, {created} created",
            Timestamp = _timeProvider.GetUtcNow().UtcDateTime,
            CorrelationId = correlationId,
            IdempotencyKey = idempotencyKey,
            Data = JsonSerializer.Serialize(new
            {
                ScanJobId = job.Id,
                Found = found,
                Created = created,
                Path = scanRoot
            })
        }, cancellationToken);
        return new ScanTerminalDecision("Completed", null, MoveOwned: false);
    }

    private async Task<ScanTerminalDecision> RecordScanFailureHistoryAsync(
        IHistoryRepository historyRepository,
        ScanJob job,
        Audiobook? audiobook,
        string error,
        CancellationToken cancellationToken)
    {
        if (job.MoveScanHandoffId.HasValue && _moveScanHandoffStore != null)
        {
            var result = await _moveScanHandoffStore.CompleteAttemptAsync(
                job.MoveScanHandoffId.Value,
                job.MoveScanAttemptGeneration,
                job.Id,
                MoveScanTerminalOutcome.Failed,
                error,
                found: 0,
                created: 0,
                job.Path,
                _timeProvider.GetUtcNow(),
                cancellationToken);
            return ToTerminalDecision(result);
        }

        var correlationId = job.CorrelationId ?? job.Id.ToString("N");
        var idempotencyKey = $"scan:{job.Id:N}:failed";
        var existing = await historyRepository.GetByCorrelationIdAsync(
            correlationId,
            cancellationToken);
        if (existing.Any(history => string.Equals(
                history.IdempotencyKey,
                idempotencyKey,
                StringComparison.Ordinal)))
        {
            return new ScanTerminalDecision("Failed", error, MoveOwned: false);
        }

        await historyRepository.AddAsync(new History
        {
            AudiobookId = job.AudiobookId,
            AudiobookTitle = audiobook?.Title,
            SourceTitle = audiobook?.Title,
            DownloadId = job.DownloadId,
            EventType = HistoryEvents.ScanFailed,
            Outcome = HistoryOutcome.Failed,
            Source = "LibraryScan",
            Message = "Library scan failed",
            Error = error,
            Timestamp = _timeProvider.GetUtcNow().UtcDateTime,
            CorrelationId = correlationId,
            IdempotencyKey = idempotencyKey,
            Data = JsonSerializer.Serialize(new { ScanJobId = job.Id, job.Path })
        }, cancellationToken);
        return new ScanTerminalDecision("Failed", error, MoveOwned: false);
    }

    private Task<ScanTerminalDecision> RecordMoveScanSupersededAsync(
        ScanJob job,
        string error,
        CancellationToken cancellationToken) =>
        CommitTerminalDecisionAsync(
            job,
            async commitToken =>
            {
                if (!job.MoveScanHandoffId.HasValue || _moveScanHandoffStore == null)
                {
                    return new ScanTerminalDecision(
                        "Superseded",
                        error,
                        MoveOwned: false);
                }

                var result = await _moveScanHandoffStore.CompleteAttemptAsync(
                    job.MoveScanHandoffId.Value,
                    job.MoveScanAttemptGeneration,
                    job.Id,
                    MoveScanTerminalOutcome.Superseded,
                    error,
                    found: 0,
                    created: 0,
                    scanPath: job.Path,
                    _timeProvider.GetUtcNow(),
                    commitToken);
                return ToTerminalDecision(result);
            },
            cancellationToken);

    private async Task<ScanTerminalDecision> RecordMoveScanFailureAsync(
        IHistoryRepository historyRepository,
        ScanJob job,
        Audiobook? audiobook,
        string error,
        CancellationToken cancellationToken)
    {
        return await CommitTerminalDecisionAsync(
            job,
            commitToken => RecordScanFailureHistoryAsync(
                historyRepository,
                job,
                audiobook,
                error,
                commitToken),
            cancellationToken);
    }

    private async Task<ScanTerminalDecision> CommitTerminalDecisionAsync(
        ScanJob job,
        Func<CancellationToken, Task<ScanTerminalDecision>> persistTerminalDecision,
        CancellationToken cancellationToken)
    {
        ScanTerminalDecision? committedDecision = null;
        await _queue.CommitTerminalJobStatusAsync(
            job.Id,
            async () =>
            {
                committedDecision = await persistTerminalDecision(
                    CancellationToken.None);
                return (
                    committedDecision.Status,
                    committedDecision.Error);
            },
            cancellationToken);
        return committedDecision
            ?? throw new InvalidOperationException(
                "Terminal scan persistence completed without a terminal decision.");
    }

    private static ScanTerminalDecision ToTerminalDecision(MoveScanAttemptResult result) =>
        result.Outcome switch
        {
            MoveScanAttemptOutcome.Completed => new ScanTerminalDecision("Completed", null, MoveOwned: true),
            MoveScanAttemptOutcome.Failed => new ScanTerminalDecision("Failed", result.Error, MoveOwned: true),
            _ => new ScanTerminalDecision(
                "Superseded",
                "A newer move scan attempt owns the durable handoff.",
                MoveOwned: true)
        };

    private void ApplyTerminalStatus(ScanJob job, ScanTerminalDecision decision)
    {
        try
        {
            _queue.UpdateJobStatus(job.Id, decision.Status, decision.Error);
        }
        catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
        {
            _logger.LogDebug(
                exception,
                "Unable to apply terminal scan status {Status} for job {JobId}",
                decision.Status,
                job.Id);
        }
    }
}
