namespace Listenarr.Application.Common.Contracts;

public interface IFilesystemMutationCoordinator
{
    Task ExecuteExclusiveAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);

    Task<T> ExecuteExclusiveAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default);
}
