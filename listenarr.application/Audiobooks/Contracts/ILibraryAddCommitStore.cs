namespace Listenarr.Application.Audiobooks.Contracts;

/// <summary>
/// Atomically persists a newly added audiobook and its required Added history event.
/// </summary>
public interface ILibraryAddCommitStore
{
    Task CommitAsync(
        Audiobook audiobook,
        History history,
        CancellationToken cancellationToken = default);
}
