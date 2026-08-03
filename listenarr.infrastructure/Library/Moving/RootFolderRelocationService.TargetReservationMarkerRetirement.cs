using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private async Task RetireRetainedRelocationReservationMarkersAsync(
        Guid relocationId,
        CancellationToken cancellationToken)
    {
        await using var db =
            await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var reservations = await db.RootFolderRelocationCreatedDirectories
            .AsNoTracking()
            .Where(candidate =>
                candidate.RelocationId == relocationId
                && candidate.State ==
                    RootFolderRelocationCreatedDirectoryState.Retained)
            .OrderBy(candidate => candidate.CanonicalPath.Length)
            .ToListAsync(cancellationToken);
        foreach (var reservation in reservations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var canonicalPath = RequireHostReservationPath(
                    reservation.CanonicalPath);
                var parentPath = Path.GetDirectoryName(canonicalPath)
                    ?? throw new InvalidOperationException(
                        "A retained relocation directory has no parent.");
                using var parent =
                    PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                        parentPath,
                        createMissing: false);
                using var publication =
                    parent.TryOpenExistingChildForPublication(
                        Path.GetFileName(canonicalPath));
                if (publication == null)
                {
                    continue;
                }

                using var directory =
                    publication.OpenCreatedDirectoryAnchor();
                ValidateReservationDirectoryIdentity(
                    reservation,
                    directory);
                using var marker = directory.TryOpenExistingFile(
                    RelocationReservationMarkerName,
                    requireDeleteAccess: true);
                if (marker == null)
                {
                    continue;
                }

                ValidateReservationMarker(
                    relocationId,
                    reservation,
                    marker);
                if (!marker.VisiblePathMatches()
                    || !directory.VisiblePathMatches()
                    || !parent.VisiblePathMatches())
                {
                    throw new InvalidOperationException(
                        "A retained relocation reservation marker changed before retirement.");
                }

                ValidateReservationMarker(
                    relocationId,
                    reservation,
                    marker);
                BeforeReservationMarkerRetirementForTest?.Invoke(
                    canonicalPath);
                marker.Delete();
                AfterReservationMarkerRetiredForTest?.Invoke(
                    canonicalPath);
            }
            catch (Exception exception) when (exception is not (
                OperationCanceledException
                    or OutOfMemoryException
                    or StackOverflowException))
            {
                // Retained directories are already permanently non-deletable.
                // Preserve uncertain marker state for the next reconciliation pass.
            }
        }
    }

    private async Task RetireAllRetainedRelocationReservationMarkersAsync(
        CancellationToken cancellationToken)
    {
        await using var db =
            await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var relocationIds = await db.RootFolderRelocationCreatedDirectories
            .AsNoTracking()
            .Where(candidate => candidate.State ==
                RootFolderRelocationCreatedDirectoryState.Retained)
            .Select(candidate => candidate.RelocationId)
            .Distinct()
            .ToListAsync(cancellationToken);
        foreach (var relocationId in relocationIds)
        {
            await RetireRetainedRelocationReservationMarkersAsync(
                relocationId,
                cancellationToken);
        }
    }
}
