namespace Listenarr.Application.Audiobooks.Contracts;

public interface ILibraryDirectoryOwnershipReconciler
{
    Task ReconcileAsync(CancellationToken cancellationToken = default);
}
