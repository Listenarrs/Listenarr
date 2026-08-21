namespace Listenarr.Api.Features.Downloads;

public sealed partial class ManualImportCompanionImporter
{
    private async Task<bool> PublishAndRegisterAudioCompanionAsync(
        FilePublicationPlan publicationPlan,
        string sourcePath,
        string destinationPath,
        Guid operationId,
        FilePublicationSourceProof expectedSourceProof,
        Audiobook audiobook,
        AudiobookFileOwnershipCheckResult ownership,
        CancellationToken cancellationToken)
    {
        var expectedIdentity = publicationPlan.EffectiveAction
            == FileAction.HardlinkCopy
            ? ownership.ExistingFile?.PhysicalObjectIdentity
            : null;
        var preparation = await _fileMover
            .PrepareActionForRegistrationDetailedAsync(
                publicationPlan,
                sourcePath,
                destinationPath,
                operationId,
                expectedIdentity,
                expectedSourceProof);

        using var registrationLease = preparation.RegistrationLease;
        if (registrationLease == null
            || _audiobookFileService == null
            || !(publicationPlan.Mode
                == FilePublicationExecutionMode.AdditiveCopyRetainSource
                ? await _audiobookFileService.RegisterCompatibilityPublicationAsync(
                    audiobook,
                    ownership,
                    registrationLease,
                    "manual-import-companion",
                    cancellationToken)
                : await _audiobookFileService.RegisterPublishedGenerationAsync(
                    audiobook,
                    ownership,
                    registrationLease,
                    "manual-import-companion",
                    cancellationToken)))
        {
            return false;
        }

        if (publicationPlan.EffectiveAction == FileAction.Move
            && !await _fileMover.CompletePreparedMoveAsync(
                sourcePath,
                destinationPath,
                registrationLease,
                operationId))
        {
            await _audiobookFileService.RollbackPublishedGenerationIfStaleAsync(
                audiobook,
                registrationLease);
            return false;
        }

        var completion = registrationLease.CompletePublication();
        if (completion
            == RegistrationPublicationCompletion.CommittedCleanupPending)
        {
            _logger.LogWarning(
                "Manual import companion committed for audiobook {AudiobookId}, but registration-publication cleanup remains pending for {Path}",
                audiobook.Id,
                LogRedaction.SanitizeFilePath(destinationPath));
        }

        return true;
    }

    private async Task<bool> PublishUnregisteredCompanionAsync(
        FilePublicationPlan publicationPlan,
        string sourcePath,
        string destinationPath,
        Guid operationId,
        FilePublicationSourceProof expectedSourceProof,
        int audiobookId)
    {
        var preparation = await _fileMover
            .PrepareActionForRegistrationDetailedAsync(
                publicationPlan,
                sourcePath,
                destinationPath,
                operationId,
                expectedRegisteredPhysicalObjectIdentity: null,
                expectedSourceProof,
                isCompanionFile: true,
                companionAudiobookId: audiobookId);
        using var lease = preparation.RegistrationLease;
        if (lease == null || !lease.PrepareCleanupRecovery(audiobookId))
        {
            return false;
        }

        var completion = lease.CompletePublication();
        if (completion
            == RegistrationPublicationCompletion.CommittedCleanupPending)
        {
            _logger.LogWarning(
                "Manual import companion publication committed, but cleanup remains pending for {Path}",
                LogRedaction.SanitizeFilePath(destinationPath));
            return false;
        }

        return publicationPlan.EffectiveAction != FileAction.Move
            || await _fileMover.CompletePreparedMoveAsync(
                sourcePath,
                destinationPath,
                lease,
                operationId);
    }
}
