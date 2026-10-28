using Listenarr.Domain.Common;

namespace Listenarr.Domain.Audiobooks;

public static class MoveJobBoundaryConflict
{
    public static bool TouchesBoundary(
        MoveJob job,
        string boundaryPath,
        FileSystemPathSemantics boundarySemantics)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(boundaryPath);

        // This primitive answers path geometry only. Callers own the lifecycle policy
        // that decides which move jobs are relevant (active, unresolved, historical,
        // etc.); hiding an active-status filter here caused unresolved terminal moves
        // to bypass root-folder mutation fences.
        return EndpointTouchesBoundary(
                job.SourcePath,
                job.TryGetSourceIdentity(out var sourceIdentity)
                    ? sourceIdentity
                    : null,
                boundaryPath,
                boundarySemantics)
            || EndpointTouchesBoundary(
                job.RequestedPath,
                job.TryGetTargetIdentity(out var targetIdentity)
                    ? targetIdentity
                    : null,
                boundaryPath,
                boundarySemantics);
    }

    public static bool EndpointTouchesBoundary(
        string? endpointPath,
        PathIdentitySnapshot? endpointIdentity,
        string boundaryPath,
        FileSystemPathSemantics boundarySemantics)
    {
        if (string.IsNullOrWhiteSpace(endpointPath))
        {
            return false;
        }

        return FileSystemPathIdentity.StoredPathMayTouchBoundary(
            endpointPath,
            boundaryPath,
            boundarySemantics,
            endpointIdentity);
    }
}
