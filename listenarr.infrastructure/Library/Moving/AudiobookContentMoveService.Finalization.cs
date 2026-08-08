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
    public async Task VerifyTargetBeforeMetadataRewriteAsync(
        AudiobookContentMoveRequest request,
        AudiobookContentMoveResult result,
        CancellationToken cancellationToken)
    {
        await EnsureCurrentExecutionProtocolAsync(request.JobId, cancellationToken);
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
                "Target verification before metadata rewrite requires completed source cleanup.");
        }

        var manifest = await LoadManifestAsync(request.JobId, cancellationToken);
        if (manifest.Count == 0)
        {
            throw new MoveNeedsAttentionException(
                "Target verification before metadata rewrite requires a persisted manifest.");
        }

        ValidateTargetManifest(
            result.Target,
            manifest,
            request.TargetSemantics);
        await VerifyMarkerlessTargetAsync(
            request,
            result.Target,
            manifest,
            cancellationToken,
            targetVerificationLease: result.TargetVerificationLease);
        VerifySourceCleanupState(
            request,
            result.Source,
            result.Target,
            manifest);
    }

    public async Task FinalizeMoveAsync(
        AudiobookContentMoveRequest request,
        AudiobookContentMoveResult result,
        CancellationToken cancellationToken)
    {
        await EnsureCurrentExecutionProtocolAsync(request.JobId, cancellationToken);
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
            await RemoveEmptySourceAncestorsAsync(
                request,
                result.Source,
                result.Target,
                request.SourceCleanupBoundary,
                request.SourceSemantics,
                cancellationToken);
        }

        var manifest = await LoadManifestAsync(
            request.JobId,
            cancellationToken);
        VerifySourceCleanupState(
            request,
            result.Source,
            result.Target,
            manifest);
    }

    public async Task CleanupCompletedMoveArtifactsAsync(
        AudiobookContentMoveRequest request,
        AudiobookContentMoveResult result,
        CancellationToken cancellationToken)
    {
        await EnsureCurrentExecutionProtocolAsync(request.JobId, cancellationToken);
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
                "Completed move artifact cleanup cannot run before source cleanup completes.");
        }

        var manifest = await LoadManifestAsync(request.JobId, cancellationToken);
        if (manifest.Count == 0)
        {
            throw new MoveNeedsAttentionException(
                "Completed move verification requires a persisted manifest.");
        }

        ValidateTargetManifest(
            result.Target,
            manifest,
            request.TargetSemantics);
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
    }

    public async Task MarkCompletionRecordingAsync(
        AudiobookContentMoveRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureCurrentExecutionProtocolAsync(request.JobId, cancellationToken);
        await EnsureLeaseOwnedAsync(request.JobId, request.LeaseToken, cancellationToken);
        await UpdateJobPhaseAsync(
            request.JobId,
            request.LeaseToken,
            MoveJobPhase.RecordingCompletion,
            cancellationToken);
    }
}
