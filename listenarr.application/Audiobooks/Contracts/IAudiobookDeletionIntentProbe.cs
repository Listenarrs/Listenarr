namespace Listenarr.Application.Audiobooks.Contracts;

public interface IAudiobookDeletionIntentProbe
{
    Task<bool> HasActiveAsync(
        int audiobookId,
        CancellationToken cancellationToken = default);
}
