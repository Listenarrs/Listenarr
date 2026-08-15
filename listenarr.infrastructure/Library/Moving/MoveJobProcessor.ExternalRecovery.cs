using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving;

internal partial class MoveJobProcessor
{
    private async Task<bool> EnsureNoExternalRecoveryOwnerAsync(
        MoveJob job,
        CancellationToken cancellationToken)
    {
        string? error = null;
        if (fileRenameRecoveryProbe != null
            && await fileRenameRecoveryProbe.HasBlockingAsync(
                job.AudiobookId,
                cancellationToken))
        {
            error = "An interrupted file organize operation owns this audiobook's filesystem state. Complete restart recovery before resuming the move.";
        }
        else if (deletionIntentProbe != null
            && await deletionIntentProbe.HasActiveAsync(
                job.AudiobookId,
                cancellationToken))
        {
            error = "An audiobook deletion owns this audiobook's filesystem state. Complete or repair that deletion before resuming the move.";
        }

        if (error == null)
        {
            return true;
        }

        await UpdateJobStatusAsync(
            job,
            MoveJobStatus.NeedsAttention,
            error,
            cancellationToken);
        metrics.Increment("worker.move.job.needs_attention");
        logger.LogWarning(
            "Move job {JobId} stopped before filesystem mutation because another durable recovery workflow owns audiobook {AudiobookId}",
            job.Id,
            job.AudiobookId);
        return false;
    }
}
