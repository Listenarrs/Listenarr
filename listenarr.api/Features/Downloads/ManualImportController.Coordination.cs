using Listenarr.Api.Dtos.ManualImport;
using Listenarr.Application.Common;

namespace Listenarr.Api.Features.Downloads;

public partial class ManualImportController
{
    private Task ExecuteWithAudiobookLocksAsync(
        IEnumerable<int> audiobookIds,
        IReadOnlyCollection<string> requestedSourcePaths,
        Func<IReadOnlyDictionary<int, IReadOnlyList<FileRegistrationRecoveryReceipt>>, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var lockedAudiobookIds = audiobookIds
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        return _filesystemMutationCoordinator.ExecuteExclusiveAsync(
            globalToken => _audiobookOperationCoordinator.ExecuteExclusiveAsync(
                lockedAudiobookIds,
                async operationToken =>
                {
                    var recoveryReceipts = new Dictionary<int, IReadOnlyList<FileRegistrationRecoveryReceipt>>();
                    foreach (var audiobookId in lockedAudiobookIds)
                    {
                        recoveryReceipts[audiobookId] =
                            await _fileRegistrationRecoveryService
                                .ReconcileAudiobookWithReceiptsAsync(
                                    audiobookId,
                                    requestedSourcePaths,
                                    operationToken)
                            ?? [];
                        await _moveQueueService.EnsureFilesystemMutationAllowedAsync(
                            audiobookId,
                            operationToken);
                    }

                    await operation(recoveryReceipts, operationToken);
                },
                globalToken),
            cancellationToken);
    }

    /// <summary>
    /// Order a scan for each audiobook impacted by the importation and update audiobook base path.
    /// </summary>
    /// <param name="results">List of imported files.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    private async Task EnqueueFocusedScansAsync(
        IEnumerable<ManualImportResultDto> results,
        CancellationToken cancellationToken)
    {
        var groupedResults = results
            .Where(result => result.Success
                && result.Audiobook != null
                && !string.IsNullOrWhiteSpace(result.DestinationPath))
            .GroupBy(result => result.Audiobook!.Id);

        foreach (var group in groupedResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scanPath = ManualImportPathPlanner.DetermineScanPath(group
                .Select(result => result.DestinationPath!)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList());

            if (string.IsNullOrWhiteSpace(scanPath))
            {
                _logger.LogDebug(
                    "No focused scan path could be determined for audiobook {AudiobookId} after manual import",
                    group.Key);
                continue;
            }

            try
            {
                await _audiobookOperationCoordinator.ExecuteExclusiveAsync(
                    group.Key,
                    async operationToken =>
                    {
                        operationToken.ThrowIfCancellationRequested();
                        var audiobook = await _audiobookRepository.GetByIdAsync(
                            group.Key);
                        if (audiobook == null)
                        {
                            _logger.LogWarning(
                                "Audiobook {AudiobookId} disappeared before its focused manual-import scan could be queued",
                                group.Key);
                            return;
                        }

                        var authorization =
                            await _scanPathAuthorizationService.AuthorizeAsync(
                                scanPath,
                                operationToken);
                        if (!authorization.IsAuthorized)
                        {
                            _logger.LogWarning(
                                "Skipped focused scan for audiobook {AudiobookId}: {Reason}",
                                group.Key,
                                LogRedaction.SanitizeText(
                                    authorization.Error));
                            return;
                        }

                        var scanJobId = await _scanQueueService.EnqueueScanAsync(
                            new ScanEnqueueCommand(
                                audiobook,
                                authorization.Path,
                                authorization.Identity,
                                authorization.PhysicalIdentity,
                                IsAuthoritativeScope: false,
                                AuthorizationMode:
                                    ScanAuthorizationMode.PreauthorizedPath));
                        _logger.LogInformation(
                            "Enqueued focused scan {ScanJobId} for audiobook {AudiobookId} (path: {Path}) after manual import batch of {FileCount} file(s)",
                            scanJobId,
                            group.Key,
                            LogRedaction.SanitizeFilePath(scanPath),
                            group.Count());
                    },
                    cancellationToken);
            }
            catch (OperationCanceledException exception) when (
                !cancellationToken.CanBeCanceled)
            {
                _logger.LogWarning(
                    exception,
                    "Manual import completed for audiobook {AudiobookId}, but its focused scan was canceled after commit",
                    group.Key);
            }
            catch (Exception exception) when (
                WorkerExceptionClassifier.IsNonFatal(exception))
            {
                _logger.LogWarning(
                    exception,
                    "Manual import completed for audiobook {AudiobookId}, but its focused scan could not be queued",
                    group.Key);
            }
        }
    }
}
