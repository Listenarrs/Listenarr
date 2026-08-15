using Listenarr.Application.Common.Exceptions;
using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Jobs;

public partial class MoveQueueService
{
    private static void EnsureMatchingActiveExecutionOptions(
        MoveJob existingJob,
        MoveEnqueueCommand command,
        string? persistedSourceBoundary)
    {
        if (ActiveExecutionOptionsMatch(
                existingJob,
                command.SourceIdentity,
                command.TargetIdentity,
                command.DeleteEmptySource,
                command.RelocationId,
                persistedSourceBoundary))
        {
            return;
        }

        throw new ApplicationConflictException(
            "move_active_options_conflict",
            "An active move already owns this source and destination with different execution options. Wait for it to finish or resolve its recovery state before retrying.");
    }

    private static bool ActiveExecutionOptionsMatch(
        MoveJob existingJob,
        PathIdentitySnapshot sourceIdentity,
        PathIdentitySnapshot targetIdentity,
        bool deleteEmptySource,
        Guid? relocationId,
        string? persistedSourceBoundary) =>
        MoveExecutionContract.Matches(
            existingJob,
            sourceIdentity,
            targetIdentity,
            deleteEmptySource,
            persistedSourceBoundary,
            relocationId);
}
