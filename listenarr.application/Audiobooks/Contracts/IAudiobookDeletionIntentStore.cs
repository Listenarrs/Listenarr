namespace Listenarr.Application.Audiobooks.Contracts;

public interface IAudiobookDeletionIntentStore
{
    Task<AudiobookDeletionIntent> GetOrCreateAsync(
        int audiobookId,
        bool deleteFolder,
        CancellationToken cancellationToken = default);

    Task MarkFilesystemCleanupCompletedAsync(
        Guid intentId,
        CancellationToken cancellationToken = default);

    Task MarkCompletedAsync(
        Guid intentId,
        CancellationToken cancellationToken = default);

    Task RecordErrorAsync(
        Guid intentId,
        string error,
        CancellationToken cancellationToken = default);

    Task MarkNeedsAttentionAsync(
        Guid intentId,
        string error,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AudiobookDeletionIntent>> GetActiveAsync(
        CancellationToken cancellationToken = default);
}
