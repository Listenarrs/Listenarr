namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task CleanupTerminalMarkerlessTargetDirectoriesAsync(
        AudiobookContentMoveRequest request,
        CancellationToken cancellationToken)
    {
        var directories = (await GetCreatedDirectoriesAsync(
                request.JobId,
                cancellationToken))
            .OrderByDescending(directory => GetPathDepth(directory.Path))
            .ToList();
        foreach (var planned in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateMarkerlessTargetDirectoryLedgerPath(
                planned.Path,
                request.Target,
                request.TargetSemantics);
            if (planned.State is MoveCreatedDirectoryState.Removed
                or MoveCreatedDirectoryState.Retained)
            {
                continue;
            }
            if (!TryGetMarkerlessPathAttributes(
                    planned.Path,
                    out var plannedAttributes))
            {
                await UpdateCreatedDirectoryStateAsync(
                    request.JobId,
                    request.LeaseToken,
                    planned.Path,
                    MoveCreatedDirectoryState.Removed,
                    cancellationToken);
                planned.State = MoveCreatedDirectoryState.Removed;
                continue;
            }
            if ((plannedAttributes & FileAttributes.Directory) == 0
                || (plannedAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new MoveNeedsAttentionException(
                    $"A markerless move-created directory path is occupied by a file or link: {planned.Path}");
            }

            // Created-directory rows survive process boundaries. They prove
            // historical provenance, not current authority to remove the pathname.
            // Preserve any directory that still exists and retire only the recovery
            // metadata; empty scaffolding is safer than deleting a path that may have
            // been reused since the creating operation lost its live handle.
            await UpdateCreatedDirectoryStateAsync(
                request.JobId,
                request.LeaseToken,
                planned.Path,
                MoveCreatedDirectoryState.Retained,
                cancellationToken);
            planned.State = MoveCreatedDirectoryState.Retained;
        }
    }
}
