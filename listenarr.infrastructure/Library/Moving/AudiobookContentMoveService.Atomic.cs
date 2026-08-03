using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task<AudiobookContentMoveResult?> TryMoveByAtomicRenameAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        string tempName,
        bool targetInsideSource,
        bool sourceInsideTarget,
        string? recoveryStage,
        IReadOnlyList<MoveJobEntry> manifest,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()
            || targetInsideSource
            || sourceInsideTarget
            || (faultInjector != null && !faultInjector.AllowAtomicRename)
            || request.SourcePhysicalObjectIdentities is { Count: > 0 }
            || !request.DeleteEmptySource
            || IsSourceCleanupBoundary(source, request.SourceCleanupBoundary, sourceSemantics)
            || Directory.Exists(target)
            || Directory.Exists(tempName)
            || recoveryStage != null)
        {
            return null;
        }

        await EnsureLeaseOwnedAsync(request.JobId, request.LeaseToken, cancellationToken);
        await ValidatePersistedMoveIdentityAsync(
            request.JobId,
            source,
            target,
            sourceSemantics,
            targetSemantics,
            request.LeaseToken,
            cancellationToken);
        ValidateMoveSourceRoot(source);
        ValidateMoveTargetRoot(target);
        if (Directory.Exists(target))
        {
            throw new MoveNeedsAttentionException(
                "Atomic rename target appeared after validation; no filesystem mutation was performed.");
        }

        var atomicMarkerPath = GetRecoveryMarkerPath(source, request.JobId);
        await WriteRecoveryMarkerAsync(
            source,
            request,
            source,
            target,
            AtomicRenameCompletedStage,
            cancellationToken);
        var renameCompleted = false;
        try
        {
            // Recheck both roots after publishing the durable marker and immediately
            // before the rename so a linked or newly occupied target is never followed.
            await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
            ValidateMoveSourceRoot(source);
            ValidateMoveTargetRoot(target);
            if (Directory.Exists(target))
            {
                throw new MoveNeedsAttentionException(
                    "Atomic rename target appeared before publication; no directory was moved.");
            }

            faultInjector?.OnAtomicRename(
                request.JobId,
                AtomicRenameFaultPoint.BeforeSourceRevalidation);
            var currentSource = await SnapshotSourceAsync(
                request.JobId,
                source,
                target,
                targetInsideSource: false,
                sourceSemantics,
                cancellationToken,
                atomicMarkerPath);
            var expectedSource = manifest
                .Where(entry => !IsRootManifestEntry(entry))
                .ToList();
            if (!ManifestMatches(expectedSource, currentSource, sourceSemantics))
            {
                throw new MoveNeedsAttentionException(
                    "Source content changed after the atomic move was planned; the directory was not moved.");
            }

            await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
            ValidateMoveSourceRoot(source);
            ValidateMoveTargetRoot(target);
            ValidateExistingRecoveryMarkerForStage(
                source,
                atomicMarkerPath,
                request,
                source,
                target,
                AtomicRenameCompletedStage);
            if (Directory.Exists(target))
            {
                throw new MoveNeedsAttentionException(
                    "Atomic rename target appeared immediately before publication; no directory was moved.");
            }

            var sourceParent = Path.GetDirectoryName(source)
                ?? throw new MoveNeedsAttentionException("The atomic move source parent is unavailable.");
            var targetParent = Path.GetDirectoryName(target)
                ?? throw new MoveNeedsAttentionException("The atomic move target parent is unavailable.");
            using var sourcePublication = PinnedDirectoryCreation.OpenExistingForPublication(
                sourceParent,
                Path.GetFileName(source));
            using var targetParentAnchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
                targetParent);
            if (!sourcePublication.VisiblePathMatches()
                || !targetParentAnchor.VisiblePathMatches())
            {
                throw new MoveNeedsAttentionException(
                    "The atomic move source or target parent changed while it was being pinned.");
            }

            faultInjector?.OnAtomicRename(
                request.JobId,
                AtomicRenameFaultPoint.BeforeDirectoryPublication);
            await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
            if (!sourcePublication.VisiblePathMatches()
                || !targetParentAnchor.VisiblePathMatches())
            {
                throw new MoveNeedsAttentionException(
                    "The atomic move source or target parent changed at publication.");
            }
            if (Directory.Exists(target) || File.Exists(target))
            {
                throw new MoveNeedsAttentionException(
                    "The atomic move target appeared at publication; no directory was moved.");
            }

            using var publishedAnchor = sourcePublication.PublishCreatedDirectoryTo(
                targetParentAnchor,
                Path.GetFileName(target));
            if (!publishedAnchor.VisiblePathMatches(target))
            {
                throw new MoveNeedsAttentionException(
                    "The atomic move target does not identify the pinned source directory.");
            }

            renameCompleted = true;
            faultInjector?.OnAtomicRename(
                request.JobId,
                AtomicRenameFaultPoint.AfterDirectoryMoveBeforeVerification);
            ValidateExistingDestinationContents(
                source,
                target,
                manifest,
                request.JobId,
                targetSemantics,
                allowPartialFiles: false);
            await VerifyPublishedManifestAsync(
                target,
                manifest,
                targetSemantics,
                cancellationToken);
            await UpdateCopyStateAsync(
                request.JobId,
                request.LeaseToken,
                cancellationToken);
        }
        catch (MoveNeedsAttentionException)
        {
            await DeleteFailedAtomicMarkerAsync(
                request,
                atomicMarkerPath,
                source,
                target,
                null,
                cancellationToken);
            throw;
        }
        catch (Exception exception) when (
            !renameCompleted
            && exception is IOException or UnauthorizedAccessException)
        {
            await DeleteFailedAtomicMarkerAsync(
                request,
                atomicMarkerPath,
                source,
                target,
                exception,
                cancellationToken);
            ValidateMoveTargetRoot(target);
            if (!Directory.Exists(source) || Directory.Exists(target))
            {
                throw new MoveNeedsAttentionException(
                    "Atomic rename failed with an ambiguous source or target state; copy fallback was blocked.");
            }

            return null;
        }

        await UpdateJobPhaseAsync(
            request.JobId,
            request.LeaseToken,
            MoveJobPhase.Finalizing,
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
            false,
            false,
            GetRecoveryMarkerPath(target, request.JobId),
            SourceCleanupCompleted: true,
            targetPhysicalObjectIdentities);
    }

    private async Task DeleteFailedAtomicMarkerAsync(
        AudiobookContentMoveRequest request,
        string atomicMarkerPath,
        string source,
        string target,
        Exception? renameException,
        CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(atomicMarkerPath))
            {
                await RetirePinnedArtifactAsync(
                    atomicMarkerPath,
                    entry =>
                    {
                        ValidateMoveSourceRoot(source);
                        ValidatePinnedAtomicMarker(
                            entry,
                            atomicMarkerPath,
                            request,
                            source,
                            target);
                    },
                    () => EnsureMutationAuthorizedAsync(
                        request,
                        source,
                        target,
                        cancellationToken));
            }
        }
        catch (Exception exception) when (exception is MoveLeaseLostException or PersistenceException)
        {
            throw;
        }
        catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
        {
            throw new MoveNeedsAttentionException(
                $"Atomic rename failed and its recovery marker could not be removed. "
                + $"Rename error: {renameException?.Message ?? "precondition changed"}. "
                + $"Marker cleanup error: {exception.Message}");
        }
    }
}
