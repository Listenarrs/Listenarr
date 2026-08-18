namespace Listenarr.Application.Downloads.Import;

public partial class DownloadImportService
{
    private async Task<(List<string> RemainingFiles, List<ImportResult> Results)>
        ConsumeRecoveredImportsAsync(
            IReadOnlyCollection<string> requestedFiles,
            IReadOnlyList<FileRegistrationRecoveryReceipt> recoveryReceipts,
            CancellationToken cancellationToken)
    {
        var remainingFiles = requestedFiles.ToList();
        var results = new List<ImportResult>();
        foreach (var receipt in recoveryReceipts)
        {
            var requestedSource = remainingFiles.FirstOrDefault(candidate =>
                RecoveredSourceMatchesRequestedPath(
                    candidate,
                    receipt.SourcePath));
            if (requestedSource == null)
            {
                continue;
            }

            var sourceCapability = await filePublicationSourceCapability.CheckAsync(
                receipt.SourcePath,
                cancellationToken);
            if (sourceCapability.IsSupported
                || sourceCapability.FailureKind
                    != FilePublicationSourceCapabilityFailureKind.Missing)
            {
                continue;
            }

            remainingFiles.RemoveAll(candidate =>
                RecoveredSourceMatchesRequestedPath(
                    candidate,
                    receipt.SourcePath));
            var result = ImportResult.ImportSuccess(
                FileAction.Move,
                requestedSource,
                receipt.DestinationPath,
                wasRegisteredToAudiobook: true);
            result.Message =
                "Recovered a previously committed move import and completed source cleanup.";
            results.Add(result);
        }

        return (remainingFiles, results);
    }

    private static bool RecoveredSourceMatchesRequestedPath(
        string requestedPath,
        string recoveredSourcePath) =>
        string.Equals(
            requestedPath,
            recoveredSourcePath,
            StringComparison.Ordinal);
}
