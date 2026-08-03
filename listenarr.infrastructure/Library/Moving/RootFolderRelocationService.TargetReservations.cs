using System.Text.Json;
using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private const string RelocationReservationMarkerName =
        ".listenarr-relocation-directory.json";
    private const string RelocationReservationParentMarkerPrefix =
        ".listenarr-relocation-parent-";
    private static readonly JsonSerializerOptions ReservationJsonOptions =
        new(JsonSerializerDefaults.Web);
    internal Action<string>? TargetReservationDirectoryFlushedForTest
    {
        get;
        set;
    }
    internal Action<string>? AfterReservationParentMarkerPublishedForTest
    {
        get;
        set;
    }
    internal Action<string>? BeforeReservationMarkerRetirementForTest
    {
        get;
        set;
    }
    internal Action<string>? AfterReservationMarkerRetiredForTest
    {
        get;
        set;
    }

    private async Task ReconcileRelocationTargetReservationsAsync(
        Guid relocationId,
        CancellationToken cancellationToken)
    {
        await using var db =
            await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var reservations = await db.RootFolderRelocationCreatedDirectories
            .Where(candidate => candidate.RelocationId == relocationId)
            .OrderByDescending(candidate => candidate.CanonicalPath.Length)
            .ToListAsync(cancellationToken);
        foreach (var reservation in reservations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reservation.State is
                RootFolderRelocationCreatedDirectoryState.Removed
                    or RootFolderRelocationCreatedDirectoryState.Retained)
            {
                continue;
            }

            var canonicalPath = RequireHostReservationPath(
                reservation.CanonicalPath);
            var parentPath = Path.GetDirectoryName(canonicalPath)
                ?? throw new InvalidOperationException(
                    "A relocation directory reservation has no parent.");
            using var parent =
                PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                    parentPath,
                    createMissing: false);
            using var publication =
                parent.TryOpenExistingChildForPublication(
                    Path.GetFileName(canonicalPath));
            if (publication == null)
            {
                if (reservation.State ==
                    RootFolderRelocationCreatedDirectoryState.Planned)
                {
                    RetireReservationParentMarker(
                        relocationId,
                        reservation,
                        parent);
                }

                reservation.State =
                    RootFolderRelocationCreatedDirectoryState.Removed;
                reservation.UpdatedAt =
                    timeProvider.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }

            using var directory =
                publication.OpenCreatedDirectoryAnchor();
            if (reservation.State ==
                RootFolderRelocationCreatedDirectoryState.Planned)
            {
                if (!TryEnrollPublishedPlannedReservation(
                        relocationId,
                        reservation,
                        parent,
                        directory))
                {
                    reservation.State =
                        RootFolderRelocationCreatedDirectoryState.Retained;
                    reservation.UpdatedAt =
                        timeProvider.GetUtcNow().UtcDateTime;
                    await db.SaveChangesAsync(cancellationToken);
                    continue;
                }

                var nativeIdentity = directory.GetDirectoryObjectIdentity();
                reservation.State =
                    RootFolderRelocationCreatedDirectoryState.Created;
                reservation.DirectoryObjectIdentityVersion =
                    ManagedDirectoryIdentity.CurrentVersion;
                reservation.DirectoryObjectIdentity =
                    ManagedDirectoryIdentity.Create(
                        reservation.OwnershipToken,
                        nativeIdentity);
                reservation.UpdatedAt =
                    timeProvider.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(cancellationToken);
                RetireReservationParentMarker(
                    relocationId,
                    reservation,
                    parent);
            }
            else
            {
                RetireReservationParentMarker(
                    relocationId,
                    reservation,
                    parent);
            }

            ValidateReservationDirectory(
                relocationId,
                reservation,
                directory);
            ManagedDirectoryEnrollment.RetireValidMarker(directory);
            var entries = Directory.EnumerateFileSystemEntries(
                    canonicalPath)
                .Take(2)
                .ToList();
            var markerPath = Path.Join(
                canonicalPath,
                RelocationReservationMarkerName);
            if (entries.Count != 1
                || !string.Equals(
                    entries[0],
                    markerPath,
                    PathComparison)
                || !directory.VisiblePathMatches()
                || !parent.VisiblePathMatches())
            {
                reservation.State =
                    RootFolderRelocationCreatedDirectoryState.Retained;
                reservation.UpdatedAt =
                    timeProvider.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }

            using (var marker = directory.OpenExistingFile(
                RelocationReservationMarkerName,
                requireDeleteAccess: true))
            {
                ValidateReservationMarker(
                    relocationId,
                    reservation,
                    marker);
                if (!marker.VisiblePathMatches()
                    || !directory.VisiblePathMatches())
                {
                    throw new InvalidOperationException(
                        "A relocation reservation marker changed before cleanup.");
                }

                ValidateReservationMarker(
                    relocationId,
                    reservation,
                    marker);
                marker.Delete();
            }

            if (Directory.EnumerateFileSystemEntries(
                    canonicalPath).Any()
                || !directory.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "A relocation-created directory changed after marker retirement.");
            }

            publication.DeletePinnedEmptyDirectory(
                Path.GetFileName(canonicalPath));
            reservation.State =
                RootFolderRelocationCreatedDirectoryState.Removed;
            reservation.UpdatedAt =
                timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task FinalizeRelocationTargetReservationsAsync(
        ListenArrDbContext db,
        Guid relocationId,
        CancellationToken cancellationToken)
    {
        var reservations = await db.RootFolderRelocationCreatedDirectories
            .Where(candidate => candidate.RelocationId == relocationId)
            .OrderBy(candidate => candidate.CanonicalPath.Length)
            .ToListAsync(cancellationToken);
        foreach (var reservation in reservations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reservation.State ==
                RootFolderRelocationCreatedDirectoryState.Retained)
            {
                continue;
            }

            if (reservation.State !=
                RootFolderRelocationCreatedDirectoryState.Created)
            {
                throw new InvalidOperationException(
                    "A successful relocation has an incomplete target directory reservation.");
            }

            var canonicalPath = RequireHostReservationPath(
                reservation.CanonicalPath);
            var parentPath = Path.GetDirectoryName(canonicalPath)
                ?? throw new InvalidOperationException(
                    "A relocation directory reservation has no parent.");
            using var parent =
                PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                    parentPath,
                    createMissing: false);
            using var publication =
                parent.TryOpenExistingChildForPublication(
                    Path.GetFileName(canonicalPath))
                ?? throw new InvalidOperationException(
                    "A relocation-created target directory disappeared before finalization.");
            using var directory =
                publication.OpenCreatedDirectoryAnchor();
            ValidateReservationDirectoryIdentity(
                reservation,
                directory);

            using var marker = directory.TryOpenExistingFile(
                RelocationReservationMarkerName,
                requireDeleteAccess: false);
            if (marker != null)
            {
                ValidateReservationMarker(
                    relocationId,
                    reservation,
                    marker);
                if (!marker.VisiblePathMatches()
                    || !directory.VisiblePathMatches()
                    || !parent.VisiblePathMatches())
                {
                    throw new InvalidOperationException(
                        "A relocation reservation marker changed before finalization.");
                }
            }

            if (!directory.VisiblePathMatches()
                || !parent.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "A relocation-created target directory changed during finalization.");
            }

            reservation.State =
                RootFolderRelocationCreatedDirectoryState.Retained;
            reservation.UpdatedAt =
                timeProvider.GetUtcNow().UtcDateTime;
        }
    }

    private async Task<DirectoryObjectIdentityResolution>
        CreateOrReuseTargetReservationsAsync(
            Guid relocationId,
            string existingAncestor,
            CancellationToken cancellationToken)
    {
        await using var db =
            await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var reservations = await db.RootFolderRelocationCreatedDirectories
            .Where(candidate => candidate.RelocationId == relocationId)
            .OrderBy(candidate => candidate.CanonicalPath.Length)
            .ToListAsync(cancellationToken);
        existingAncestor = RequireHostReservationPath(existingAncestor);
        var current =
            PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                existingAncestor,
                createMissing: false);
        try
        {
            foreach (var reservation in reservations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reservation.State is
                    RootFolderRelocationCreatedDirectoryState.Retained
                        or RootFolderRelocationCreatedDirectoryState.Removed)
                {
                    throw new InvalidOperationException(
                        "A terminal relocation target reservation cannot be reused.");
                }

                var canonicalPath = RequireHostReservationPath(
                    reservation.CanonicalPath);
                var childName = Path.GetFileName(canonicalPath);
                var parentIdentity =
                    current.GetDirectoryObjectIdentity();
                if (reservation.State ==
                    RootFolderRelocationCreatedDirectoryState.Planned)
                {
                    var expectedParentIdentity = ManagedDirectoryIdentity.Create(
                        reservation.OwnershipToken,
                        parentIdentity);
                    if (reservation.DirectoryObjectIdentityVersion == null
                        && string.IsNullOrWhiteSpace(
                            reservation.DirectoryObjectIdentity))
                    {
                        reservation.DirectoryObjectIdentityVersion =
                            ManagedDirectoryIdentity.CurrentVersion;
                        reservation.DirectoryObjectIdentity =
                            expectedParentIdentity;
                        reservation.UpdatedAt =
                            timeProvider.GetUtcNow().UtcDateTime;
                        await db.SaveChangesAsync(cancellationToken);
                    }
                    else if (reservation.DirectoryObjectIdentityVersion
                                != ManagedDirectoryIdentity.CurrentVersion
                        || !string.Equals(
                            reservation.DirectoryObjectIdentity,
                            expectedParentIdentity,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The parent of a planned relocation directory was replaced before creation.");
                    }

                    bool childAlreadyExists;
                    using (var existingChild =
                        current.TryOpenExistingChildForPublication(childName))
                    {
                        childAlreadyExists = existingChild != null;
                    }

                    await EnsureReservationParentMarkerAsync(
                        relocationId,
                        reservation,
                        current,
                        allowPublication: !childAlreadyExists,
                        cancellationToken);
                }

                PinnedDirectoryCreation.PinnedDirectoryAnchor? next = null;
                try
                {
                    using var creation =
                        current.TryCreateChildForPublication(childName);
                    if (creation.Created)
                    {
                        next = creation.OpenCreatedDirectoryAnchor();
                        WriteReservationMarker(
                            relocationId,
                            reservation,
                            next);
                        FlushReservationDirectory(next);
                        FlushReservationDirectory(current);
                    }
                    else
                    {
                        next = current.OpenExistingChild(childName);
                        ValidateReservationMarker(
                            relocationId,
                            reservation,
                            next);
                    }

                    var liveIdentity =
                        next.GetDirectoryObjectIdentity();
                    if (reservation.State ==
                            RootFolderRelocationCreatedDirectoryState.Created
                        && !ManagedDirectoryIdentity.Matches(
                            reservation.DirectoryObjectIdentityVersion,
                            reservation.DirectoryObjectIdentity,
                            reservation.OwnershipToken,
                            liveIdentity))
                    {
                        throw new InvalidOperationException(
                            "A relocation-created directory was replaced after enrollment.");
                    }

                    reservation.State =
                        RootFolderRelocationCreatedDirectoryState.Created;
                    reservation.DirectoryObjectIdentityVersion =
                        ManagedDirectoryIdentity.CurrentVersion;
                    reservation.DirectoryObjectIdentity =
                        ManagedDirectoryIdentity.Create(
                            reservation.OwnershipToken,
                            liveIdentity);
                    reservation.UpdatedAt =
                        timeProvider.GetUtcNow().UtcDateTime;
                    await db.SaveChangesAsync(cancellationToken);
                    RetireReservationParentMarker(
                        relocationId,
                        reservation,
                        current);
                    current.Dispose();
                    current = next;
                    next = null;
                }
                finally
                {
                    next?.Dispose();
                }
            }

            if (!current.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The reserved relocation target changed before use.");
            }

            var finalNativeIdentity = current.GetDirectoryObjectIdentity();
            return await ManagedDirectoryEnrollment.ResolveAsync(
                current,
                finalNativeIdentity,
                enrollIfMissing: true,
                cancellationToken);
        }
        finally
        {
            current.Dispose();
        }
    }

    private static string RequireHostReservationPath(string path)
    {
        if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                path,
                out var canonicalPath,
                out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        return canonicalPath;
    }

    private async Task MarkPrecommittedRelocationNeedsAttentionAsync(
        Guid relocationId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await using var recoveryDb =
            await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var persisted = await recoveryDb.RootFolderRelocations
            .SingleAsync(
                candidate => candidate.Id == relocationId,
                cancellationToken);
        if (persisted.Status is
            RootFolderRelocationStatus.Pending
                or RootFolderRelocationStatus.Running)
        {
            persisted.Status =
                RootFolderRelocationStatus.NeedsAttention;
            persisted.Error =
                $"Target directory reservation requires attention: {exception.Message}";
            persisted.UpdatedAt =
                timeProvider.GetUtcNow().UtcDateTime;
            await recoveryDb.SaveChangesAsync(cancellationToken);
        }
    }

}
