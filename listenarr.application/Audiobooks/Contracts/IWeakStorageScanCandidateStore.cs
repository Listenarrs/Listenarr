namespace Listenarr.Application.Audiobooks.Contracts;

public sealed record WeakStorageMissingFileCandidate(
    int AudiobookFileId,
    string ExpectedStoredPath,
    string ExpectedResolvedPath,
    string? ExpectedPhysicalObjectIdentity);

public sealed record WeakStorageScanConfirmationResult(
    int RemovedCount,
    int PreservedCount,
    IReadOnlyList<string> PreservedPaths);

public interface IWeakStorageScanCandidateStore
{
    Task<Guid> ReplaceAsync(
        int audiobookId,
        IReadOnlyCollection<WeakStorageMissingFileCandidate> candidates,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WeakStorageScanCandidate>> GetPendingAsync(
        int audiobookId,
        CancellationToken cancellationToken = default);
    Task<WeakStorageScanConfirmationResult> ConfirmAsync(
        int audiobookId,
        Guid scanToken,
        IReadOnlyCollection<Guid> candidateIds,
        CancellationToken cancellationToken = default);
}
