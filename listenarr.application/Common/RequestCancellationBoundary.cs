namespace Listenarr.Application.Common;

/// <summary>
/// Defines the transition from request-cancelable preflight into a phase that
/// must complete once durable state or filesystem mutation can begin.
/// </summary>
public static class RequestCancellationBoundary
{
    /// <summary>
    /// Re-observes request cancellation immediately before an irreversible
    /// mutation or durable commit and returns the token for the noncancelable
    /// completion phase.
    /// </summary>
    public static CancellationToken EnterNonCancelablePhase(
        CancellationToken requestCancellationToken)
    {
        requestCancellationToken.ThrowIfCancellationRequested();
        return CancellationToken.None;
    }
}
