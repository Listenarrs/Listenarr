namespace Listenarr.Application.Audiobooks.Contracts;

public interface IAudiobookUpdatePublisher
{
    Task PublishCurrentAsync(
        int audiobookId,
        CancellationToken cancellationToken = default);
}
