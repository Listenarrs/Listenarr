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

    private async Task RemoveEmptyDirectoryTreeAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        string directory,
        string boundary,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken)
    {
        var current = directory;
        while (Directory.Exists(current)
            && !FileSystemPathIdentity.AreEquivalent(
                current,
                boundary,
                semantics))
        {
            if (!FileSystemSafety.TryValidateMutationTarget(
                    current,
                    [boundary],
                    out current,
                    out var reason))
            {
                throw new MoveNeedsAttentionException(reason);
            }

            var ownership = await ResolveOwnedDirectoryForCleanupAsync(
                current,
                semantics,
                cancellationToken);
            if (ownership == null)
            {
                return;
            }
            if (ownership.State == LibraryDirectoryOwnershipState.Removing)
            {
                var interruptedRemovalCompleted = await ResumeOwnedDirectoryRemovalAsync(
                    request,
                    source,
                    target,
                    ownership,
                    cancellationToken);
                if (!interruptedRemovalCompleted)
                {
                    return;
                }

                current = Path.GetDirectoryName(current) ?? boundary;
                continue;
            }

            ValidateExistingMoveDirectory(current, "source ancestor cleanup directory");
            if (!LibraryDirectoryOwnershipMarker.ContainsOnlyInsideMarker(
                    ownership,
                    current))
            {
                return;
            }

            faultInjector?.OnMoveFinalization(
                request.JobId,
                MoveFinalizationFaultPoint.BeforeSourceAncestorDelete);
            await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
            var finalOwnership = await ResolveOwnedDirectoryForCleanupAsync(
                current,
                semantics,
                cancellationToken);
            if (finalOwnership == null
                || finalOwnership.Id != ownership.Id
                || !string.Equals(
                    finalOwnership.PathOwnershipKey,
                    ownership.PathOwnershipKey,
                    StringComparison.Ordinal))
            {
                throw new MoveNeedsAttentionException(
                    "The durable directory ownership claim changed before source-parent cleanup.");
            }

            ValidateExistingMoveDirectory(current, "source ancestor cleanup directory");
            if (!LibraryDirectoryOwnershipMarker.ContainsOnlyInsideMarker(
                    finalOwnership,
                    current))
            {
                return;
            }

            var ownershipKey = finalOwnership.PathOwnershipKey
                ?? throw new MoveNeedsAttentionException(
                    "The durable directory ownership key is unavailable.");
            await directoryOwnershipStore.BeginRemovalAsync(
                finalOwnership.Id,
                ownershipKey,
                cancellationToken);
            var removalCompleted = await ResumeOwnedDirectoryRemovalAsync(
                request,
                source,
                target,
                finalOwnership,
                cancellationToken);
            if (!removalCompleted)
            {
                return;
            }

            current = Path.GetDirectoryName(current) ?? boundary;
        }
    }

    private static bool IsSourceCleanupBoundary(
        string path,
        string? boundary,
        FileSystemPathSemantics semantics)
    {
        if (string.IsNullOrWhiteSpace(boundary))
        {
            return false;
        }

        try
        {
            return FileSystemPathIdentity.AreEquivalent(path, boundary, semantics);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
        {
            throw new MoveNeedsAttentionException(
                $"The source cleanup boundary is invalid: {exception.Message}");
        }
    }

    private async Task RemoveEmptySourceAncestorsAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        string? boundary,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(boundary))
        {
            return;
        }

        var fullBoundary = Path.GetFullPath(boundary);
        var current = Path.GetDirectoryName(Path.GetFullPath(source));
        while (current != null
            && FileSystemPathIdentity.IsSameOrInside(current, fullBoundary, semantics))
        {
            if (FileSystemPathIdentity.AreEquivalent(current, fullBoundary, semantics))
            {
                return;
            }

            if (Directory.Exists(current))
            {
                await RemoveEmptyDirectoryTreeAsync(
                    request,
                    source,
                    target,
                    current,
                    fullBoundary,
                    semantics,
                    cancellationToken);
                return;
            }

            var ownership = await ResolveOwnedDirectoryForCleanupAsync(
                current,
                semantics,
                cancellationToken);
            if (ownership != null)
            {
                if (ownership.State != LibraryDirectoryOwnershipState.Removing)
                {
                    throw new MoveNeedsAttentionException(
                        "An owned source-parent directory disappeared without a durable cleanup intent.");
                }

                await ResumeOwnedDirectoryRemovalAsync(
                    request,
                    source,
                    target,
                    ownership,
                    cancellationToken);
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private async Task<LibraryDirectoryOwnership?> ResolveOwnedDirectoryForCleanupAsync(
        string directory,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken)
    {
        var resolution = await directoryOwnershipStore.ResolveOwnedAsync(
            directory,
            semantics,
            cancellationToken);
        return resolution.State switch
        {
            LibraryDirectoryOwnershipResolutionState.Owned
                when resolution.Ownership != null => resolution.Ownership,
            LibraryDirectoryOwnershipResolutionState.Unowned => null,
            LibraryDirectoryOwnershipResolutionState.Conflict =>
                throw new MoveNeedsAttentionException(
                    resolution.Reason
                        ?? "Conflicting durable directory ownership claims prevent cleanup."),
            LibraryDirectoryOwnershipResolutionState.Unavailable =>
                throw new MoveNeedsAttentionException(
                    resolution.Reason
                        ?? "Durable directory ownership is unavailable for cleanup."),
            _ => throw new MoveNeedsAttentionException(
                "Durable directory ownership could not be resolved for cleanup.")
        };
    }
}
