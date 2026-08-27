using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads.Import;

public partial class DownloadImportService
{
    private Task<FilePublicationPlan> ResolvePublicationPlanAsync(
        FileAction requestedAction,
        string source,
        string destination,
        FilePublicationSourceProof sourceProof,
        Guid compatibilityBatchId,
        CancellationToken cancellationToken)
    {
        return filePublicationCapabilityResolver == null
            ? Task.FromResult(sourceProof.HasDurablePhysicalObjectIdentity
                ? FilePublicationPlan.Durable(requestedAction)
                : FilePublicationPlan.Additive(requestedAction))
            : filePublicationCapabilityResolver.ResolveAsync(
                requestedAction,
                source,
                destination,
                sourceProof,
                cancellationToken,
                compatibilityBatchId,
                CompatibilityCleanupOwner.DownloadClient);
    }

    private static ImportResult CreateBlockedImportResult(
        FilePublicationPlan publicationPlan,
        string source,
        string destination) =>
        CreatePublicationFailureResult(
            publicationPlan.RequestedAction,
            publicationPlan.EffectiveAction,
            publicationPlan.SourceDisposition,
            source,
            destination,
            publicationPlan.ReasonCode,
            publicationPlan.Message);

    private static ImportResult CreatePublicationFailureResult(
        FilePublicationPreparationResult preparation,
        string source,
        string destination) =>
        CreatePublicationFailureResult(
            preparation.RequestedAction,
            preparation.EffectiveAction,
            preparation.SourceDisposition,
            source,
            destination,
            preparation.ReasonCode,
            preparation.Message);

    private static ImportResult CreatePublicationFailureResult(
        FilePublicationPlan publicationPlan,
        string source,
        string destination) =>
        CreatePublicationFailureResult(
            publicationPlan.RequestedAction,
            publicationPlan.EffectiveAction,
            publicationPlan.SourceDisposition,
            source,
            destination,
            publicationPlan.ReasonCode,
            message: null);

    private static ImportResult CreatePublicationFailureResult(
        FileAction requestedAction,
        FileAction effectiveAction,
        FilePublicationSourceDisposition sourceDisposition,
        string source,
        string destination,
        string? reasonCode,
        string? message)
    {
        var failed = ImportResult.ImportFailure(
            effectiveAction,
            source,
            destination);
        failed.RequestedAction = requestedAction;
        failed.EffectiveAction = effectiveAction;
        failed.SourceDisposition = ToImportSourceDisposition(sourceDisposition);
        failed.WarningCode = reasonCode;
        if (!string.IsNullOrWhiteSpace(message))
        {
            failed.Message = message;
        }

        return failed;
    }

    private static ImportSourceDisposition ToImportSourceDisposition(
        FilePublicationSourceDisposition sourceDisposition) =>
        sourceDisposition switch
        {
            FilePublicationSourceDisposition.Retained =>
                ImportSourceDisposition.Retained,
            FilePublicationSourceDisposition.Retired =>
                ImportSourceDisposition.Retired,
            _ => ImportSourceDisposition.Unchanged
        };

    private async Task<FilePublicationPlan?> PerformOwnedFileActionAsync(
        FileAction action,
        string source,
        string destination,
        string managedBoundary,
        FileSystemPathSemantics semantics,
        Guid operationId,
        FilePublicationSourceProof expectedSourceProof,
        int audiobookId,
        Guid compatibilityBatchId,
        CancellationToken cancellationToken)
    {
        expectedSourceProof.Validate();
        var publicationPlan = filePublicationCapabilityResolver == null
            ? expectedSourceProof.HasDurablePhysicalObjectIdentity
                ? FilePublicationPlan.Durable(action)
                : FilePublicationPlan.Additive(action)
            : await filePublicationCapabilityResolver.ResolveAsync(
                action,
                source,
                destination,
                expectedSourceProof,
                cancellationToken,
                compatibilityBatchId,
                CompatibilityCleanupOwner.DownloadClient);
        if (!publicationPlan.IsAllowed)
        {
            logger.LogWarning(
                "Blocked companion publication for {Source}: {Reason}",
                LogRedaction.SanitizeFilePath(source),
                LogRedaction.SanitizeText(publicationPlan.Message));
            return null;
        }

        if (!await EnsureOwnedImportDestinationAsync(
                source,
                destination,
                managedBoundary,
                semantics,
                operationId,
                audiobookId,
                publicationPlan,
                cancellationToken))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var preparation = await fileMover
            .PrepareActionForRegistrationDetailedAsync(
                publicationPlan,
                source,
                destination,
                operationId,
                expectedRegisteredPhysicalObjectIdentity: null,
                expectedSourceProof,
                isCompanionFile: true,
                companionAudiobookId: audiobookId);
        using var lease = preparation.RegistrationLease;
        if (lease == null || !lease.PrepareCleanupRecovery(audiobookId))
        {
            return null;
        }

        var completion = lease.CompletePublication();
        if (completion
            == RegistrationPublicationCompletion.CommittedCleanupPending)
        {
            logger.LogWarning(
                "Companion publication committed, but cleanup remains pending for {Path}",
                LogRedaction.SanitizeFilePath(destination));
        }

        if (publicationPlan.EffectiveAction == FileAction.Move
            && !await fileMover.CompletePreparedMoveAsync(
                source,
                destination,
                lease,
                operationId))
        {
            return null;
        }

        return publicationPlan;
    }

    private async Task<FilePublicationPreparationResult> PrepareOwnedFileActionForRegistrationAsync(
        FilePublicationPlan publicationPlan,
        string source,
        string destination,
        string managedBoundary,
        FileSystemPathSemantics semantics,
        Guid operationId,
        string? expectedRegisteredPhysicalObjectIdentity,
        FilePublicationSourceProof expectedSourceProof,
        int audiobookId,
        CancellationToken cancellationToken)
    {
        expectedSourceProof.Validate();
        if (!await EnsureOwnedImportDestinationAsync(
                source,
                destination,
                managedBoundary,
                semantics,
                operationId,
                audiobookId,
                publicationPlan,
                cancellationToken))
        {
            return new FilePublicationPreparationResult(
                FilePublicationOutcome.Blocked,
                publicationPlan.RequestedAction,
                publicationPlan.EffectiveAction,
                publicationPlan.SourceDisposition,
                ReasonCode: "destination_ownership_unavailable",
                Message: "The import destination could not be prepared safely.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (publicationPlan.Mode == FilePublicationExecutionMode.Durable)
        {
            var lease = publicationPlan.EffectiveAction == FileAction.HardlinkCopy
                && !string.IsNullOrWhiteSpace(
                    expectedRegisteredPhysicalObjectIdentity)
                    ? await fileMover.PrepareActionForRegistrationAsync(
                        publicationPlan.EffectiveAction,
                        source,
                        destination,
                        operationId,
                        expectedRegisteredPhysicalObjectIdentity,
                        expectedSourceProof)
                    : await fileMover.PrepareActionForRegistrationAsync(
                        publicationPlan.EffectiveAction,
                        source,
                        destination,
                        operationId,
                        expectedRegisteredPhysicalObjectIdentity: null,
                        expectedSourceProof);
            return new FilePublicationPreparationResult(
                lease == null
                    ? FilePublicationOutcome.Blocked
                    : FilePublicationOutcome.Success,
                publicationPlan.RequestedAction,
                publicationPlan.EffectiveAction,
                publicationPlan.SourceDisposition,
                lease);
        }

        return await fileMover.PrepareActionForRegistrationDetailedAsync(
            publicationPlan,
            source,
            destination,
            operationId,
            publicationPlan.EffectiveAction == FileAction.HardlinkCopy
                ? expectedRegisteredPhysicalObjectIdentity
                : null,
            expectedSourceProof);
    }

    private async Task<FilePublicationSourceProof?> ResolvePublishableSourceProofAsync(
        string source,
        CancellationToken cancellationToken)
    {
        var capability = await filePublicationSourceCapability.CheckAsync(
            source,
            cancellationToken);
        if (capability.IsSupported && capability.SourceProof.HasValue)
        {
            return capability.SourceProof.Value;
        }

        logger.LogWarning(
            "Blocked download import before destination creation because source publication capability is unavailable for {Source}: {Reason}",
            LogRedaction.SanitizeFilePath(source),
            LogRedaction.SanitizeText(capability.Reason));
        return null;
    }

    private async Task<bool> EnsureOwnedImportDestinationAsync(
        string source,
        string destination,
        string managedBoundary,
        FileSystemPathSemantics semantics,
        Guid operationId,
        int audiobookId,
        FilePublicationPlan? publicationPlan,
        CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException(
                "The import destination has no parent directory.");
        var audiobook = await audiobookRepository.GetByIdSnapshotAsync(
            audiobookId,
            cancellationToken);
        if (audiobook == null)
        {
            logger.LogWarning(
                "Blocked download import because audiobook {AudiobookId} disappeared before destination ownership could be verified",
                audiobookId);
            return false;
        }

        var ownership = await audiobookFileService.CheckAudiobookFileOwnershipAsync(
            audiobook,
            destination,
            destinationDirectory,
            cancellationToken);
        if (ownership.Outcome is not (
                AudiobookFileOwnershipCheckOutcome.Available or
                AudiobookFileOwnershipCheckOutcome.AlreadyOwnedByAudiobook))
        {
            logger.LogWarning(
                "Blocked download import because destination ownership is unavailable. Audiobook {AudiobookId}, Source {Source}, Destination {Destination}, Outcome {Outcome}, Reason {Reason}",
                audiobookId,
                source,
                destination,
                ownership.Outcome,
                ownership.Reason);
            return false;
        }

        if (publicationPlan?.Mode is
            FilePublicationExecutionMode.AdditiveCopyRetainSource or
            FilePublicationExecutionMode.CompatibilityCopyVerifiedCleanup)
        {
            await directoryOwnershipStore.EnsureAdditiveHierarchyAsync(
                destinationDirectory,
                managedBoundary,
                semantics,
                cancellationToken);
        }
        else
        {
            await directoryOwnershipStore.EnsureCreatedHierarchyAsync(
                destinationDirectory,
                managedBoundary,
                semantics,
                "download-import",
                operationId,
                audiobookId,
                cancellationToken);
        }
        return true;
    }
}
