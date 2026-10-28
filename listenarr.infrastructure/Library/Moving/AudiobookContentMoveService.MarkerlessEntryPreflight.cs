namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task ValidateMoveSourceRootForExecutionAsync(
        Guid jobId,
        string source,
        CancellationToken cancellationToken)
    {
        if (!TryGetMarkerlessPathAttributes(source, out _))
        {
            var endpoints = await GetEndpointObjectIdentitiesAsync(
                jobId,
                cancellationToken);
            if (endpoints.SourceDirectoryCleanupState is
                MoveJobEntryCleanupState.DeleteAuthorized
                    or MoveJobEntryCleanupState.Deleted)
            {
                return;
            }
        }

        ValidateMoveSourceRoot(source);
    }
}
