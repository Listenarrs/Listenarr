namespace Listenarr.Application.Audiobooks.Jobs;

public partial class MoveQueueService
{
    public async Task<MoveRetryScheduleResult> ScheduleRetryAsync(
        Guid id,
        string leaseOwner,
        int leaseGeneration,
        string error,
        CancellationToken cancellationToken = default)
    {
        var result = await ScheduleRetryWithoutNotificationAsync(
            id,
            leaseOwner,
            leaseGeneration,
            error,
            cancellationToken);
        await NotifyCommittedJobStateAsync(
            id,
            result.Status,
            BuildReportedRetryError(result.Status, error),
            cancellationToken);
        return result;
    }

    public async Task<MoveRetryScheduleResult> ScheduleRetryWithoutNotificationAsync(
        Guid id,
        string leaseOwner,
        int leaseGeneration,
        string error,
        CancellationToken cancellationToken = default)
    {
        var job = await _persistence.GetByIdAsync(id, cancellationToken)
            ?? throw new MoveLeaseLostException(id, leaseGeneration);
        var nextAttemptCount = job.AttemptCount + 1;
        var now = _timeProvider.GetUtcNow();
        var nextAttemptAt = now.Add(
            MoveTimingPolicy.GetRetryDelay(id, nextAttemptCount));
        return await _persistence.ScheduleRetryAsync(
            id,
            leaseOwner,
            leaseGeneration,
            job.AttemptCount,
            now,
            nextAttemptAt,
            MoveTimingPolicy.MaxTransientAttempts,
            error,
            cancellationToken)
            ?? throw new MoveLeaseLostException(id, leaseGeneration);
    }

    private static string BuildReportedRetryError(
        MoveJobStatus status,
        string error) =>
        status == MoveJobStatus.NeedsAttention
            ? $"{error} Automatic retry limit exhausted; operator attention is required."
            : error;
}
