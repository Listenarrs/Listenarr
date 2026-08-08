namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    public async Task CleanupTerminalTargetScaffoldingAsync(
        AudiobookContentMoveRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureCurrentExecutionProtocolAsync(request.JobId, cancellationToken);
        await CleanupTerminalMarkerlessTargetDirectoriesAsync(
            request,
            cancellationToken);
    }
}
