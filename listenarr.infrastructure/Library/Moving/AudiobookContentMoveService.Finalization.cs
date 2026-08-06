/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    public async Task FinalizeMoveAsync(
        AudiobookContentMoveRequest request,
        AudiobookContentMoveResult result,
        CancellationToken cancellationToken)
    {
        request = await WithValidatedTargetDirectoryOwnershipAsync(
            request,
            cancellationToken);
        await EnsureLeaseOwnedAsync(request.JobId, request.LeaseToken, cancellationToken);
        await ValidatePersistedMoveIdentityAsync(
            request.JobId,
            result.Source,
            result.Target,
            request.SourceSemantics,
            request.TargetSemantics,
            request.LeaseToken,
            cancellationToken);
        if (!result.SourceCleanupCompleted)
        {
            throw new InvalidOperationException(
                "Move finalization cannot run before source cleanup completes.");
        }

        await UpdateJobPhaseAsync(
            request.JobId,
            request.LeaseToken,
            MoveJobPhase.Finalizing,
            cancellationToken);

        if (request.DeleteEmptySource
            && !Directory.Exists(result.Source)
            && !string.IsNullOrWhiteSpace(request.SourceCleanupBoundary))
        {
            // The boundary is only an upper fence. Every parent deletion still requires
            // a durable ownership claim for the exact live directory identity.
            await RemoveEmptySourceAncestorsAsync(
                request,
                result.Source,
                result.Target,
                request.SourceCleanupBoundary,
                request.SourceSemantics,
                cancellationToken);
        }

        if (await GetExecutionProtocolVersionAsync(
                request.JobId,
                cancellationToken)
            >= MoveExecutionProtocol.MarkerlessDatabaseState)
        {
            var manifest = await LoadManifestAsync(
                request.JobId,
                cancellationToken);
            VerifySourceCleanupState(
                request,
                result.Source,
                result.Target,
                manifest);
            return;
        }

        var tempOwnership = await TryValidatePublishedTempOwnershipAsync(
            result.Target,
            request,
            result.Source,
            result.Target,
            cancellationToken);
        await EnsureLeaseOwnedAsync(request.JobId, request.LeaseToken, cancellationToken);
        await TryDeletePublishedTempOwnershipMarkerAsync(
            tempOwnership,
            request,
            result.Source,
            result.Target,
            cancellationToken);
    }

    public async Task CleanupCompletedMoveArtifactsAsync(
        AudiobookContentMoveRequest request,
        AudiobookContentMoveResult result,
        CancellationToken cancellationToken)
    {
        request = await WithValidatedTargetDirectoryOwnershipAsync(
            request,
            cancellationToken);
        await EnsureLeaseOwnedAsync(request.JobId, request.LeaseToken, cancellationToken);
        await ValidatePersistedMoveIdentityAsync(
            request.JobId,
            result.Source,
            result.Target,
            request.SourceSemantics,
            request.TargetSemantics,
            request.LeaseToken,
            cancellationToken);
        if (!result.SourceCleanupCompleted)
        {
            throw new InvalidOperationException(
                "Completed move artifacts cannot be cleaned before source cleanup completes.");
        }

        var manifest = await LoadManifestAsync(request.JobId, cancellationToken);
        if (manifest.Count == 0)
        {
            throw new MoveNeedsAttentionException(
                "Completed move artifact cleanup requires a persisted manifest.");
        }

        ValidateTargetManifest(
            result.Target,
            manifest,
            request.TargetSemantics);
        if (await GetExecutionProtocolVersionAsync(
                request.JobId,
                cancellationToken)
            >= MoveExecutionProtocol.MarkerlessDatabaseState)
        {
            try
            {
                await VerifyMarkerlessTargetAsync(
                    request,
                    result.Target,
                    manifest,
                    cancellationToken,
                    progressStart: 92,
                    progressSpan: 5,
                    progressPhase: "Final verification",
                    targetVerificationLease: result.TargetVerificationLease);
                VerifySourceCleanupState(
                    request,
                    result.Source,
                    result.Target,
                    manifest);
                await UpdateJobPhaseAsync(
                    request.JobId,
                    request.LeaseToken,
                    MoveJobPhase.CleaningArtifacts,
                    cancellationToken);
                foreach (var directory in await GetCreatedDirectoriesAsync(
                    request.JobId,
                    cancellationToken))
                {
                    if (directory.State == MoveCreatedDirectoryState.Created)
                    {
                        await UpdateCreatedDirectoryStateAsync(
                            request.JobId,
                            request.LeaseToken,
                            directory.Path,
                            MoveCreatedDirectoryState.Retained,
                            cancellationToken);
                    }
                }
            }
            finally
            {
                result.TargetVerificationLease?.Dispose();
            }
            return;
        }

        var publishedTempOwnership = await TryValidatePublishedTempOwnershipAsync(
            result.Target,
            request,
            result.Source,
            result.Target,
            cancellationToken);
        ValidateExistingDestinationContents(
            result.Source,
            result.Target,
            manifest,
            request.JobId,
            request.TargetSemantics,
            publishedTempOwnership,
            quarantineOwnership: null,
            allowPartialFiles: false,
            targetDirectoryOwnership: request.TargetDirectoryOwnership);
        await VerifyPublishedManifestAsync(
            result.Target,
            manifest,
            request.TargetSemantics,
            cancellationToken);
        VerifySourceCleanupState(
            request,
            result.Source,
            result.Target,
            manifest);

        if (!RecoveryMarkerEntryExists(result.RecoveryMarkerPath))
        {
            await UpdateJobPhaseAsync(
                request.JobId,
                request.LeaseToken,
                MoveJobPhase.CleaningArtifacts,
                cancellationToken);
            await RetainTargetScaffoldingAsync(request, cancellationToken);
            return;
        }

        ValidateMoveTargetRoot(result.Target);
        var recoveryMarker = ReadRecoveryMarker(result.RecoveryMarkerPath);
        if (recoveryMarker == null)
        {
            throw new MoveNeedsAttentionException(
                "Completed move artifact cleanup requires a structured recovery marker.");
        }

        ValidateRecoveryMarker(
            recoveryMarker,
            request,
            result.Source,
            result.Target);
        ValidateRecoveryMarkerLocation(
            result.RecoveryMarkerPath,
            result.Target,
            request.TargetSemantics);
        ValidateMoveTargetRoot(result.Target);
        ValidateRecoveryMarker(
            ReadRecoveryMarker(result.RecoveryMarkerPath),
            request,
            result.Source,
            result.Target);
        ValidateRecoveryMarkerLocation(
            result.RecoveryMarkerPath,
            result.Target,
            request.TargetSemantics);
        if (RecoveryMarkerPathIsLinked(result.RecoveryMarkerPath))
        {
            throw new MoveNeedsAttentionException(
                "The completed recovery marker became a symbolic link or reparse point.");
        }

        await UpdateJobPhaseAsync(
            request.JobId,
            request.LeaseToken,
            MoveJobPhase.CleaningArtifacts,
            cancellationToken);
        faultInjector?.OnCompletedArtifactCleanup(
            request.JobId,
            CompletedArtifactCleanupFaultPoint.BeforeRecoveryMarkerDelete);
        VerifySourceCleanupState(
            request,
            result.Source,
            result.Target,
            manifest);
        await EnsureLeaseOwnedAsync(request.JobId, request.LeaseToken, cancellationToken);
        ValidateMoveTargetRoot(result.Target);
        var finalTempOwnership = await TryValidatePublishedTempOwnershipAsync(
            result.Target,
            request,
            result.Source,
            result.Target,
            cancellationToken);
        ValidateExistingDestinationContents(
            result.Source,
            result.Target,
            manifest,
            request.JobId,
            request.TargetSemantics,
            finalTempOwnership,
            quarantineOwnership: null,
            allowPartialFiles: false,
            targetDirectoryOwnership: request.TargetDirectoryOwnership);
        await VerifyPublishedManifestAsync(
            result.Target,
            manifest,
            request.TargetSemantics,
            cancellationToken);
        faultInjector?.OnCompletedArtifactCleanup(
            request.JobId,
            CompletedArtifactCleanupFaultPoint.BeforeFinalDestinationOwnershipValidation);
        await EnsureMutationAuthorizedAsync(
            request,
            result.Source,
            result.Target,
            cancellationToken);
        VerifySourceCleanupState(
            request,
            result.Source,
            result.Target,
            manifest);
        ValidateMoveTargetRoot(result.Target);
        ValidateExistingDestinationContents(
            result.Source,
            result.Target,
            manifest,
            request.JobId,
            request.TargetSemantics,
            finalTempOwnership,
            quarantineOwnership: null,
            allowPartialFiles: false,
            targetDirectoryOwnership: request.TargetDirectoryOwnership);
        ValidateRecoveryMarkerLocation(
            result.RecoveryMarkerPath,
            result.Target,
            request.TargetSemantics);
        await RetirePinnedArtifactAsync(
            result.RecoveryMarkerPath,
            entry => ValidateRecoveryMarker(
                ReadRecoveryMarker(entry, result.RecoveryMarkerPath),
                request,
                result.Source,
                result.Target),
            () => EnsureMutationAuthorizedAsync(
                request,
                result.Source,
                result.Target,
                cancellationToken));
        await RetainTargetScaffoldingAsync(request, cancellationToken);
    }

    public async Task MarkCompletionRecordingAsync(
        AudiobookContentMoveRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureLeaseOwnedAsync(request.JobId, request.LeaseToken, cancellationToken);
        await UpdateJobPhaseAsync(
            request.JobId,
            request.LeaseToken,
            MoveJobPhase.RecordingCompletion,
            cancellationToken);
    }
}
