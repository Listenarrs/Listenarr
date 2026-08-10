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

        if (Directory.Exists(target))
        {
            await TryRetireReplacedMarkerlessTargetOwnershipAsync(
                request,
                target,
                cancellationToken);
        }
        var targetOwnership = Directory.Exists(target)
            ? await LoadValidatedTargetDirectoryOwnershipAsync(
                target,
                request.TargetSemantics,
                cancellationToken)
            : null;
        request = request with { TargetDirectoryOwnership = targetOwnership };
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

        EnsureTargetCanReceiveContents(
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
        ValidateUnixMarkerlessMoveVolumes(
            request,
            source,
            target,
            manifest
                .Where(candidate => candidate.EntryType == MoveJobEntryType.File)
                .Where(IsPhysicalManifestEntry)
                .ToList());

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

            await UpdateJobPhaseAsync(
                request.JobId,
                request.LeaseToken,
                MoveJobPhase.CleaningSource,
                cancellationToken);
            await ReportProgressAsync(request, 75, "Cleaning source", cancellationToken);
            await DeleteMarkerlessSourceAsync(
                request,
                source,
                target,
                targetInsideSource,
                manifest,
                cancellationToken);
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
                targetVerificationLease.IsEmpty ? null : targetVerificationLease);
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
        var cleanupStarted = physicalEntries.Any(entry =>
                entry.CleanupState != MoveJobEntryCleanupState.Pending)
            || endpoints.SourceDirectoryCleanupState
                != MoveJobEntryCleanupState.Pending;
        if (!cleanupStarted)
        {
            return null;
        }

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
        await DeleteMarkerlessSourceAsync(
            request,
            source,
            target,
            targetInsideSource,
            manifest,
            cancellationToken);
        VerifySourceCleanupState(request, source, target, manifest);
        await UpdateJobPhaseAsync(
            request.JobId,
            request.LeaseToken,
            MoveJobPhase.Finalizing,
            cancellationToken);
        return CreateMarkerlessMoveResult(
            request,
            source,
            target,
            targetInsideSource,
            sourceInsideTarget,
            manifest);
    }

    private static AudiobookContentMoveResult CreateMarkerlessMoveResult(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        bool targetInsideSource,
        bool sourceInsideTarget,
        IEnumerable<MoveJobEntry> manifest,
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
        if (!Directory.Exists(target))
        {
            throw new MoveNeedsAttentionException(
                "The verified markerless move target is missing.");
        }

        faultInjector?.OnFinalizedVerification(
            request.JobId,
            FinalizedVerificationFaultPoint.BeforeManifestVerification);
        await VerifyMarkerlessTargetAsync(
            request,
            target,
            manifest,
            cancellationToken);
        var endpoints = await GetEndpointObjectIdentitiesAsync(
            request.JobId,
            cancellationToken);
        var sourceEntriesComplete = manifest
            .Where(IsPhysicalManifestEntry)
            .All(entry => entry.CleanupState is
                MoveJobEntryCleanupState.Deleted
                or MoveJobEntryCleanupState.Retained);
        var sourceRootComplete = endpoints.SourceDirectoryCleanupState is
            MoveJobEntryCleanupState.Deleted
            or MoveJobEntryCleanupState.Retained;
        if (!sourceEntriesComplete || !sourceRootComplete)
        {
            return null;
        }

        VerifySourceCleanupState(request, source, target, manifest);
        var identities = CreatePersistedTargetPhysicalIdentityMap(
            target,
            files,
            request.TargetSemantics);
        return new AudiobookContentMoveResult(
            source,
            target,
            IsSameOrInside(target, source, request.SourceSemantics),
            IsSameOrInside(source, target, request.TargetSemantics),
            SourceCleanupCompleted: true,
            identities);
    }

    private static bool IsPhysicalManifestEntry(MoveJobEntry entry) =>
        !IsRootManifestEntry(entry)
        && !MoveManifestIdentity.IsTargetBoundaryAuthorization(entry);

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
