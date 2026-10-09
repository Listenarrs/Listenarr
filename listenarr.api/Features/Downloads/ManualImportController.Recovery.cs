using Listenarr.Api.Dtos.ManualImport;
using Listenarr.Domain.Common;

namespace Listenarr.Api.Features.Downloads;

public partial class ManualImportController
{
    private async Task<ManualImportResultDto?> TryConsumeRecoveredManualImportAsync(
        ManualImportItemDto item,
        FileAction action,
        FileSystemPathSemantics? sourceSemantics,
        IReadOnlyDictionary<int, IReadOnlyList<FileRegistrationRecoveryReceipt>> recoveryReceipts,
        ISet<Guid> consumedRecoveryOperationIds,
        ManualImportDestinationTracker destinationTracker,
        IReadOnlyCollection<RootFolder> rootFolders,
        CancellationToken cancellationToken)
    {
        if (action != FileAction.Move
            || string.IsNullOrWhiteSpace(item.FullPath)
            || !recoveryReceipts.TryGetValue(
                item.MatchedAudiobookId,
                out var audiobookReceipts))
        {
            return null;
        }

        var matchingReceipts = audiobookReceipts
            .Where(receipt =>
                !consumedRecoveryOperationIds.Contains(receipt.OperationId)
                && RecoveredManualSourceMatches(
                    item.FullPath,
                    receipt.SourcePath,
                    sourceSemantics))
            .ToList();
        if (matchingReceipts.Count == 0)
        {
            return null;
        }
        if (matchingReceipts.Count != 1)
        {
            _logger.LogWarning(
                "Manual import retry found multiple completed registration recoveries for audiobook {AudiobookId} and source {Source}; refusing to infer one recovered result.",
                item.MatchedAudiobookId,
                LogRedaction.SanitizeFilePath(item.FullPath));
            return null;
        }

        var receipt = matchingReceipts[0];
        var sourceCapability = await _filePublicationSourceCapability.CheckAsync(
            receipt.SourcePath,
            cancellationToken);
        var sourceMatchesReceipt = receipt.SourceRetained
            ? sourceCapability.IsSupported
                && sourceCapability.SourceProof is { } sourceProof
                && sourceProof.Length == receipt.SourceLength
                && string.Equals(sourceProof.Sha256, receipt.SourceSha256,
                    StringComparison.OrdinalIgnoreCase)
            : !sourceCapability.IsSupported
                && sourceCapability.FailureKind
                    == FilePublicationSourceCapabilityFailureKind.Missing;
        if (!sourceMatchesReceipt)
        {
            return null;
        }

        var audiobook = await _audiobookRepository.GetByIdAsync(
            item.MatchedAudiobookId);
        if (audiobook == null)
        {
            return null;
        }

        FileSystemSemanticsResolution destinationResolution;
        try
        {
            destinationResolution = await ResolveDestinationResolutionAsync(
                receipt.DestinationPath,
                rootFolders,
                cancellationToken);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException)
        {
            _logger.LogWarning(
                exception,
                "Manual import retry could not validate recovered destination {Destination} for audiobook {AudiobookId}.",
                LogRedaction.SanitizeFilePath(receipt.DestinationPath),
                item.MatchedAudiobookId);
            return null;
        }

        destinationTracker.CommitRecovered(
            receipt.DestinationPath,
            destinationResolution);
        consumedRecoveryOperationIds.Add(receipt.OperationId);
        return new ManualImportResultDto
        {
            Success = true,
            SourcePath = item.FullPath,
            DestinationPath = receipt.DestinationPath,
            Audiobook = audiobook,
            RequestedAction = action.ToString(),
            EffectiveAction = FileAction.Move.ToString(),
            SourceDisposition = receipt.SourceRetained
                ? FilePublicationSourceDisposition.Retained.ToString()
                : FilePublicationSourceDisposition.Retired.ToString()
        };
    }

    private static bool RecoveredManualSourceMatches(
        string requestedPath,
        string recoveredSourcePath,
        FileSystemPathSemantics? sourceSemantics)
    {
        if (string.Equals(
                requestedPath,
                recoveredSourcePath,
                StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            return sourceSemantics.HasValue && FileSystemPathIdentity.AreEquivalent(
                requestedPath,
                recoveredSourcePath,
                sourceSemantics.Value);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
