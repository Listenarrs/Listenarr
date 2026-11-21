namespace Listenarr.Application.Audiobooks.Contracts;

public sealed class RootFolderRecoveryBlockedException(
    FileRegistrationRecoveryBlocker blocker)
    : InvalidOperationException(blocker.PublicReason)
{
    public FileRegistrationRecoveryBlocker Blocker { get; } = blocker;
}

public interface IRootFolderStorageConfirmationService
{
    Task<RootFolder> ConfirmCurrentFolderAsync(
        int rootFolderId,
        string expectedCurrentPath,
        string confirmationToken,
        CancellationToken cancellationToken = default);
}
