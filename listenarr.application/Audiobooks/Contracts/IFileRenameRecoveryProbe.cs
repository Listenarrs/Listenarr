namespace Listenarr.Application.Audiobooks.Contracts;

public interface IFileRenameRecoveryProbe
{
    Task<bool> HasBlockingAsync(
        int audiobookId,
        CancellationToken cancellationToken = default);
}
