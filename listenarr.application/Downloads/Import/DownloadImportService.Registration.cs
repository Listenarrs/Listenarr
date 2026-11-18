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

        var registered = await RegisterPublishedImportAsync(
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
        if (audiobook.TryGetNamingDestination(out _, out _))
        {
            var paths = (audiobook.Files ?? [])
                .Where(file => !string.IsNullOrWhiteSpace(file.Path))
                .Select(file => file.Path!)
                .Append(registrationLease.PublicPath);
            var observedBase = FileUtils.GetCommonDirectory(paths)
                ?? Path.GetDirectoryName(registrationLease.PublicPath)!;
            return audiobookFileService.RegisterPublishedGenerationWithBasePathAsync(
                audiobook, initialOwnership, registrationLease, observedBase, source, cancellationToken);
        }
        return audiobookFileService.RegisterPublishedGenerationAsync(
            audiobook,
            initialOwnership,
            registrationLease,
            source,
            cancellationToken);
    }
}
