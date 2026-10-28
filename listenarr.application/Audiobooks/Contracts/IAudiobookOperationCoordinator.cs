namespace Listenarr.Application.Audiobooks.Contracts;

public interface IAudiobookOperationCoordinator
{
    Task ExecuteExclusiveAsync(
        int audiobookId,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);

    Task<T> ExecuteExclusiveAsync<T>(
        int audiobookId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default);

    Task ExecuteExclusiveAsync(
        IEnumerable<int> audiobookIds,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);

    Task<T> ExecuteExclusiveAsync<T>(
        IEnumerable<int> audiobookIds,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default);
}
