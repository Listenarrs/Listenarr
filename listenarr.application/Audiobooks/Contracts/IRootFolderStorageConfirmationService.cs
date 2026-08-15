namespace Listenarr.Application.Audiobooks.Contracts;

public interface IRootFolderStorageConfirmationService
{
    Task<RootFolder> ConfirmCurrentFolderAsync(
        int rootFolderId,
        string expectedCurrentPath,
        string confirmationToken,
        CancellationToken cancellationToken = default);
}
