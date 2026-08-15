using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Common;

[Trait("Name", "RequestCancellationBoundaryTests")]
[Trait("Category", "Application")]
public sealed class RequestCancellationBoundaryTests : BaseTests
{
    [Fact]
    public void EnterNonCancelablePhase_RequestStillActive_ReturnsNoncancelableToken()
    {
        using var cancellation = new CancellationTokenSource();

        var completionToken = RequestCancellationBoundary.EnterNonCancelablePhase(
            cancellation.Token);

        Assert.Equal(CancellationToken.None, completionToken);
        Assert.False(completionToken.CanBeCanceled);
    }

    [Fact]
    public void EnterNonCancelablePhase_RequestAlreadyCancelled_ThrowsBeforePhaseEntry()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            RequestCancellationBoundary.EnterNonCancelablePhase(cancellation.Token));
    }
}
