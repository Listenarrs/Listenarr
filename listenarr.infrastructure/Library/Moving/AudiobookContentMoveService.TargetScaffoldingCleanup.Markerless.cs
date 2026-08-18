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

            var parentPath = Path.GetDirectoryName(planned.Path)
                ?? throw new MoveNeedsAttentionException(
                    "A markerless move-created directory has no parent.");
            if (planned.State == MoveCreatedDirectoryState.Planned
                && string.IsNullOrWhiteSpace(planned.DirectoryObjectIdentity))
            {
                using var parent = OpenPinnedMoveBoundaryDescendant(
                    request,
                    parentPath,
                    request.TargetSemantics,
                    sourceBoundary: false);
                using var directory = parent.OpenExistingChild(
                    Path.GetFileName(planned.Path));
                if (!PinnedDirectoryVisibleOrThrowUnavailable(
                        directory,
                        "An unproven markerless target directory is temporarily unavailable while being retained.")
                    || !PinnedDirectoryVisibleOrThrowUnavailable(
                        parent,
                        "The parent of an unproven markerless target directory is temporarily unavailable while being retained."))
                {
                    throw new MoveNeedsAttentionException(
                        "An unproven markerless target directory changed while it was being retained.");
                }

                await UpdateCreatedDirectoryPublicationAsync(
                    request.JobId,
                    request.LeaseToken,
                    planned.Path,
                    MoveCreatedDirectoryState.Retained,
                    directory.GetDirectoryObjectIdentity(),
                    cancellationToken);
                planned.State = MoveCreatedDirectoryState.Retained;
                continue;
            }
            if (planned.State != MoveCreatedDirectoryState.Created
                || string.IsNullOrWhiteSpace(planned.DirectoryObjectIdentity))
            {
                throw new MoveNeedsAttentionException(
                    $"A markerless move-created directory has inconsistent durable state: {planned.Path}");
            }

            using var pinnedParent = OpenPinnedMoveBoundaryDescendant(
                request,
                parentPath,
                request.TargetSemantics,
                sourceBoundary: false);
            using var publication = pinnedParent.OpenExistingChildForPublication(
                Path.GetFileName(planned.Path));
            using var parentAnchor = publication.OpenParentDirectoryAnchor();
            using var directoryAnchor = publication.OpenCreatedDirectoryAnchor();
            if (!directoryAnchor.MatchesDirectoryObjectIdentity(
                    planned.DirectoryObjectIdentity)
                || !PinnedDirectoryVisibleOrThrowUnavailable(
                    directoryAnchor,
                    $"A markerless move-created directory is temporarily unavailable before terminal cleanup: {planned.Path}")
                || !PinnedDirectoryVisibleOrThrowUnavailable(
                    parentAnchor,
                    $"The parent of a markerless move-created directory is temporarily unavailable before terminal cleanup: {planned.Path}"))
            {
                throw new MoveNeedsAttentionException(
                    $"A markerless move-created directory changed physical generation before terminal cleanup: {planned.Path}");
            }
            if (Directory.EnumerateFileSystemEntries(planned.Path).Any())
            {
                await UpdateCreatedDirectoryStateAsync(
                    request.JobId,
                    request.LeaseToken,
                    planned.Path,
                    MoveCreatedDirectoryState.Retained,
                    cancellationToken);
                planned.State = MoveCreatedDirectoryState.Retained;
                continue;
            }

            await EnsureMutationAuthorizedAsync(
                request,
                request.Source,
                request.Target,
                cancellationToken);
            if (!directoryAnchor.MatchesDirectoryObjectIdentity(
                    planned.DirectoryObjectIdentity)
                || !PinnedDirectoryVisibleOrThrowUnavailable(
                    directoryAnchor,
                    $"A markerless move-created directory is temporarily unavailable before terminal retirement: {planned.Path}")
                || !PinnedDirectoryVisibleOrThrowUnavailable(
                    parentAnchor,
                    $"The parent of a markerless move-created directory is temporarily unavailable before terminal retirement: {planned.Path}"))
            {
                throw new MoveNeedsAttentionException(
                    $"A markerless move-created directory changed before terminal retirement: {planned.Path}");
            }
            if (Directory.EnumerateFileSystemEntries(planned.Path).Any())
            {
                await UpdateCreatedDirectoryStateAsync(
                    request.JobId,
                    request.LeaseToken,
                    planned.Path,
                    MoveCreatedDirectoryState.Retained,
                    cancellationToken);
                planned.State = MoveCreatedDirectoryState.Retained;
                continue;
            }

            publication.DeletePinnedEmptyDirectoryImmediately(
                Path.GetFileName(planned.Path));
            await UpdateCreatedDirectoryStateAsync(
                request.JobId,
                request.LeaseToken,
                planned.Path,
                MoveCreatedDirectoryState.Removed,
                cancellationToken);
            planned.State = MoveCreatedDirectoryState.Removed;
        }
    }
}
