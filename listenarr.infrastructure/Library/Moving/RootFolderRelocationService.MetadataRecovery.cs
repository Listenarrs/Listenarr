using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private const string MetadataOnlyTargetVerificationAttentionPrefix =
        "Metadata-only root repair target verification requires attention: ";
    private const string MetadataOnlyCompletionAttentionPrefix =
        "Metadata-only root repair completion requires attention: ";
    private const string MetadataOnlyRecoveryAttentionPrefix =
        "Metadata-only root repair recovery is blocked: ";

    private async Task<List<RootFolderPathChangeResult>>
        ReconcileCommittedMetadataOnlyRelocationsAsync(
            CancellationToken cancellationToken)
    {
        await using var discoveryDb =
            await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var relocationIds = await discoveryDb.RootFolderRelocations
            .AsNoTracking()
            .Where(relocation =>
                relocation.Mode == RootFolderRelocationMode.MetadataOnly
                && relocation.ActiveRootFolderId != null
                && relocation.OwnershipPathMigrations.Count == 0
                && (relocation.Status == RootFolderRelocationStatus.Pending
                    || relocation.Status == RootFolderRelocationStatus.Failed))
            .OrderBy(relocation => relocation.CreatedAt)
            .ThenBy(relocation => relocation.Id)
            .Select(relocation => relocation.Id)
            .ToListAsync(cancellationToken);

        var results = new List<RootFolderPathChangeResult>(relocationIds.Count);
        foreach (var relocationId in relocationIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RecoverCommittedMetadataOnlyRelocationAsync(
                relocationId,
                cancellationToken));
        }

        return results;
    }

    private async Task<RootFolderPathChangeResult>
        RecoverCommittedMetadataOnlyRelocationAsync(
            Guid relocationId,
            CancellationToken cancellationToken)
    {
        await using var db =
            await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var relocation = await db.RootFolderRelocations
            .AsSplitQuery()
            .Include(candidate => candidate.OwnershipPathMigrations)
                .ThenInclude(migration => migration.Ownership)
            .Include(candidate => candidate.SkippedItems)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == relocationId,
                cancellationToken)
            ?? throw new KeyNotFoundException(
                "Root folder relocation not found");
        if (relocation.Mode != RootFolderRelocationMode.MetadataOnly)
        {
            throw new InvalidOperationException(
                "Only metadata-only root repairs can use metadata recovery.");
        }

        var plans = RehydrateOwnershipMigrationPlans(relocation);
        try
        {
            await CompleteOwnershipMigrationMetadataAsync(
                db,
                relocation,
                plans,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException
                or StackOverflowException))
        {
            db.ChangeTracker.Clear();
            var persistedRelocation = await db.RootFolderRelocations
                .SingleAsync(
                    candidate => candidate.Id == relocationId,
                    CancellationToken.None);
            persistedRelocation.Status =
                RootFolderRelocationStatus.Failed;
            persistedRelocation.CompletedAt = null;
            persistedRelocation.Error =
                $"{MetadataOnlyRecoveryAttentionPrefix}{exception.Message}";
            persistedRelocation.UpdatedAt =
                timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(CancellationToken.None);
        }

        var resultRelocation = await db.RootFolderRelocations
            .AsNoTracking()
            .Include(candidate => candidate.SkippedItems)
            .SingleAsync(
                candidate => candidate.Id == relocationId,
                CancellationToken.None);
        var currentPath = resultRelocation.RootFolderId is int rootId
            ? await db.RootFolders
                .AsNoTracking()
                .Where(root => root.Id == rootId)
                .Select(root => root.Path)
                .SingleOrDefaultAsync(CancellationToken.None)
            : null;
        return Map(
            resultRelocation,
            currentPath ?? ResolveCurrentPathFallback(resultRelocation));
    }
}
