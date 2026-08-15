namespace Listenarr.Application.Audiobooks.Contracts;

public interface IFileRenameRecoveryReconciler
{
    Task ReconcileAsync(CancellationToken cancellationToken = default);
}
