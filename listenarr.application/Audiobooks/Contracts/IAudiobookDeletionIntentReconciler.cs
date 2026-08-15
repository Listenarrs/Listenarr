namespace Listenarr.Application.Audiobooks.Contracts;

public interface IAudiobookDeletionIntentReconciler
{
    Task ReconcileAsync(CancellationToken cancellationToken = default);
}
