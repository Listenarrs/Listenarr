namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    public async Task CleanupTerminalTargetScaffoldingAsync(
        AudiobookContentMoveRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureCurrentExecutionProtocolAsync(request.JobId, cancellationToken);
        request = await WithBoundaryAuthorizationAsync(request, cancellationToken);
        await CleanupTerminalMarkerlessTargetDirectoriesAsync(
            request,
            cancellationToken);
    }
}
