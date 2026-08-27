namespace Listenarr.Application.Audiobooks.Contracts;

public sealed record RootFolderWeakStoragePolicyUpdate(
    WeakStorageSourceCleanupPolicy Policy,
    int ExpectedRevision);

public sealed class RootFolderWeakStoragePolicyConflictException(string message)
    : InvalidOperationException(message);

public interface IRootFolderWeakStoragePolicyService
{
    Task<RootFolder> UpdateAsync(
        int rootFolderId,
        RootFolderWeakStoragePolicyUpdate update,
        CancellationToken cancellationToken = default);
}
