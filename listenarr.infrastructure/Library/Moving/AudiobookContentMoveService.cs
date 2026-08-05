/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed record MoveLeaseToken(string Owner, int Generation);

internal sealed record AudiobookContentMoveRequest(
    string Source,
    string Target,
    Guid JobId,
    bool DeleteEmptySource,
    FileSystemPathSemantics SourceSemantics,
    FileSystemPathSemantics TargetSemantics,
    MoveLeaseToken LeaseToken,
    string? SourceCleanupBoundary = null,
    LibraryDirectoryOwnership? TargetDirectoryOwnership = null,
    IReadOnlyDictionary<string, string>? SourcePhysicalObjectIdentities = null)
{
    public string LeaseOwner => LeaseToken.Owner;
    public int LeaseGeneration => LeaseToken.Generation;
}

internal sealed record AudiobookContentMoveResult(
    string Source,
    string Target,
    bool TargetInsideSource,
    bool SourceInsideTarget,
    string RecoveryMarkerPath,
    bool SourceCleanupCompleted,
    IReadOnlyDictionary<string, string> TargetPhysicalObjectIdentities);

internal sealed class MoveNeedsAttentionException(string message) : IOException(message);

internal sealed partial class AudiobookContentMoveService(
    ILogger<AudiobookContentMoveService> logger,
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    TimeProvider timeProvider,
    IMoveFaultInjector? faultInjector = null,
    IMoveExecutionStore? moveExecutionStore = null,
    ILibraryDirectoryOwnershipStore? directoryOwnershipStore = null,
    LibraryDirectoryOwnershipBoundaryAuthorizer? ownershipAuthorizer = null)
{
    private const int MaxCopyAttempts = 5;
    private readonly IMoveExecutionStore executionStore =
        moveExecutionStore ?? new EfMoveExecutionStore(dbContextFactory, timeProvider);
    private readonly ILibraryDirectoryOwnershipStore directoryOwnershipStore =
        directoryOwnershipStore ?? new EfLibraryDirectoryOwnershipStore(dbContextFactory, timeProvider);
    private readonly LibraryDirectoryOwnershipBoundaryAuthorizer ownershipAuthorizer =
        ownershipAuthorizer
        ?? new LibraryDirectoryOwnershipBoundaryAuthorizer(dbContextFactory);

    internal void OnCompletionHandoff(
        Guid jobId,
        CompletionHandoffFaultPoint faultPoint) =>
        faultInjector?.OnCompletionHandoff(jobId, faultPoint);

    public async Task<AudiobookContentMoveResult> MoveContentsAsync(
        AudiobookContentMoveRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureLeaseOwnedAsync(request.JobId, request.LeaseToken, cancellationToken);

        var source = NormalizeMoveDirectoryEndpoint(request.Source);
        var target = NormalizeMoveDirectoryEndpoint(request.Target);
        var sourceSemantics = request.SourceSemantics;
        var targetSemantics = request.TargetSemantics;
        if (IsFilesystemRoot(source, sourceSemantics)
            || IsFilesystemRoot(target, targetSemantics)
            || FileSystemPathIdentity.AreEquivalentEndpoints(
                source,
                sourceSemantics,
                target,
                targetSemantics))
        {
            throw new MoveNeedsAttentionException(
                "Move source and target must be distinct non-root directories.");
        }

        ValidateMoveSourceRoot(source);
        ValidateMoveTargetRoot(target);
        await ValidatePersistedMoveIdentityAsync(
            request.JobId,
            source,
            target,
            sourceSemantics,
            targetSemantics,
            request.LeaseToken,
            cancellationToken);

        var targetInsideSource = IsSameOrInside(target, source, sourceSemantics);
        var sourceInsideTarget = IsSameOrInside(source, target, targetSemantics);

        var targetParent = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(targetParent))
        {
            throw new MoveNeedsAttentionException("Invalid target path");
        }

        var targetScaffolding = await PlanTargetScaffoldingAsync(
            request,
            source,
            target,
            targetParent,
            cancellationToken);
        ValidateMoveTargetRoot(target);

        await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
        await RecoverRecoveryMarkerWriteFilesAsync(
            source,
            request,
            source,
            target,
            cancellationToken);
        await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
        await RecoverRecoveryMarkerWriteFilesAsync(
            target,
            request,
            source,
            target,
            cancellationToken);

        var sourceRecoveryMarkerPath = GetRecoveryMarkerPath(source, request.JobId);
        var sourceRecoveryMarker = ReadRecoveryMarker(sourceRecoveryMarkerPath);
        ValidateRecoveryMarker(sourceRecoveryMarker, request, source, target);
        if (sourceRecoveryMarker != null
            && !string.Equals(
                sourceRecoveryMarker.Stage,
                AtomicRenameCompletedStage,
                StringComparison.Ordinal))
        {
            throw new MoveNeedsAttentionException(
                "A non-atomic recovery marker exists inside the move source and cannot be resumed safely.");
        }

        var recoveryMarkerPath = GetRecoveryMarkerPath(target, request.JobId);
        var recoveryMarker = ReadRecoveryMarker(recoveryMarkerPath);
        ValidateRecoveryMarker(recoveryMarker, request, source, target);
        if (sourceRecoveryMarker != null
            && (recoveryMarker != null
                || Directory.Exists(target)
                || File.Exists(target)))
        {
            throw new MoveNeedsAttentionException(
                "A source-side atomic recovery marker conflicts with existing target recovery state.");
        }

        var recoveryStage = recoveryMarker?.Stage;
        var persistedManifest = await LoadManifestAsync(
            request.JobId,
            cancellationToken);
        if (persistedManifest.Count == 0)
        {
            throw new MoveNeedsAttentionException(
                recoveryMarker != null
                    ? "A move recovery marker exists without a persisted tracked-file manifest; destination ownership cannot be proven."
                    : "The move job has no persisted tracked-file source manifest and cannot infer ownership from BasePath.");
        }

        var resumingDirectCopy = recoveryStage == CopyStartedStage && persistedManifest.Count > 0;
        var targetDirectoryOwnership = Directory.Exists(target)
            ? await LoadValidatedTargetDirectoryOwnershipAsync(
                target,
                targetSemantics,
                cancellationToken)
            : null;
        request = request with { TargetDirectoryOwnership = targetDirectoryOwnership };
        RejectUnownedPartialArtifacts(
            target,
            request.JobId,
            recoveryMarker?.StructuredMarker != null);
        EnsureTargetCanReceiveContents(
            source,
            target,
            sourceInsideTarget,
            resumingDirectCopy,
            targetSemantics,
            targetDirectoryOwnership);
        var ownedSourceDirectories = await LoadValidatedOwnedSourceDirectoriesAsync(
            source,
            sourceSemantics,
            cancellationToken);
        var ownedSourceMarkerPaths = GetOwnedSourceMarkerPaths(
            source,
            ownedSourceDirectories,
            sourceSemantics);
        var targetStructuralSpine = GetTargetStructuralSpine(
            source,
            target,
            sourceSemantics);
        ValidateExistingTargetSpine(
            targetStructuralSpine,
            target,
            sourceSemantics);
        await ValidatePersistedSourceManifestAsync(
            source,
            target,
            targetInsideSource,
            persistedManifest,
            sourceSemantics,
            cancellationToken);
        // Foreign ordinary content may coexist with the tracked-file manifest, but
        // links, reparse points, and stale Listenarr recovery artifacts anywhere in
        // the source tree still invalidate mutation authority.
        _ = ValidateSourceTreeForMove(
            source,
            target,
            targetInsideSource,
            sourceSemantics,
            cancellationToken,
            sourceRecoveryMarker == null ? null : sourceRecoveryMarkerPath,
            targetScaffolding.Select(directory => directory.Path).ToList(),
            targetStructuralSpine,
            ownedSourceMarkerPaths,
            request.SourceCleanupBoundary);

        var tempName = Path.Join(targetParent, Path.GetFileName(target) + ".tmp-" + request.JobId.ToString("N"));
        if (!FileSystemSafety.TryValidateMutationTarget(tempName, [targetParent], out tempName, out var tempReason))
        {
            logger.LogWarning("Blocked move temp path for job {JobId}: {Reason}", request.JobId, tempReason);
            throw new MoveNeedsAttentionException(tempReason);
        }

        var manifest = persistedManifest;
        ValidateTargetManifest(target, manifest, targetSemantics);
        await UpdateJobPhaseAsync(
            request.JobId,
            request.LeaseToken,
            MoveJobPhase.Planned,
            cancellationToken);
        await CreateOrValidateTargetScaffoldingAsync(
            request,
            source,
            target,
            targetScaffolding,
            cancellationToken);

        try
        {
            var sourceTreeIsExclusive = ownedSourceDirectories.Count == 0
                && await SourceTreeExactlyMatchesManifestAsync(
                    request.JobId,
                    source,
                    target,
                    targetInsideSource,
                    manifest,
                    sourceSemantics,
                    sourceRecoveryMarker == null
                        ? null
                        : sourceRecoveryMarkerPath,
                    targetScaffolding.Select(directory => directory.Path).ToList(),
                    targetStructuralSpine,
                    ownedSourceMarkerPaths,
                    cancellationToken);
            var atomicResult = sourceTreeIsExclusive
                ? await TryMoveByAtomicRenameAsync(
                    request,
                    source,
                    target,
                    tempName,
                    targetInsideSource,
                    sourceInsideTarget,
                    recoveryStage,
                    manifest,
                    sourceSemantics,
                    targetSemantics,
                    cancellationToken)
                : null;
            if (atomicResult != null)
            {
                return atomicResult;
            }

            if (sourceRecoveryMarker != null)
            {
                await RetireSourceAtomicMarkerBeforeCopyFallbackAsync(
                    request,
                    source,
                    target,
                    sourceRecoveryMarkerPath,
                    cancellationToken);
            }

            ValidateMoveSourceRoot(source);
            ValidateMoveTargetRoot(target);

            // The move operation relocates the contents of the audiobook BasePath, not the
            // BasePath directory itself. Child destinations must copy directly and skip their
            // own subtree to avoid recursively copying the destination into itself.
            var useTemp = !targetInsideSource && !Directory.Exists(target);
            var copyDestination = useTemp ? tempName : target;
            await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
            var tempOwnership = useTemp
                ? await CreateOrValidateOwnedTempDirectoryAsync(
                    tempName,
                    targetParent,
                    request,
                    source,
                    target,
                    cancellationToken)
                : null;
            if (tempOwnership != null)
            {
                await RecoverRecoveryMarkerWriteFilesAsync(
                    copyDestination,
                    request,
                    source,
                    target,
                    cancellationToken);
            }

            await EnsureCopyDestinationRootAsync(
                request,
                source,
                target,
                copyDestination,
                useTemp,
                targetSemantics,
                cancellationToken);
            if (!useTemp && !resumingDirectCopy)
            {
                await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
                await WriteRecoveryMarkerAsync(
                    copyDestination,
                    request,
                    source,
                    target,
                    CopyStartedStage,
                    cancellationToken);
            }

            await UpdateJobPhaseAsync(request.JobId, request.LeaseToken, MoveJobPhase.Copying, cancellationToken);
            await CopySourceContentsAsync(
                request,
                source,
                target,
                copyDestination,
                manifest,
                sourceSemantics,
                targetSemantics,
                tempOwnership,
                directCopyOwnershipValidated: !useTemp,
                cancellationToken);

            ValidateExistingDestinationContents(
                source,
                copyDestination,
                manifest,
                request.JobId,
                targetSemantics,
                tempOwnership,
                quarantineOwnership: null,
                allowPartialFiles: false,
                targetDirectoryOwnership: request.TargetDirectoryOwnership);
            await VerifyPublishedManifestAsync(copyDestination, manifest, targetSemantics, cancellationToken);
            await UpdateCopyStateAsync(request.JobId, request.LeaseToken, cancellationToken);

            await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
            await WriteRecoveryMarkerAsync(
                copyDestination,
                request,
                source,
                target,
                CopyCompletedStage,
                cancellationToken);

            if (useTemp)
            {
                await PublishOwnedTempDirectoryAsync(
                    request,
                    source,
                    target,
                    tempName,
                    targetParent,
                    cancellationToken);
            }

            await UpdateJobPhaseAsync(request.JobId, request.LeaseToken, MoveJobPhase.Published, cancellationToken);

            if (faultInjector != null)
            {
                await faultInjector.AfterPublishedAsync(request.JobId, cancellationToken);
            }

            await UpdateJobPhaseAsync(request.JobId, request.LeaseToken, MoveJobPhase.CleaningSource, cancellationToken);
            await DeleteOriginalSourceAsync(
                source,
                target,
                targetInsideSource,
                request.DeleteEmptySource,
                request.JobId,
                request.LeaseToken,
                manifest,
                sourceSemantics,
                targetSemantics,
                request.TargetDirectoryOwnership,
                request.SourceCleanupBoundary,
                request.SourcePhysicalObjectIdentities,
                cancellationToken);
            VerifySourceCleanupState(request, source, target, manifest);
            await UpdateJobPhaseAsync(request.JobId, request.LeaseToken, MoveJobPhase.Finalizing, cancellationToken);
            await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
            await WriteRecoveryMarkerAsync(
                target,
                request,
                source,
                target,
                SourceCleanupCompletedStage,
                cancellationToken);
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
                SourceCleanupCompleted: true,
                targetPhysicalObjectIdentities);
        }
        catch (Exception exception) when (exception is MoveLeaseLostException or PersistenceException)
        {
            throw;
        }
        catch (MoveNeedsAttentionException)
        {
            await TryDeleteOwnedTempDirectoryAsync(
                tempName,
                targetParent,
                request,
                source,
                target,
                cancellationToken);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The temp directory and its structured ownership marker are durable retry
            // state. Preserve verified files so a transient failure resumes instead of
            // restarting the entire copy.
            throw;
        }
        catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
        {
            await TryDeleteOwnedTempDirectoryAsync(
                tempName,
                targetParent,
                request,
                source,
                target,
                cancellationToken);
            throw;
        }
    }

}
