using Listenarr.Application.Common.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    public async Task<RootFolderPathChangeResult> AbandonUnpublishedAsync(
        Guid relocationId,
        CancellationToken cancellationToken = default)
    {
        EnsureFilesystemMutationReady();
        var result = await _mutationCoordinator.ExecuteExclusiveAsync(
            token => ExecuteWithAllAudiobookLocksAsync(
                lockedToken => AbandonUnpublishedCoreAsync(
                    relocationId,
                    lockedToken),
                token),
            cancellationToken);
        await BroadcastAsync(result, cancellationToken);
        return result;
    }

    private async Task<RootFolderPathChangeResult> AbandonUnpublishedCoreAsync(
        Guid relocationId,
        CancellationToken cancellationToken)
    {
        await using (var preflight =
            await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var relocation = await preflight.RootFolderRelocations
                .AsNoTracking()
                .AsSplitQuery()
                .Include(candidate => candidate.MoveJobs)
                .Include(candidate => candidate.OwnershipPathMigrations)
                .Include(candidate => candidate.CreatedDirectories)
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == relocationId,
                    cancellationToken)
                ?? throw new KeyNotFoundException(
                    "Root folder relocation not found");
            var currentRootPath = relocation.RootFolderId is int rootFolderId
                ? await preflight.RootFolders
                    .AsNoTracking()
                    .Where(root => root.Id == rootFolderId)
                    .Select(root => root.Path)
                    .SingleOrDefaultAsync(cancellationToken)
                : null;
            if (string.IsNullOrWhiteSpace(currentRootPath)
                || !CanAbandonUnpublishedRelocation(
                    relocation,
                    currentRootPath))
            {
                throw new ApplicationConflictException(
                    "root_folder_relocation_cannot_abandon",
                    "This relocation cannot be abandoned automatically because move jobs or other durable recovery state may already own its filesystem changes.");
            }
        }

        await RetireUnpublishedTargetReservationsAsync(
            relocationId,
            cancellationToken);

        await using var db =
            await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        var current = await db.RootFolderRelocations
            .AsSplitQuery()
            .Include(candidate => candidate.MoveJobs)
            .Include(candidate => candidate.OwnershipPathMigrations)
            .Include(candidate => candidate.CreatedDirectories)
            .Include(candidate => candidate.SkippedItems)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == relocationId,
                cancellationToken)
            ?? throw new KeyNotFoundException(
                "Root folder relocation not found");
        var rootPath = current.RootFolderId is int currentRootId
            ? await db.RootFolders
                .Where(root => root.Id == currentRootId)
                .Select(root => root.Path)
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        if (string.IsNullOrWhiteSpace(rootPath)
            || !CanAbandonUnpublishedRelocation(current, rootPath)
            || current.CreatedDirectories.Any(reservation => reservation.State is not (
                RootFolderRelocationCreatedDirectoryState.Removed
                    or RootFolderRelocationCreatedDirectoryState.Retained)))
        {
            throw new ApplicationConflictException(
                "root_folder_relocation_cannot_abandon",
                "The unfinished relocation changed while it was being abandoned. Refresh the root folder and review the current recovery state.");
        }

        var result = new RootFolderPathChangeResult(
            current.Id,
            current.RootFolderId,
            rootPath,
            current.TargetPath,
            RootFolderRelocationStatus.Failed,
            current.TotalJobs,
            current.CompletedJobs,
            "The unfinished relocation was abandoned before any move jobs were published.",
            TargetIdentityEnrollmentState.NotRequired,
            [],
            current.Mode,
            [],
            CanAbandon: false);

        db.RootFolderRelocationCreatedDirectories.RemoveRange(
            current.CreatedDirectories);
        db.RootFolderRelocationSkippedItems.RemoveRange(current.SkippedItems);
        db.RootFolderRelocations.Remove(current);
        await db.SaveChangesAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
        return result;
    }

    private async Task RetireUnpublishedTargetReservationsAsync(
        Guid relocationId,
        CancellationToken cancellationToken)
    {
        try
        {
            await ReconcileRelocationTargetReservationsAsync(
                relocationId,
                cancellationToken);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException
                or System.ComponentModel.Win32Exception)
        {
            // Abandonment must never delete a target object whose generation can no
            // longer be proven. Retain every unresolved reservation and release all
            // future cleanup authority instead of blocking the root forever.
            await using var db =
                await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
            var reservations = await db.RootFolderRelocationCreatedDirectories
                .Where(candidate =>
                    candidate.RelocationId == relocationId
                    && candidate.State != RootFolderRelocationCreatedDirectoryState.Removed
                    && candidate.State != RootFolderRelocationCreatedDirectoryState.Retained)
                .ToListAsync(CancellationToken.None);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            foreach (var reservation in reservations)
            {
                reservation.State =
                    RootFolderRelocationCreatedDirectoryState.Retained;
                reservation.UpdatedAt = now;
            }
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }
}
