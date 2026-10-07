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
            if (receipt.SourceRetained)
            {
                if (!sourceCapability.IsSupported
                    || !sourceCapability.SourceProof.HasValue
                    || receipt.SourceLength is not long expectedLength
                    || string.IsNullOrWhiteSpace(receipt.SourceSha256)
                    || sourceCapability.SourceProof.Value.Length != expectedLength
                    || !string.Equals(
                        sourceCapability.SourceProof.Value.Sha256,
                        receipt.SourceSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }
            else if (sourceCapability.IsSupported
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
                FileAction.Move,
                receipt.SourceRetained
                    ? ImportSourceDisposition.Retained
                    : ImportSourceDisposition.Retired,
                requestedSource,
                receipt.DestinationPath,
                wasRegisteredToAudiobook: true);
            result.Message = receipt.SourceRetained
                ? "Recovered a previously committed move import; the source was retained because live delete authority was lost."
                : "Recovered a previously committed move import and observed that source cleanup was already complete.";
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
