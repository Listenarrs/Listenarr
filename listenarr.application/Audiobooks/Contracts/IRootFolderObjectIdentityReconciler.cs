namespace Listenarr.Application.Audiobooks.Contracts;

public interface IRootFolderObjectIdentityReconciler
{
    Task ReconcileAsync(CancellationToken cancellationToken = default);
}
