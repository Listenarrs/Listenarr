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
            || !parent.MatchesManagedDirectoryOwnershipIdentity(
                reservation.DirectoryObjectIdentityVersion,
                reservation.DirectoryObjectIdentity,
                reservation.OwnershipToken)
            || !ReservationPathMatchesOrThrowUnavailable(
                parent,
                "The parent of a planned relocation directory is temporarily unavailable."))
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
            || !parent.MatchesManagedDirectoryOwnershipIdentity(
                reservation.DirectoryObjectIdentityVersion,
                reservation.DirectoryObjectIdentity,
                reservation.OwnershipToken)
            || !ReservationPathMatchesOrThrowUnavailable(
                parent,
                "The planned relocation directory parent is temporarily unavailable."))
        {
            throw new InvalidOperationException(
                "A planned relocation directory lost its parent-generation authorization.");
        }
    }

    private void CaptureCreatedReservation(
        RootFolderRelocationCreatedDirectory reservation,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory)
    {
        if (!ReservationPathMatchesOrThrowUnavailable(
                directory,
                "The relocation-created directory is temporarily unavailable before enrollment."))
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
        if (!ReservationPathMatchesOrThrowUnavailable(
                directory,
                "The observed relocation directory is temporarily unavailable before retention."))
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

    private static bool ReservationPathMatchesOrThrowUnavailable(
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory,
        string unavailableMessage)
    {
        var outcome = directory.ProbeVisiblePathMatch();
        if (outcome == RegistrationPublicationMatchOutcome.Unavailable)
        {
            throw new IOException(unavailableMessage);
        }

        return outcome == RegistrationPublicationMatchOutcome.Match;
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
