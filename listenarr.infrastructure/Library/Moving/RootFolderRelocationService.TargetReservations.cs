using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    internal Action<string>? TargetReservationDirectoryFlushedForTest
    {
        get;
        set;
    }
    internal Action<string>? AfterReservationParentIntentPersistedForTest
    {
        get;
        set;
    }
    internal Action<string>? AfterTargetReservationStatePersistedForTest
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
            ValidateReservationPathIsDirectChild(
                reservation,
                parent.FullPath);
            using var publication =
                parent.TryOpenExistingChildForPublication(
                    Path.GetFileName(canonicalPath));
            if (publication == null)
            {
                reservation.State =
                    RootFolderRelocationCreatedDirectoryState.Removed;
                reservation.UpdatedAt =
                    timeProvider.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }

            using var directory = publication.OpenCreatedDirectoryAnchor();
            if (reservation.State ==
                RootFolderRelocationCreatedDirectoryState.Planned)
            {
                ValidatePlannedReservationParent(
                    reservation,
                    parent);
                // A directory visible while its reservation is still only Planned
                // cannot be proven as Listenarr-created after a crash without writing
                // sidecar evidence into the library. Preserve it as retained instead.
                RetainObservedReservation(
                    reservation,
                    directory);
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }
            else
            {
                ValidateReservationDirectoryIdentity(
                    reservation,
                    directory);
            }

            if (Directory.EnumerateFileSystemEntries(canonicalPath).Any()
                || !ReservationPathMatchesOrThrowUnavailable(
                    directory,
                    "The reserved relocation directory is temporarily unavailable during cleanup.")
                || !ReservationPathMatchesOrThrowUnavailable(
                    parent,
                    "The reserved relocation directory parent is temporarily unavailable during cleanup."))
            {
                reservation.State =
                    RootFolderRelocationCreatedDirectoryState.Retained;
                reservation.UpdatedAt =
                    timeProvider.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }

            publication.DeletePinnedEmptyDirectoryImmediately(
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
            if (reservation.State is not (
                    RootFolderRelocationCreatedDirectoryState.Created
                        or RootFolderRelocationCreatedDirectoryState.Retained))
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
            ValidateReservationPathIsDirectChild(
                reservation,
                parent.FullPath);
            using var publication =
                parent.TryOpenExistingChildForPublication(
                    Path.GetFileName(canonicalPath))
                ?? throw new InvalidOperationException(
                    "A relocation target directory disappeared before finalization.");
            using var directory = publication.OpenCreatedDirectoryAnchor();
            ValidateReservationDirectoryIdentity(
                reservation,
                directory);
            if (!ReservationPathMatchesOrThrowUnavailable(
                    directory,
                    "The relocation target directory is temporarily unavailable during finalization.")
                || !ReservationPathMatchesOrThrowUnavailable(
                    parent,
                    "The relocation target directory parent is temporarily unavailable during finalization."))
            {
                throw new InvalidOperationException(
                    "A relocation target directory changed during finalization.");
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
                if (reservation.State ==
                    RootFolderRelocationCreatedDirectoryState.Removed)
                {
                    throw new InvalidOperationException(
                        "A removed relocation target reservation cannot be reused.");
                }

                var canonicalPath = RequireHostReservationPath(
                    reservation.CanonicalPath);
                ValidateReservationPathIsDirectChild(
                    reservation,
                    current.FullPath);
                var childName = Path.GetFileName(canonicalPath);
                bool childAlreadyExists;
                using (var existing =
                    current.TryOpenExistingChildForPublication(childName))
                {
                    childAlreadyExists = existing != null;
                }
                if (reservation.State ==
                    RootFolderRelocationCreatedDirectoryState.Planned)
                {
                    if (childAlreadyExists
                        && reservation.DirectoryObjectIdentityVersion == null
                        && string.IsNullOrWhiteSpace(
                            reservation.DirectoryObjectIdentity))
                    {
                        throw new InvalidOperationException(
                            "A relocation child appeared before its parent-generation intent was persisted.");
                    }
                    await PersistOrValidatePlannedParentIdentityAsync(
                        db,
                        reservation,
                        current,
                        cancellationToken);
                }

                PinnedDirectoryCreation.PinnedDirectoryAnchor? next = null;
                try
                {
                    using var creation =
                        current.TryCreateChildForPublication(childName);
                    if (creation.Created && creation.CreationGenerationIsProvable)
                    {
                        next = creation.OpenCreatedDirectoryAnchor();
                        next.FlushDirectoryEntry();
                        current.FlushDirectoryEntry();
                        TargetReservationDirectoryFlushedForTest?.Invoke(
                            canonicalPath);
                        CaptureCreatedReservation(
                            reservation,
                            next);
                        await db.SaveChangesAsync(cancellationToken);
                        AfterTargetReservationStatePersistedForTest?.Invoke(
                            canonicalPath);
                    }
                    else
                    {
                        next = current.OpenExistingChild(childName);
                        if (reservation.State ==
                            RootFolderRelocationCreatedDirectoryState.Planned)
                        {
                            if (Directory.EnumerateFileSystemEntries(
                                    canonicalPath).Any())
                            {
                                throw new InvalidOperationException(
                                    "An unproven relocation target directory contains content.");
                            }
                            RetainObservedReservation(
                                reservation,
                                next);
                            await db.SaveChangesAsync(cancellationToken);
                            AfterTargetReservationStatePersistedForTest?.Invoke(
                                canonicalPath);
                        }
                        else
                        {
                            ValidateReservationDirectoryIdentity(
                                reservation,
                                next);
                        }
                    }

                    if (!ReservationPathMatchesOrThrowUnavailable(
                            next,
                            "The relocation target reservation is temporarily unavailable before use.")
                        || !ReservationPathMatchesOrThrowUnavailable(
                            current,
                            "The relocation target reservation parent is temporarily unavailable before use."))
                    {
                        throw new InvalidOperationException(
                            "A relocation target reservation changed before use.");
                    }

                    current.Dispose();
                    current = next;
                    next = null;
                }
                finally
                {
                    next?.Dispose();
                }
            }

            if (!ReservationPathMatchesOrThrowUnavailable(
                    current,
                    "The reserved relocation target is temporarily unavailable before use."))
            {
                throw new InvalidOperationException(
                    "The reserved relocation target changed before use.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion,
                ManagedDirectoryIdentity.CreateMarkerless(
                    current.GetDirectoryObjectIdentity()),
                null);
        }
        finally
        {
            current.Dispose();
        }
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
        persisted.Status = RootFolderRelocationStatus.NeedsAttention;
        persisted.Error =
            $"Relocation target reservation requires attention: {exception.Message}";
        persisted.UpdatedAt =
            timeProvider.GetUtcNow().UtcDateTime;
        await recoveryDb.SaveChangesAsync(cancellationToken);
    }
}
