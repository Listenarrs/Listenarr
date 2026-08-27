using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task<AudiobookContentMoveResult> MoveContentsMarkerlessAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        bool targetInsideSource,
        bool sourceInsideTarget,
        CancellationToken cancellationToken)
    {
        await ReportProgressAsync(request, 2, "Preparing", cancellationToken);
        var manifest = await LoadManifestAsync(request.JobId, cancellationToken);
        if (manifest.Count == 0)
        {
            throw new MoveNeedsAttentionException(
                "The markerless move has no persisted tracked-file source manifest.");
        }

        request = await WithValidatedTargetDirectoryOwnershipAsync(
            request,
            cancellationToken);
        var targetOwnership = request.TargetDirectoryOwnership;
        var physicalFiles = manifest
            .Where(candidate => candidate.EntryType == MoveJobEntryType.File)
            .Where(IsPhysicalManifestEntry)
            .ToList();
        var resumedCleanup = await TryResumeMarkerlessSourceCleanupAsync(
            request,
            source,
            target,
            targetInsideSource,
            sourceInsideTarget,
            manifest,
            cancellationToken);
        if (resumedCleanup != null)
        {
            return resumedCleanup;
        }

        var crossVolumeMove = IsUnixCrossVolumeMove(
            request,
            source,
            target,
            physicalFiles);

        EnsureTargetCanReceiveContents(
            request,
            source,
            target,
            sourceInsideTarget,
            resumingOwnedDirectCopy: true,
            request.TargetSemantics,
            targetOwnership);
        await RecoverInterruptedMarkerlessNativeRenamesAsync(
            request,
            source,
            target,
            manifest,
            cancellationToken);

        _ = await LoadValidatedOwnedSourceDirectoriesAsync(
            source,
            request.SourceSemantics,
            cancellationToken);
        var targetStructuralSpine = GetTargetStructuralSpine(
            source,
            target,
            request.SourceSemantics);
        ValidateExistingTargetSpine(
            targetStructuralSpine,
            target,
            request.SourceSemantics);
        await ValidatePersistedSourceManifestAsync(
            source,
            target,
            targetInsideSource,
            manifest,
            request.SourceSemantics,
            cancellationToken,
            verifyFileContents: false,
            allowVerifiedNativeRenameMissingSources: true);
        _ = ValidateSourceTreeForMove(
            source,
            target,
            targetInsideSource,
            request.SourceSemantics,
            cancellationToken,
            structuralSpinePaths: targetStructuralSpine);
        await ReportProgressAsync(request, 3, "Capturing source", cancellationToken);
        await CaptureMarkerlessSourceIdentitiesAsync(
            request,
            source,
            manifest,
            cancellationToken);
        await ReportProgressAsync(request, 5, "Planning", cancellationToken);
        await UpdateJobPhaseAsync(
            request.JobId,
            request.LeaseToken,
            MoveJobPhase.Planned,
            cancellationToken);
        await CreateMarkerlessTargetDirectoriesAsync(
            request,
            target,
            manifest,
            cancellationToken);
        await CaptureOrValidateMarkerlessTargetRootAsync(
            request,
            target,
            cancellationToken);
        await TryRetireReplacedMarkerlessTargetOwnershipAsync(
            request,
            target,
            cancellationToken);

        ValidateExistingDestinationContents(
            request,
            source,
            target,
            manifest,
            request.TargetSemantics,
            request.TargetDirectoryOwnership);
        await UpdateJobPhaseAsync(
            request.JobId,
            request.LeaseToken,
            MoveJobPhase.Copying,
            cancellationToken);
        await ReportProgressAsync(request, 5, "Verifying source", cancellationToken);
        var targetVerificationLease = new MarkerlessTargetVerificationLease(
            request.TargetSemantics);
        try
        {
            await CopyMarkerlessTargetFilesAsync(
                request,
                source,
                target,
                manifest,
                crossVolumeMove || request.ForceCopyAndRetainSource,
                targetVerificationLease,
                cancellationToken);
            await UpdateJobPhaseAsync(
                request.JobId,
                request.LeaseToken,
                MoveJobPhase.Published,
                cancellationToken);
            await ReportProgressAsync(request, 72, "Copy verified", cancellationToken);

            if (faultInjector != null)
            {
                await faultInjector.AfterPublishedAsync(request.JobId, cancellationToken);
            }

            // Cross-volume moves always copy the complete manifest first. Only after every
            // target is durably verified do we revalidate the persisted root-folder policy
            // snapshot and decide whether source deletion may begin.
            var retainSource = request.ForceCopyAndRetainSource
                || crossVolumeMove
                    && !await CanDeleteVerifiedCrossVolumeSourceAsync(
                        request,
                        cancellationToken);

            await UpdateJobPhaseAsync(
                request.JobId,
                request.LeaseToken,
                MoveJobPhase.CleaningSource,
                cancellationToken);
            await ReportProgressAsync(
                request,
                75,
                retainSource ? "Retaining source" : "Cleaning source",
                cancellationToken);
            if (retainSource)
            {
                await RetainMarkerlessSourceAsync(
                    request,
                    target,
                    manifest,
                    cancellationToken);
            }
            else
            {
                await DeleteMarkerlessSourceAsync(
                    request,
                    source,
                    target,
                    targetInsideSource,
                    manifest,
                    cancellationToken);
            }
            VerifySourceCleanupState(request, source, target, manifest);
            await UpdateJobPhaseAsync(
                request.JobId,
                request.LeaseToken,
                MoveJobPhase.Finalizing,
                cancellationToken);
            await ReportProgressAsync(request, 92, "Finalizing", cancellationToken);

            return CreateMarkerlessMoveResult(
                request,
                source,
                target,
                targetInsideSource,
                sourceInsideTarget,
                manifest,
                retainSource,
                targetVerificationLease);
        }
        catch
        {
            targetVerificationLease.Dispose();
            throw;
        }
    }

    private async Task<AudiobookContentMoveResult?> TryResumeMarkerlessSourceCleanupAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        bool targetInsideSource,
        bool sourceInsideTarget,
        IReadOnlyCollection<MoveJobEntry> manifest,
        CancellationToken cancellationToken)
    {
        var endpoints = await GetEndpointObjectIdentitiesAsync(
            request.JobId,
            cancellationToken);
        var physicalEntries = manifest
            .Where(IsPhysicalManifestEntry)
            .ToList();
        var cleanupDisposition = ResolveMarkerlessSourceCleanupDisposition(
            endpoints.SourceDirectoryCleanupState,
            physicalEntries);
        if (cleanupDisposition == MarkerlessSourceCleanupDisposition.NotStarted)
        {
            return null;
        }
        if (request.ForceCopyAndRetainSource
            && cleanupDisposition == MarkerlessSourceCleanupDisposition.Delete)
        {
            throw new MoveNeedsAttentionException(
                "Forced source retention cannot resume after destructive source cleanup was authorized.");
        }
        var retainSource = cleanupDisposition
            == MarkerlessSourceCleanupDisposition.Retain;

        if (physicalEntries
            .Where(entry => entry.EntryType == MoveJobEntryType.File)
            .Any(entry => entry.CopyState != MoveJobEntryCopyState.Verified
                || string.IsNullOrWhiteSpace(
                    entry.TargetPhysicalObjectIdentity)))
        {
            throw new MoveNeedsAttentionException(
                "Markerless source cleanup started before every target file was durably verified.");
        }
        if (physicalEntries.Any(entry => string.IsNullOrWhiteSpace(
                entry.SourcePhysicalObjectIdentity)))
        {
            throw new MoveNeedsAttentionException(
                "Markerless source cleanup lacks persisted source-generation evidence.");
        }

        await UpdateJobPhaseAsync(
            request.JobId,
            request.LeaseToken,
            MoveJobPhase.CleaningSource,
            cancellationToken);
        if (retainSource)
        {
            if (physicalEntries.Any(entry => entry.CleanupState is
                    MoveJobEntryCleanupState.DeleteAuthorized
                        or MoveJobEntryCleanupState.Deleted))
            {
                throw new MoveNeedsAttentionException(
                    "Cross-volume source retention cannot resume after destructive source cleanup began.");
            }
            await RetainMarkerlessSourceAsync(
                request,
                target,
                manifest,
                cancellationToken);
        }
        else
        {
            await DeleteMarkerlessSourceAsync(
                request,
                source,
                target,
                targetInsideSource,
                manifest,
                cancellationToken);
        }
        VerifySourceCleanupState(request, source, target, manifest);
        await UpdateJobPhaseAsync(
            request.JobId,
            request.LeaseToken,
            MoveJobPhase.Finalizing,
            cancellationToken);
        MarkerlessTargetVerificationLease? targetVerificationLease =
            new(request.TargetSemantics);
        try
        {
            await VerifyMarkerlessTargetAsync(
                request,
                target,
                manifest,
                cancellationToken,
                targetVerificationLease: targetVerificationLease);
            return CreateMarkerlessMoveResult(
                request,
                source,
                target,
                targetInsideSource,
                sourceInsideTarget,
                manifest,
                retainSource,
                targetVerificationLease);
        }
        catch
        {
            targetVerificationLease.Dispose();
            throw;
        }
    }

    private static AudiobookContentMoveResult CreateMarkerlessMoveResult(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        bool targetInsideSource,
        bool sourceInsideTarget,
        IEnumerable<MoveJobEntry> manifest,
        bool sourceRetained,
        MarkerlessTargetVerificationLease? targetVerificationLease = null)
    {
        var targetIdentities = CreatePersistedTargetPhysicalIdentityMap(
            target,
            manifest,
            request.TargetSemantics);
        return new AudiobookContentMoveResult(
            source,
            target,
            targetInsideSource,
            sourceInsideTarget,
            SourceCleanupCompleted: true,
            SourceRetained: sourceRetained,
            targetIdentities,
            targetVerificationLease);
    }

    private async Task<AudiobookContentMoveResult?> GetMarkerlessRecoverableMoveAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        var manifest = await LoadManifestAsync(request.JobId, cancellationToken);
        if (manifest.Count == 0)
        {
            throw new MoveNeedsAttentionException(
                "A markerless move has no persisted manifest for recovery.");
        }

        var files = manifest
            .Where(entry => entry.EntryType == MoveJobEntryType.File)
            .Where(IsPhysicalManifestEntry)
            .ToList();
        if (files.Any(entry => entry.CopyState != MoveJobEntryCopyState.Verified))
        {
            return null;
        }
        if (!TryGetMarkerlessPathAttributes(target, out var targetAttributes))
        {
            throw new MoveNeedsAttentionException(
                "The verified markerless move target is missing.");
        }
        if ((targetAttributes & FileAttributes.Directory) == 0
            || (targetAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new MoveNeedsAttentionException(
                "The verified markerless move target changed type or became a link.");
        }

        faultInjector?.OnFinalizedVerification(
            request.JobId,
            FinalizedVerificationFaultPoint.BeforeManifestVerification);
        var targetVerificationLease = new MarkerlessTargetVerificationLease(
            request.TargetSemantics);
        try
        {
            await VerifyMarkerlessTargetAsync(
                request,
                target,
                manifest,
                cancellationToken,
                targetVerificationLease: targetVerificationLease);
            var endpoints = await GetEndpointObjectIdentitiesAsync(
                request.JobId,
                cancellationToken);
            var completedCleanupState = endpoints.SourceDirectoryCleanupState;
            var sourceRootComplete = completedCleanupState is
                MoveJobEntryCleanupState.Deleted
                or MoveJobEntryCleanupState.Retained;
            var physicalEntries = manifest
                .Where(IsPhysicalManifestEntry)
                .ToList();
            var sourceEntriesComplete = physicalEntries.All(entry =>
                entry.CleanupState is MoveJobEntryCleanupState.Deleted
                    or MoveJobEntryCleanupState.Retained);
            if (!sourceEntriesComplete || !sourceRootComplete)
            {
                return null;
            }

            VerifySourceCleanupState(request, source, target, manifest);
            var sourceRetained = ResolveCompletedMarkerlessSourceRetention(
                completedCleanupState,
                physicalEntries);
            if (request.ForceCopyAndRetainSource && !sourceRetained)
            {
                throw new MoveNeedsAttentionException(
                    "Forced source retention has contradictory completed destructive cleanup evidence.");
            }
            var identities = CreatePersistedTargetPhysicalIdentityMap(
                target,
                files,
                request.TargetSemantics);
            if (targetVerificationLease.IsEmpty)
            {
                return new AudiobookContentMoveResult(
                    source,
                    target,
                    IsSameOrInside(target, source, request.SourceSemantics),
                    IsSameOrInside(source, target, request.TargetSemantics),
                    SourceCleanupCompleted: true,
                    SourceRetained: sourceRetained,
                    identities);
            }

            var result = new AudiobookContentMoveResult(
                source,
                target,
                IsSameOrInside(target, source, request.SourceSemantics),
                IsSameOrInside(source, target, request.TargetSemantics),
                SourceCleanupCompleted: true,
                SourceRetained: sourceRetained,
                identities,
                targetVerificationLease);
            targetVerificationLease = null;
            return result;
        }
        finally
        {
            targetVerificationLease?.Dispose();
        }
    }

    private static bool IsPhysicalManifestEntry(MoveJobEntry entry) =>
        !IsRootManifestEntry(entry)
        && !MoveManifestIdentity.IsBoundaryAuthorization(entry);

    private static string ResolveManifestPath(
        string root,
        MoveJobEntry entry,
        FileSystemPathSemantics semantics,
        string endpoint)
    {
        if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                root,
                entry.RelativePath,
                semantics,
                out var path))
        {
            throw new MoveNeedsAttentionException(
                $"A manifest entry escaped the markerless {endpoint} root: {entry.RelativePath}");
        }
        return path;
    }
}
