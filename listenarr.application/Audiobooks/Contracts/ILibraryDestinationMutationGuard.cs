namespace Listenarr.Application.Audiobooks.Contracts;

public interface ILibraryDestinationMutationGuard
{
    Task<string?> GetBlockingReasonAsync(
        string destinationPath,
        CancellationToken cancellationToken = default);
}
