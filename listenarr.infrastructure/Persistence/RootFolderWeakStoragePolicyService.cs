using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence;

public sealed class RootFolderWeakStoragePolicyService(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    IFilesystemMutationCoordinator mutationCoordinator,
    TimeProvider timeProvider) : IRootFolderWeakStoragePolicyService
{
    public Task<RootFolder> UpdateAsync(
        int rootFolderId,
        RootFolderWeakStoragePolicyUpdate update,
        CancellationToken cancellationToken = default) =>
        mutationCoordinator.ExecuteExclusiveAsync(
            token => UpdateCoreAsync(rootFolderId, update, token),
            cancellationToken);

    private async Task<RootFolder> UpdateCoreAsync(
        int rootFolderId,
        RootFolderWeakStoragePolicyUpdate update,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(update.Policy) || update.ExpectedRevision < 0)
        {
            throw new ArgumentException("The weak-storage policy request is invalid.");
        }

        await using var context = await dbContextFactory.CreateDbContextAsync(
            cancellationToken);
        await using var transaction = context.Database.IsRelational()
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var root = await context.RootFolders.SingleOrDefaultAsync(
            candidate => candidate.Id == rootFolderId,
            cancellationToken) ?? throw new KeyNotFoundException("Root folder not found");

        if (root.WeakStoragePolicyRevision != update.ExpectedRevision)
        {
            throw new RootFolderWeakStoragePolicyConflictException(
                "The root-folder cleanup policy changed while it was being edited.");
        }

        if (root.WeakStorageSourceCleanupPolicy == update.Policy)
        {
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            return root;
        }

        var previous = root.WeakStorageSourceCleanupPolicy;
        root.WeakStorageSourceCleanupPolicy = update.Policy;
        root.WeakStoragePolicyRevision = checked(root.WeakStoragePolicyRevision + 1);
        root.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        context.History.Add(new History
        {
            EventType = "Root Folder Policy Changed",
            Outcome = HistoryOutcome.Succeeded,
            Source = "RootFolderSettings",
            SourceTitle = root.Name,
            Message = "Legacy weak-storage setting updated for compatibility. "
                + "Source deletion remains governed by current storage capabilities and verified operation evidence.",
            Timestamp = root.UpdatedAt.Value,
            Data = JsonSerializer.Serialize(new
            {
                RootFolderId = root.Id,
                RootFolderPath = root.Path,
                PreviousPolicy = previous.ToString(),
                Policy = update.Policy.ToString(),
                root.WeakStoragePolicyRevision
            })
        });

        await context.SaveChangesAsync(cancellationToken);
        if (transaction != null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        return root;
    }
}
