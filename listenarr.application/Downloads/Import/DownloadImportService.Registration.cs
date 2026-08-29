using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads.Import;

public partial class DownloadImportService
{
    private async Task<ImportResult?> PrepareRegisterAndCompletePublicationAsync(
        FilePublicationPlan publicationPlan,
        string source,
        string destination,
        string destinationOwnershipBoundary,
        FileSystemPathSemantics destinationSemantics,
        Guid operationId,
        string? expectedRegisteredPhysicalObjectIdentity,
        FilePublicationSourceProof sourceProof,
        Audiobook audiobook,
        AudiobookFileOwnershipCheckResult ownership,
        CancellationToken cancellationToken)
    {
        var preparation = await PrepareOwnedFileActionForRegistrationAsync(
            publicationPlan,
            source,
            destination,
            destinationOwnershipBoundary,
            destinationSemantics,
            operationId,
            expectedRegisteredPhysicalObjectIdentity,
            sourceProof,
            audiobook.Id,
            cancellationToken);
        using var registrationLease = preparation.RegistrationLease;
        if (registrationLease == null)
        {
            return CreatePublicationFailureResult(
                preparation,
                source,
                destination);
        }

        var registered = publicationPlan.Mode is
                FilePublicationExecutionMode.AdditiveCopyRetainSource or
                FilePublicationExecutionMode.CompatibilityCopyVerifiedCleanup
                ? await audiobookFileService.RegisterCompatibilityPublicationAsync(
                    audiobook,
                    ownership,
                    registrationLease,
                    "download",
                    cancellationToken)
                : await RegisterPublishedImportAsync(
                    audiobook,
                    ownership,
                    registrationLease,
                    "download",
                    cancellationToken);
        if (!registered)
        {
            return CreatePublicationFailureResult(
                publicationPlan,
                source,
                destination);
        }

        if (publicationPlan.EffectiveAction == FileAction.Move
            && !await fileMover.CompletePreparedMoveAsync(
                source,
                destination,
                registrationLease,
                operationId))
        {
            await audiobookFileService.RollbackPublishedGenerationIfStaleAsync(
                audiobook,
                registrationLease);
            return CreatePublicationFailureResult(
                publicationPlan,
                source,
                destination);
        }

        var completion = registrationLease.CompletePublication();
        if (completion == RegistrationPublicationCompletion.CommittedCleanupPending)
        {
            logger.LogWarning(
                "Download import committed for audiobook {AudiobookId}, but registration-publication cleanup remains pending for {Destination}",
                audiobook.Id,
                LogRedaction.SanitizeFilePath(destination));
        }

        return null;
    }

    private Task<bool> RegisterPublishedImportAsync(
        Audiobook audiobook,
        AudiobookFileOwnershipCheckResult initialOwnership,
        IAudiobookFileRegistrationLease registrationLease,
        string source,
        CancellationToken cancellationToken)
    {
        return audiobookFileService.RegisterPublishedGenerationAsync(
            audiobook,
            initialOwnership,
            registrationLease,
            source,
            cancellationToken);
    }
}
