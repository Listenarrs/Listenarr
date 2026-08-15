namespace Listenarr.Application.Audiobooks.Contracts;

public interface IAudiobookFileIdentityReconciler
{
    Task<AudiobookFileIdentityReconciliationResult> ReconcileAsync(
        CancellationToken cancellationToken = default);
}
