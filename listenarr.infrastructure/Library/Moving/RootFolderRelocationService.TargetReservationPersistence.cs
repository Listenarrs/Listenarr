using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private async Task PersistTargetReservationPlanAsync(
        Guid relocationId,
        TargetReservationPlan plan,
        CancellationToken cancellationToken)
    {
        await using var db =
            await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        _ = await db.RootFolderRelocations.SingleOrDefaultAsync(
            candidate => candidate.Id == relocationId,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "The relocation must be durably committed before target reservation.");
        var existing = await db.RootFolderRelocationCreatedDirectories
            .Where(candidate => candidate.RelocationId == relocationId)
            .OrderBy(candidate => candidate.CanonicalPath.Length)
            .ToListAsync(cancellationToken);
        if (existing.Count == 0)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            for (var index = 0; index < plan.Segments.Count; index++)
            {
                var ownershipToken = Guid.NewGuid().ToString("N");
                db.RootFolderRelocationCreatedDirectories.Add(new()
                {
                    RelocationId = relocationId,
                    CanonicalPath = plan.Segments[index],
                    OwnershipToken = ownershipToken,
                    State =
                        RootFolderRelocationCreatedDirectoryState.Planned,
                    DirectoryObjectIdentityVersion =
                        index == 0
                            ? ManagedDirectoryIdentity.CurrentVersion
                            : null,
                    DirectoryObjectIdentity =
                        index == 0
                            ? ManagedDirectoryIdentity.Create(
                                ownershipToken,
                                plan.ExistingAncestorIdentity)
                            : null,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
        }
        else if (existing.Count != plan.Segments.Count
            || existing.Zip(plan.Segments).Any(pair =>
                !string.Equals(
                    RequireHostReservationPath(pair.First.CanonicalPath),
                    RequireHostReservationPath(pair.Second),
                    PathComparison)
                || !Guid.TryParseExact(
                    pair.First.OwnershipToken,
                    "N",
                    out _)))
        {
            throw new InvalidOperationException(
                "The persisted relocation target reservation generation does not match the requested target.");
        }

        await db.SaveChangesAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
    }

    private async Task EnsureReservationParentMarkerAsync(
        Guid relocationId,
        RootFolderRelocationCreatedDirectory reservation,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        bool allowPublication,
        CancellationToken cancellationToken)
    {
        var nativeIdentity = parent.GetDirectoryObjectIdentity();
        var expectedParentIdentity = ManagedDirectoryIdentity.Create(
            reservation.OwnershipToken,
            nativeIdentity);
        if (reservation.State
                == RootFolderRelocationCreatedDirectoryState.Planned
            && (reservation.DirectoryObjectIdentityVersion
                    != ManagedDirectoryIdentity.CurrentVersion
                || !string.Equals(
                    reservation.DirectoryObjectIdentity,
                    expectedParentIdentity,
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The parent of a planned relocation directory changed before creation.");
        }

        var fileName = GetReservationParentMarkerName(reservation);
        using var existing = parent.TryOpenExistingFile(
            fileName,
            requireDeleteAccess: false);
        if (existing != null)
        {
            ValidateReservationParentMarker(
                relocationId,
                reservation,
                parent,
                existing);
            return;
        }

        var temporaryName = fileName + ".tmp";
        using var interrupted = parent.TryOpenExistingFile(
            temporaryName,
            requireDeleteAccess: true);
        if (interrupted != null)
        {
            ValidateReservationParentMarker(
                relocationId,
                reservation,
                parent,
                interrupted);
            interrupted.MoveWithinParent(fileName);
            parent.FlushDirectoryEntry();
            ValidateReservationParentMarker(
                relocationId,
                reservation,
                parent);
            AfterReservationParentMarkerPublishedForTest?.Invoke(
                reservation.CanonicalPath);
            return;
        }

        if (!allowPublication)
        {
            throw new InvalidOperationException(
                "A published relocation child has no durable parent reservation intent.");
        }

        var payload = new TargetReservationParentMarker(
            1,
            relocationId,
            reservation.OwnershipToken,
            reservation.CanonicalPath,
            expectedParentIdentity);
        await parent.PublishNewFileAsync(
            temporaryName,
            fileName,
            beforeCreateAsync: () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            writeAndFlushAsync: async stream =>
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    payload,
                    ReservationJsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            },
            beforePublicationAsync: () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            preserveTemporaryFileOnFailure: _ => false);
        parent.FlushDirectoryEntry();
        ValidateReservationParentMarker(
            relocationId,
            reservation,
            parent);
        AfterReservationParentMarkerPublishedForTest?.Invoke(
            reservation.CanonicalPath);
    }

    private static void RetireReservationParentMarker(
        Guid relocationId,
        RootFolderRelocationCreatedDirectory reservation,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent)
    {
        var fileName = GetReservationParentMarkerName(reservation);
        using var marker = parent.TryOpenExistingFile(
            fileName,
            requireDeleteAccess: true);
        if (marker == null)
        {
            return;
        }

        ValidateReservationParentMarker(
            relocationId,
            reservation,
            parent,
            marker);
        if (!marker.VisiblePathMatches() || !parent.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "A relocation parent reservation marker changed before retirement.");
        }

        marker.Delete();
        parent.FlushDirectoryEntry();
    }

    private static void ValidateReservationParentMarker(
        Guid relocationId,
        RootFolderRelocationCreatedDirectory reservation,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent)
    {
        using var marker = parent.OpenExistingFile(
            GetReservationParentMarkerName(reservation),
            requireDeleteAccess: false);
        ValidateReservationParentMarker(
            relocationId,
            reservation,
            parent,
            marker);
    }

    private static void ValidateReservationParentMarker(
        Guid relocationId,
        RootFolderRelocationCreatedDirectory reservation,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        PinnedDirectoryCreation.PinnedFileEntry marker)
    {
        TargetReservationParentMarker? payload;
        using (var stream = marker.OpenReadStream(
            bufferSize: 4096,
            asynchronous: false))
        {
            if (stream.Length <= 0 || stream.Length > 16 * 1024)
            {
                throw new InvalidOperationException(
                    "A relocation parent reservation marker has an invalid size.");
            }

            payload = JsonSerializer.Deserialize<TargetReservationParentMarker>(
                stream,
                ReservationJsonOptions);
        }

        var expectedParentIdentity = ManagedDirectoryIdentity.Create(
            reservation.OwnershipToken,
            parent.GetDirectoryObjectIdentity());
        if (payload == null
            || payload.Version != 1
            || payload.RelocationId != relocationId
            || !string.Equals(
                payload.OwnershipToken,
                reservation.OwnershipToken,
                StringComparison.Ordinal)
            || !string.Equals(
                RequireHostReservationPath(payload.CanonicalPath),
                RequireHostReservationPath(reservation.CanonicalPath),
                PathComparison)
            || !string.Equals(
                payload.ParentDirectoryIdentity,
                expectedParentIdentity,
                StringComparison.Ordinal)
            || !marker.VisiblePathMatches()
            || !parent.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "A relocation parent reservation marker belongs to another directory generation.");
        }
    }

    private static string GetReservationParentMarkerName(
        RootFolderRelocationCreatedDirectory reservation)
    {
        if (!Guid.TryParseExact(reservation.OwnershipToken, "N", out _))
        {
            throw new InvalidOperationException(
                "A relocation reservation ownership token is invalid.");
        }

        return $"{RelocationReservationParentMarkerPrefix}{reservation.OwnershipToken}.json";
    }

    private static void WriteReservationMarker(
        Guid relocationId,
        RootFolderRelocationCreatedDirectory reservation,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory)
    {
        using var marker = directory.CreateNewFile(
            RelocationReservationMarkerName,
            hiddenFile: true);
        using (var stream = marker.OpenWriteStream(
            bufferSize: 4096,
            asynchronous: false))
        {
            var bytes = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(
                    new TargetReservationMarker(
                        1,
                        relocationId,
                        reservation.OwnershipToken,
                        reservation.CanonicalPath),
                    ReservationJsonOptions));
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        if (!marker.VisiblePathMatches()
            || !directory.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The relocation reservation marker changed during publication.");
        }
    }

    private static void ValidateReservationDirectory(
        Guid relocationId,
        RootFolderRelocationCreatedDirectory reservation,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory)
    {
        ValidateReservationDirectoryIdentity(reservation, directory);
        ValidateReservationMarker(
            relocationId,
            reservation,
            directory);
    }

    private static void ValidateReservationDirectoryIdentity(
        RootFolderRelocationCreatedDirectory reservation,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory)
    {
        if (reservation.State is not (
                RootFolderRelocationCreatedDirectoryState.Created
                    or RootFolderRelocationCreatedDirectoryState.Retained)
            || !ManagedDirectoryIdentity.Matches(
                reservation.DirectoryObjectIdentityVersion,
                reservation.DirectoryObjectIdentity,
                reservation.OwnershipToken,
                directory.GetDirectoryObjectIdentity())
            || !directory.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "A relocation directory reservation lacks matching physical identity.");
        }
    }

    private static void ValidateReservationMarker(
        Guid relocationId,
        RootFolderRelocationCreatedDirectory reservation,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory)
    {
        using var marker = directory.OpenExistingFile(
            RelocationReservationMarkerName,
            requireDeleteAccess: false);
        ValidateReservationMarker(
            relocationId,
            reservation,
            marker);
        if (!marker.VisiblePathMatches()
            || !directory.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "A relocation reservation marker changed during validation.");
        }
    }

    private static void ValidateReservationMarker(
        Guid relocationId,
        RootFolderRelocationCreatedDirectory reservation,
        PinnedDirectoryCreation.PinnedFileEntry marker)
    {
        TargetReservationMarker? payload;
        using (var stream = marker.OpenReadStream(
            bufferSize: 4096,
            asynchronous: false))
        {
            if (stream.Length <= 0 || stream.Length > 16 * 1024)
            {
                throw new InvalidOperationException(
                    "A relocation reservation marker has an invalid size.");
            }

            payload = JsonSerializer.Deserialize<TargetReservationMarker>(
                stream,
                ReservationJsonOptions);
        }

        if (payload == null
            || payload.Version != 1
            || payload.RelocationId != relocationId
            || !string.Equals(
                payload.OwnershipToken,
                reservation.OwnershipToken,
                StringComparison.Ordinal)
            || !string.Equals(
                RequireHostReservationPath(payload.CanonicalPath),
                RequireHostReservationPath(reservation.CanonicalPath),
                PathComparison))
        {
            throw new InvalidOperationException(
                "A relocation reservation marker belongs to another generation.");
        }
    }

    private void FlushReservationDirectory(
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory)
    {
        directory.FlushDirectoryEntry();
        TargetReservationDirectoryFlushedForTest?.Invoke(
            directory.FullPath);
    }

    private static bool TryEnrollPublishedPlannedReservation(
        Guid relocationId,
        RootFolderRelocationCreatedDirectory reservation,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory)
    {
        try
        {
            if (reservation.DirectoryObjectIdentityVersion
                    != ManagedDirectoryIdentity.CurrentVersion
                || string.IsNullOrWhiteSpace(
                    reservation.DirectoryObjectIdentity)
                || !string.Equals(
                    reservation.DirectoryObjectIdentity,
                    ManagedDirectoryIdentity.Create(
                        reservation.OwnershipToken,
                        parent.GetDirectoryObjectIdentity()),
                    StringComparison.Ordinal)
                || !parent.VisiblePathMatches()
                || !directory.VisiblePathMatches())
            {
                return false;
            }

            ValidateReservationParentMarker(
                relocationId,
                reservation,
                parent);
            ValidateReservationMarker(
                relocationId,
                reservation,
                directory);
            return parent.VisiblePathMatches()
                && directory.VisiblePathMatches();
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException
                or StackOverflowException))
        {
            return false;
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private sealed record TargetReservationPlan(
        string ExistingAncestor,
        string ExistingAncestorIdentity,
        IReadOnlyList<string> Segments);

    private sealed record TargetReservationMarker(
        int Version,
        Guid RelocationId,
        string OwnershipToken,
        string CanonicalPath);

    private sealed record TargetReservationParentMarker(
        int Version,
        Guid RelocationId,
        string OwnershipToken,
        string CanonicalPath,
        string ParentDirectoryIdentity);
}
