using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private async Task PersistOrValidatePlannedParentIdentityAsync(
        ListenArrDbContext db,
        RootFolderRelocationCreatedDirectory reservation,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        CancellationToken cancellationToken)
    {
        var expected = ManagedDirectoryIdentity.Create(
            reservation.OwnershipToken,
            parent.GetDirectoryObjectIdentity());
        if (reservation.DirectoryObjectIdentityVersion == null
            && string.IsNullOrWhiteSpace(
                reservation.DirectoryObjectIdentity))
        {
            reservation.DirectoryObjectIdentityVersion =
                ManagedDirectoryIdentity.CurrentVersion;
            reservation.DirectoryObjectIdentity = expected;
            reservation.UpdatedAt =
                timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken);
            AfterReservationParentIntentPersistedForTest?.Invoke(
                reservation.CanonicalPath);
            return;
        }

        if (reservation.DirectoryObjectIdentityVersion
                != ManagedDirectoryIdentity.CurrentVersion
            || !string.Equals(
                reservation.DirectoryObjectIdentity,
                expected,
                StringComparison.Ordinal)
            || !parent.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The parent of a planned relocation directory changed before creation.");
        }
    }

    private static void ValidatePlannedReservationParent(
        RootFolderRelocationCreatedDirectory reservation,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent)
    {
        if (reservation.DirectoryObjectIdentityVersion
                != ManagedDirectoryIdentity.CurrentVersion
            || !string.Equals(
                reservation.DirectoryObjectIdentity,
                ManagedDirectoryIdentity.Create(
                    reservation.OwnershipToken,
                    parent.GetDirectoryObjectIdentity()),
                StringComparison.Ordinal)
            || !parent.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "A planned relocation directory lost its parent-generation authorization.");
        }
    }

    private void CaptureCreatedReservation(
        RootFolderRelocationCreatedDirectory reservation,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory)
    {
        if (!directory.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "A relocation-created directory changed before enrollment.");
        }

        reservation.State =
            RootFolderRelocationCreatedDirectoryState.Created;
        reservation.DirectoryObjectIdentityVersion =
            ManagedDirectoryIdentity.CurrentVersion;
        reservation.DirectoryObjectIdentity =
            ManagedDirectoryIdentity.Create(
                reservation.OwnershipToken,
                directory.GetDirectoryObjectIdentity());
        reservation.UpdatedAt =
            timeProvider.GetUtcNow().UtcDateTime;
    }

    private void RetainObservedReservation(
        RootFolderRelocationCreatedDirectory reservation,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory)
    {
        if (!directory.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "An observed relocation directory changed before retention.");
        }

        reservation.State =
            RootFolderRelocationCreatedDirectoryState.Retained;
        reservation.DirectoryObjectIdentityVersion =
            ManagedDirectoryIdentity.CurrentVersion;
        reservation.DirectoryObjectIdentity =
            ManagedDirectoryIdentity.Create(
                reservation.OwnershipToken,
                directory.GetDirectoryObjectIdentity());
        reservation.UpdatedAt =
            timeProvider.GetUtcNow().UtcDateTime;
    }

    private static void ValidateReservationPathIsDirectChild(
        RootFolderRelocationCreatedDirectory reservation,
        string parentPath)
    {
        var expectedParent = Path.GetDirectoryName(
            RequireHostReservationPath(reservation.CanonicalPath));
        if (string.IsNullOrWhiteSpace(expectedParent)
            || !string.Equals(
                RequireHostReservationPath(expectedParent),
                RequireHostReservationPath(parentPath),
                PathComparison))
        {
            throw new InvalidOperationException(
                "A relocation reservation escaped its persisted parent chain.");
        }
    }

    private static string RequireHostReservationPath(string path)
    {
        if (!FileSystemPathIdentity
            .TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                path,
                out var canonicalPath,
                out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        return canonicalPath;
    }
}
