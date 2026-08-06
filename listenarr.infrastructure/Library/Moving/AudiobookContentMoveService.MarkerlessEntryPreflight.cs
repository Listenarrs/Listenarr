namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task ValidateMoveSourceRootForExecutionAsync(
        Guid jobId,
        string source,
        int executionProtocolVersion,
        CancellationToken cancellationToken)
    {
        if (executionProtocolVersion >= MoveExecutionProtocol.MarkerlessDatabaseState
            && !Directory.Exists(source)
            && !File.Exists(source))
        {
            var endpoints = await GetEndpointObjectIdentitiesAsync(
                jobId,
                cancellationToken);
            if (endpoints.SourceDirectoryCleanupState is
                MoveJobEntryCleanupState.DeletionAuthorized
                    or MoveJobEntryCleanupState.Deleted)
            {
                return;
            }
        }

        ValidateMoveSourceRoot(source);
    }
}
