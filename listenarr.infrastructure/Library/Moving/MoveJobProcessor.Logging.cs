using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving;

internal partial class MoveJobProcessor
{
    private void LogCleanupBoundary(MoveJob job, MoveCleanupBoundaryResolution resolution)
    {
        if (!job.DeleteEmptySource)
        {
            return;
        }

        if (resolution.IsAvailable)
        {
            logger.LogInformation(
                "Using {BoundaryKind} source cleanup boundary {Boundary} for move job {JobId}",
                resolution.Kind,
                LogRedaction.SanitizeFilePath(resolution.Boundary),
                job.Id);
        }
        else
        {
            logger.LogWarning(
                "Move job {JobId} has no safe source cleanup boundary: {Reason}",
                job.Id,
                resolution.Reason ?? "boundary unavailable");
        }
    }
}
