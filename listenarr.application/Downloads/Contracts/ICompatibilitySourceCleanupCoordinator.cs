namespace Listenarr.Application.Downloads.Contracts;

public enum CompatibilityBatchCleanupDisposition
{
    NotApplicable = 0,
    Retained = 1,
    RetiredByListenarr = 2,
    DeferredToDownloadClient = 3,
    PartialNeedsAttention = 4
}

public sealed record CompatibilityBatchCleanupResult(
    CompatibilityBatchCleanupDisposition Disposition,
    int RemovedCount = 0,
    int RetainedCount = 0,
    IReadOnlyList<string>? FailedPaths = null);

public interface ICompatibilitySourceCleanupCoordinator
{
    Task<CompatibilityBatchCleanupResult> CompleteBatchAsync(
        Guid batchId,
        bool batchSucceeded,
        CancellationToken cancellationToken = default);
}
