namespace Listenarr.Application.Audiobooks.Renaming;

public partial class RenameService
{
    private Task CommitSuccessfulRenameStateAsync(
        Audiobook audiobook,
        IReadOnlyCollection<FileRenameResultItem> items,
        CancellationToken cancellationToken) =>
        _fileRenameCommitStore.CommitOwnerMetadataAsync(
            audiobook.Id,
            items
                .Where(item => item.Success && item.OperationId.HasValue)
                .Select(item => item.OperationId!.Value)
                .ToArray(),
            cancellationToken);

    private Task CommitRollbackStateAsync(
        Audiobook audiobook,
        IReadOnlyCollection<FileRenameResultItem> items,
        CancellationToken cancellationToken) =>
        _fileRenameCommitStore.CommitOwnerMetadataAsync(
            audiobook.Id,
            items
                .Where(item => item.RolledBack)
                .SelectMany(item => new[]
                {
                    item.OperationId,
                    item.RollbackOperationId
                })
                .Where(operationId => operationId.HasValue)
                .Select(operationId => operationId!.Value)
                .ToArray(),
            cancellationToken);
}
