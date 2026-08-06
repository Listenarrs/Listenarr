using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    public async Task<AudiobookContentMoveResult?> GetRecoverableMoveAsync(
        AudiobookContentMoveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request = await WithValidatedTargetDirectoryOwnershipAsync(
            request,
            cancellationToken);

        var source = NormalizeMoveDirectoryEndpoint(request.Source);
        var target = NormalizeMoveDirectoryEndpoint(request.Target);
        var recoveryMarkerPath = GetRecoveryMarkerPath(target, request.JobId);
        var sourceSemantics = request.SourceSemantics;
        var targetSemantics = request.TargetSemantics;

        await EnsureLeaseOwnedAsync(request.JobId, request.LeaseToken, cancellationToken);
        await ValidatePersistedMoveIdentityAsync(
            request.JobId,
            source,
            target,
            sourceSemantics,
            targetSemantics,
            request.LeaseToken,
            cancellationToken);
        if (await GetExecutionProtocolVersionAsync(
                request.JobId,
                cancellationToken)
            >= MoveExecutionProtocol.MarkerlessDatabaseState)
        {
            return await GetMarkerlessRecoverableMoveAsync(
                request,
                source,
                target,
                cancellationToken);
        }
        await RecoverRecoveryMarkerWriteFilesAsync(
            source,
            request,
            source,
            target,
            cancellationToken);
        await RecoverRecoveryMarkerWriteFilesAsync(
            target,
            request,
            source,
            target,
            cancellationToken);

        var recoveryMarker = ReadRecoveryMarker(recoveryMarkerPath);
        if (recoveryMarker == null)
        {
            return null;
        }

        ValidateRecoveryMarker(recoveryMarker, request, source, target);
        ValidateRecoveryMarkerLocation(recoveryMarkerPath, target, targetSemantics);

        if (IsFilesystemRoot(source, sourceSemantics)
            || IsFilesystemRoot(target, targetSemantics)
            || FileSystemPathIdentity.AreEquivalentEndpoints(
                source,
                sourceSemantics,
                target,
                targetSemantics))
        {
            throw new MoveNeedsAttentionException(
                "Move recovery artifacts reference a filesystem root or identical source and target.");
        }

        if (!Directory.Exists(target))
        {
            return null;
        }

        if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
        {
            throw new MoveNeedsAttentionException(
                "Move recovery target is a symbolic link or reparse point.");
        }

        var manifest = await LoadManifestAsync(request.JobId, cancellationToken);
        var recoveryStage = recoveryMarker.Stage;
        if (string.Equals(recoveryStage, AtomicRenameCompletedStage, StringComparison.Ordinal))
        {
            if (Directory.Exists(source))
            {
                throw new MoveNeedsAttentionException(
                    "Both source and target exist for an atomic rename marker; completion cannot be proven and no files were changed.");
            }

            if (manifest.Count == 0)
            {
                throw new MoveNeedsAttentionException(
                    "An atomic rename marker exists without a persisted manifest; target contents cannot be proven.");
            }

            faultInjector?.OnFinalizedVerification(
                request.JobId,
                FinalizedVerificationFaultPoint.BeforeManifestVerification);
            ValidateTargetManifest(target, manifest, targetSemantics);
            ValidateExistingDestinationContents(
                source,
                target,
                manifest,
                request.JobId,
                targetSemantics,
                allowPartialFiles: false,
                targetDirectoryOwnership: request.TargetDirectoryOwnership);
            await VerifyPublishedManifestAsync(
                target,
                manifest,
                targetSemantics,
                cancellationToken);
            var atomicTargetPhysicalObjectIdentities =
                await CapturePublishedTargetPhysicalIdentitiesAsync(
                    target,
                    manifest,
                    targetSemantics,
                    cancellationToken);
            return new AudiobookContentMoveResult(
                source,
                target,
                TargetInsideSource: false,
                SourceInsideTarget: false,
                recoveryMarkerPath,
                SourceCleanupCompleted: true,
                atomicTargetPhysicalObjectIdentities);
        }

        if (manifest.Count == 0)
        {
            throw new MoveNeedsAttentionException(
                "A move recovery marker exists without a persisted manifest; destination ownership cannot be proven.");
        }

        if (recoveryStage == CopyStartedStage)
        {
            return null;
        }

        if (recoveryStage is not (CopyCompletedStage or SourceCleanupCompletedStage))
        {
            throw new MoveNeedsAttentionException("The move recovery stage is not recoverable.");
        }

        faultInjector?.OnFinalizedVerification(
            request.JobId,
            FinalizedVerificationFaultPoint.BeforeManifestVerification);
        var tempOwnership = await TryValidatePublishedTempOwnershipAsync(
            target,
            request,
            source,
            target,
            cancellationToken);
        var quarantineOwnership = await TryValidateExistingQuarantineDirectoryAsync(
            source,
            target,
            request.JobId,
            sourceSemantics,
            targetSemantics,
            request.LeaseToken,
            cancellationToken);
        ValidateExistingDestinationContents(
            source,
            target,
            manifest,
            request.JobId,
            targetSemantics,
            tempOwnership,
            quarantineOwnership,
            allowPartialFiles: false,
            targetDirectoryOwnership: request.TargetDirectoryOwnership);
        await VerifyPublishedManifestAsync(
            target,
            manifest,
            targetSemantics,
            cancellationToken);

        var targetInsideSource = IsSameOrInside(target, source, sourceSemantics);
        var sourceInsideTarget = IsSameOrInside(source, target, targetSemantics);
        var sourceCleanupCompleted = string.Equals(
            recoveryStage,
            SourceCleanupCompletedStage,
            StringComparison.Ordinal);
        if (sourceCleanupCompleted)
        {
            VerifySourceCleanupState(
                request,
                source,
                target,
                manifest);
        }

        var targetPhysicalObjectIdentities =
            await CapturePublishedTargetPhysicalIdentitiesAsync(
                target,
                manifest,
                targetSemantics,
                cancellationToken);
        return new AudiobookContentMoveResult(
            source,
            target,
            targetInsideSource,
            sourceInsideTarget,
            recoveryMarkerPath,
            sourceCleanupCompleted,
            targetPhysicalObjectIdentities);
    }

    public async Task<AudiobookContentMoveResult> ResumeSourceCleanupAsync(
        AudiobookContentMoveRequest request,
        AudiobookContentMoveResult result,
        CancellationToken cancellationToken)
    {
        request = await WithValidatedTargetDirectoryOwnershipAsync(
            request,
            cancellationToken);
        if (result.SourceCleanupCompleted)
        {
            return result;
        }

        var manifest = await LoadManifestAsync(request.JobId, cancellationToken);
        if (manifest.Count == 0)
        {
            throw new MoveNeedsAttentionException(
                "Source cleanup is blocked because no persisted move manifest is available.");
        }

        await EnsureLeaseOwnedAsync(request.JobId, request.LeaseToken, cancellationToken);
        if (await GetExecutionProtocolVersionAsync(
                request.JobId,
                cancellationToken)
            >= MoveExecutionProtocol.MarkerlessDatabaseState)
        {
            await DeleteMarkerlessSourceAsync(
                request,
                result.Source,
                result.Target,
                result.TargetInsideSource,
                manifest,
                cancellationToken);
            VerifySourceCleanupState(
                request,
                result.Source,
                result.Target,
                manifest);
            var markerlessIdentities = CreatePersistedTargetPhysicalIdentityMap(
                result.Target,
                manifest,
                request.TargetSemantics);
            return result with
            {
                SourceCleanupCompleted = true,
                RecoveryMarkerPath = string.Empty,
                TargetPhysicalObjectIdentities = markerlessIdentities
            };
        }

        await DeleteOriginalSourceAsync(
            result.Source,
            result.Target,
            result.TargetInsideSource,
            request.DeleteEmptySource,
            request.JobId,
            request.LeaseToken,
            manifest,
            request.SourceSemantics,
            request.TargetSemantics,
            request.TargetDirectoryOwnership,
            request.SourceCleanupBoundary,
            request.SourcePhysicalObjectIdentities,
            cancellationToken);
        VerifySourceCleanupState(
            request,
            result.Source,
            result.Target,
            manifest);
        await WriteRecoveryMarkerAsync(
            result.Target,
            request,
            result.Source,
            result.Target,
            SourceCleanupCompletedStage,
            cancellationToken);
        var targetPhysicalObjectIdentities =
            await CapturePublishedTargetPhysicalIdentitiesAsync(
                result.Target,
                manifest,
                request.TargetSemantics,
                cancellationToken);
        return result with
        {
            SourceCleanupCompleted = true,
            TargetPhysicalObjectIdentities = targetPhysicalObjectIdentities
        };
    }
}
