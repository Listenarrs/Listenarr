namespace Listenarr.Application.Audiobooks.Contracts;

public interface IFileRenameCommitStore
{
    Task CommitOwnerMetadataAsync(
        int audiobookId,
        IReadOnlyCollection<Guid> operationIds,
        CancellationToken cancellationToken = default);
}
