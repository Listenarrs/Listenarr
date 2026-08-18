using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving;

internal partial class MoveJobProcessor
{
    private async Task<bool> EnsureNoExternalRecoveryOwnerAsync(
        MoveJob job,
        CancellationToken cancellationToken)
    {
        string? error = null;
        if (fileRegistrationRecoveryProbe != null
            && await fileRegistrationRecoveryProbe.HasBlockingAsync(
                job.AudiobookId,
                cancellationToken))
        {
            error = "A committed file import still owns source-cleanup state for this audiobook. Complete that recovery before resuming the move.";
        }
        else if (fileRenameRecoveryProbe != null
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

        return await ApplyExternalRecoveryConflictAsync(
            job,
            error,
            cancellationToken);
    }

    private async Task<bool> EnsureNoExternalRecoveryBoundaryOwnerAsync(
        MoveJob job,
        string source,
        PathIdentitySnapshot sourceIdentity,
        string target,
        PathIdentitySnapshot targetIdentity,
        CancellationToken cancellationToken)
    {
        if (fileRegistrationRecoveryProbe == null)
        {
            return true;
        }

        var sourceBoundary = job.RelocationId.HasValue
            && !string.IsNullOrWhiteSpace(job.SourceCleanupBoundary)
                ? job.SourceCleanupBoundary
                : source;
        var targetBoundary = job.RelocationId.HasValue
            ? targetIdentity.BoundaryPath
            : target;
        var blocksSource = await fileRegistrationRecoveryProbe.HasBlockingBoundaryAsync(
            sourceBoundary,
            sourceIdentity.Semantics,
            cancellationToken);
        var blocksTarget = !blocksSource
            && await fileRegistrationRecoveryProbe.HasBlockingBoundaryAsync(
                targetBoundary,
                targetIdentity.Semantics,
                cancellationToken);
        var error = blocksSource || blocksTarget
            ? "An unresolved file publication still owns a source or destination path touched by this move. Complete file-registration recovery before resuming the move."
            : null;
        return await ApplyExternalRecoveryConflictAsync(
            job,
            error,
            cancellationToken);
    }

    private async Task<bool> ApplyExternalRecoveryConflictAsync(
        MoveJob job,
        string? error,
        CancellationToken cancellationToken)
    {
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
