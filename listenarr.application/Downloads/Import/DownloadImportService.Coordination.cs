namespace Listenarr.Application.Downloads.Import;

public partial class DownloadImportService
{
    public Task<List<ImportResult>> ImportDownloadFilesAsync(
        Audiobook audiobook,
        List<string> files,
        CancellationToken ct = default,
        DownloadImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(audiobook);
        return filesystemMutationCoordinator.ExecuteExclusiveAsync(
            globalToken => audiobookOperationCoordinator.ExecuteExclusiveAsync(
                audiobook.Id,
                async token =>
                {
                    var recoveryReceipts = await fileRegistrationRecoveryService
                        .ReconcileAudiobookWithReceiptsAsync(
                            audiobook.Id,
                            files,
                            token)
                        ?? [];
                    await moveQueueService.EnsureFilesystemMutationAllowedAsync(
                        audiobook.Id,
                        token);
                    var currentAudiobook = await audiobookRepository.GetByIdSnapshotAsync(
                        audiobook.Id,
                        token)
                        ?? throw new InvalidOperationException(
                            $"Audiobook {audiobook.Id} no longer exists");
                    var compatibilityBatchId = Guid.NewGuid();
                    var results = await ImportDownloadFilesCoreAsync(
                        currentAudiobook,
                        files,
                        token,
                        options,
                        recoveryReceipts,
                        compatibilityBatchId);
                    if (compatibilitySourceCleanupCoordinator != null)
                    {
                        var batchSucceeded = results.All(result =>
                            result.Success || string.IsNullOrWhiteSpace(result.SourcePath));
                        var cleanup = await compatibilitySourceCleanupCoordinator
                            .CompleteBatchAsync(
                                compatibilityBatchId,
                                batchSucceeded,
                                CancellationToken.None);
                        ApplyCompatibilityCleanupResult(results, cleanup);
                    }
                    return results;
                },
                globalToken),
            ct);
    }

    private static void ApplyCompatibilityCleanupResult(
        IEnumerable<ImportResult> results,
        CompatibilityBatchCleanupResult cleanup)
    {
        foreach (var result in results.Where(result =>
            result.WarningCode == "verified_cleanup_pending"))
        {
            if (cleanup.Disposition is
                CompatibilityBatchCleanupDisposition.RetiredByListenarr or
                CompatibilityBatchCleanupDisposition.DeferredToDownloadClient)
            {
                result.SourceDisposition = ImportSourceDisposition.Retired;
                result.WarningCode = cleanup.Disposition
                    == CompatibilityBatchCleanupDisposition.DeferredToDownloadClient
                        ? "source_cleanup_deferred_to_download_client"
                        : null;
                result.Message = cleanup.Disposition
                    == CompatibilityBatchCleanupDisposition.DeferredToDownloadClient
                        ? "Destination verified; source cleanup is deferred to the download client."
                        : "Destination verified and source removed through protected cleanup.";
            }
            else
            {
                result.SourceDisposition = ImportSourceDisposition.Retained;
                result.WarningCode = cleanup.Disposition
                    == CompatibilityBatchCleanupDisposition.PartialNeedsAttention
                        ? "source_cleanup_needs_attention"
                        : "source_retained";
                result.Message = "Destination verified, but the source was retained.";
            }
        }
    }
}
