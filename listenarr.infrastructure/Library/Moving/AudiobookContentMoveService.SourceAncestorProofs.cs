using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task CaptureSourceAncestorProofsAsync(
        AudiobookContentMoveRequest request,
        string source,
        MarkerlessSourceRetirementLease lease,
        CancellationToken cancellationToken)
    {
        if (!request.DeleteEmptySource || string.IsNullOrWhiteSpace(request.SourceCleanupBoundary))
        {
            return;
        }

        var boundary = Path.GetFullPath(request.SourceCleanupBoundary);
        var current = Path.GetDirectoryName(source);
        while (current != null
            && FileSystemPathIdentity.IsSameOrInside(current, boundary, request.SourceSemantics)
            && !FileSystemPathIdentity.AreEquivalent(current, boundary, request.SourceSemantics))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ResolveOwnedDirectoryForCleanupAsync(
                current, request.SourceSemantics, cancellationToken) == null)
            {
                return;
            }

            using var directory = OpenPinnedMoveBoundaryDescendant(
                request, current, request.SourceSemantics, sourceBoundary: true);
            if (!PinnedDirectoryVisibleOrThrowUnavailable(
                directory, "The source ancestor is temporarily unavailable while pinned."))
            {
                throw new MoveNeedsAttentionException("The source ancestor changed while pinned.");
            }
            lease.AddDirectory(current, directory);
            current = Path.GetDirectoryName(current);
        }
    }
}
