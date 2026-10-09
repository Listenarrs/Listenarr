namespace Listenarr.Application.Audiobooks.Contracts;

public sealed record RootFolderWeakStoragePolicyUpdate(
    WeakStorageSourceCleanupPolicy Policy,
    int ExpectedRevision);

public sealed class RootFolderWeakStoragePolicyConflictException(string message)
    : InvalidOperationException(message);

/// <summary>
/// Persists legacy policy values for existing clients. These values do not
/// authorize source deletion; current storage capabilities and operation evidence do.
/// </summary>
public interface IRootFolderWeakStoragePolicyService
{
    Task<RootFolder> UpdateAsync(
        int rootFolderId,
        RootFolderWeakStoragePolicyUpdate update,
        CancellationToken cancellationToken = default);
}
