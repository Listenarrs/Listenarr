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

    private static void ValidateReservationDirectoryIdentity(
        RootFolderRelocationCreatedDirectory reservation,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory)
    {
        if (reservation.State is not (
                RootFolderRelocationCreatedDirectoryState.Created
                    or RootFolderRelocationCreatedDirectoryState.Retained)
            || !directory.MatchesManagedDirectoryOwnershipIdentity(
                reservation.DirectoryObjectIdentityVersion,
                reservation.DirectoryObjectIdentity,
                reservation.OwnershipToken)
            || !ReservationPathMatchesOrThrowUnavailable(
                directory,
                "The relocation directory reservation is temporarily unavailable during identity validation."))
        {
            throw new InvalidOperationException(
                "A relocation directory reservation lacks matching physical identity.");
        }
    }

    private static StringComparison PathComparison => StringComparison.Ordinal;

    private sealed record TargetReservationPlan(
        string ExistingAncestor,
        string ExistingAncestorIdentity,
        IReadOnlyList<string> Segments);
}
