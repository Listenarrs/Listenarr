using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private async Task<bool>
        TryRecoverRelocationTargetReservationEnrollmentAsync(
            Guid relocationId,
            CancellationToken cancellationToken)
    {
        string targetPath;
        await using (var preflight =
            await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var relocation = await preflight.RootFolderRelocations
                .AsNoTracking()
                .SingleAsync(
                    candidate => candidate.Id == relocationId,
                    cancellationToken);
            if (relocation.Status !=
                    RootFolderRelocationStatus.NeedsAttention
                || relocation.TargetIdentityEnrollmentState !=
                    TargetIdentityEnrollmentState.Unavailable)
            {
                return false;
            }

            targetPath = relocation.TargetPath;
        }

        DirectoryObjectIdentityResolution identity;
        try
        {
            identity = await ReserveRelocationTargetAsync(
                relocationId,
                targetPath,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException
                or OutOfMemoryException
                or StackOverflowException))
        {
            await PersistReservationRecoveryFailureAsync(
                relocationId,
                exception,
                CancellationToken.None);
            return false;
        }

        if (!identity.IsAvailable)
        {
            return false;
        }

        await using var db =
            await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        var persisted = await db.RootFolderRelocations
            .SingleAsync(
                candidate => candidate.Id == relocationId,
                cancellationToken);
        if (persisted.Status !=
                RootFolderRelocationStatus.NeedsAttention
            || persisted.TargetIdentityEnrollmentState !=
                TargetIdentityEnrollmentState.Unavailable)
        {
            return false;
        }

        persisted.TargetDirectoryObjectIdentityVersion =
            identity.Version;
        persisted.TargetDirectoryObjectIdentity = identity.Value;
        persisted.TargetDirectoryObjectIdentityUnavailableReason = null;
        persisted.TargetIdentityEnrollmentState =
            TargetIdentityEnrollmentState.Authorized;
        persisted.Error =
            "Target directory reservations were recovered and the relocation can be retried.";
        persisted.UpdatedAt =
            timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
        return true;
    }

    private async Task PersistReservationRecoveryFailureAsync(
        Guid relocationId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await using var db =
            await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var persisted = await db.RootFolderRelocations
            .SingleAsync(
                candidate => candidate.Id == relocationId,
                cancellationToken);
        if (persisted.Status ==
                RootFolderRelocationStatus.NeedsAttention
            && persisted.TargetIdentityEnrollmentState ==
                TargetIdentityEnrollmentState.Unavailable)
        {
            persisted.Error =
                $"Target directory reservation recovery failed safely: {exception.Message}";
            persisted.UpdatedAt =
                timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task PersistFailedReservationCleanupAttentionAsync(
        Guid relocationId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await using var db =
            await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var persisted = await db.RootFolderRelocations
            .Include(candidate => candidate.CreatedDirectories)
            .SingleAsync(
                candidate => candidate.Id == relocationId,
                cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var reservation in persisted.CreatedDirectories.Where(
            candidate => candidate.State is
                RootFolderRelocationCreatedDirectoryState.Planned
                    or RootFolderRelocationCreatedDirectoryState.Created))
        {
            reservation.State =
                RootFolderRelocationCreatedDirectoryState.Retained;
            reservation.UpdatedAt = now;
        }

        persisted.Error =
            $"Relocation target reservation cleanup was retained for safety: {exception.Message}";
        persisted.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }
}
