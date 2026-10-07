namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task<bool> ReconcileRestartedMarkerlessSourceAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        IReadOnlyCollection<MoveJobEntry> manifest,
        CancellationToken cancellationToken)
    {
        await VerifyMarkerlessTargetAsync(
            request,
            target,
            manifest,
            cancellationToken);

        var sourceRetained = false;
        foreach (var entry in manifest.Where(IsPhysicalManifestEntry))
        {
            var sourcePath = ResolveManifestPath(
                source,
                entry,
                request.SourceSemantics,
                "source");
            var exists = TryGetMarkerlessPathAttributes(
                sourcePath,
                out var attributes);
            if (exists)
            {
                var expectedDirectory =
                    entry.EntryType == MoveJobEntryType.Directory;
                if (((attributes & FileAttributes.Directory) != 0)
                    != expectedDirectory
                    || (attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new MoveNeedsAttentionException(
                        $"A surviving source entry changed type during restart recovery: {entry.RelativePath}");
                }

                if (expectedDirectory)
                {
                    await ReconcileRestartedDirectoryOwnershipAsync(
                        request, sourcePath, exists: true, cancellationToken);
                }

                if (entry.CleanupState != MoveJobEntryCleanupState.Retained)
                {
                    await UpdateCleanupStateAsync(
                        request.JobId,
                        request.LeaseToken,
                        entry.RelativePath,
                        MoveJobEntryCleanupState.Retained,
                        cancellationToken);
                    entry.CleanupState = MoveJobEntryCleanupState.Retained;
                }
                sourceRetained = true;
                continue;
            }

            if (entry.EntryType == MoveJobEntryType.Directory)
            {
                await ReconcileRestartedDirectoryOwnershipAsync(
                    request, sourcePath, exists: false, cancellationToken);
            }

            if (entry.CleanupState != MoveJobEntryCleanupState.Deleted)
            {
                await UpdateCleanupStateAsync(
                    request.JobId,
                    request.LeaseToken,
                    entry.RelativePath,
                    MoveJobEntryCleanupState.Deleted,
                    cancellationToken);
                entry.CleanupState = MoveJobEntryCleanupState.Deleted;
            }
        }

        var sourceExists = TryGetMarkerlessPathAttributes(
            source,
            out var sourceAttributes);
        if (sourceExists
            && ((sourceAttributes & FileAttributes.Directory) == 0
                || (sourceAttributes & FileAttributes.ReparsePoint) != 0))
        {
            throw new MoveNeedsAttentionException(
                "The surviving move source changed type during restart recovery.");
        }

        await ReconcileRestartedDirectoryOwnershipAsync(
            request, source, sourceExists, cancellationToken);

        await UpdateSourceDirectoryCleanupStateAsync(
            request.JobId,
            request.LeaseToken,
            sourceExists
                ? MoveJobEntryCleanupState.Retained
                : MoveJobEntryCleanupState.Deleted,
            cancellationToken);
        sourceRetained |= sourceExists;

        await ReportProgressAsync(
            request,
            90,
            sourceRetained
                ? "Source retained after restart"
                : "Source already absent after restart",
            cancellationToken);
        return sourceRetained;
    }

    private async Task ReconcileRestartedDirectoryOwnershipAsync(
        AudiobookContentMoveRequest request,
        string path,
        bool exists,
        CancellationToken cancellationToken)
    {
        var ownership = await ResolveMarkerlessSourceDirectoryOwnershipAsync(
            path, request.SourceSemantics, cancellationToken);
        if (exists)
        {
            await RetainMarkerlessOwnedDirectoryIfRemovingAsync(
                ownership,
                "The source survived a process boundary; restart recovery retained it.",
                cancellationToken);
        }
        else if (ownership?.State == LibraryDirectoryOwnershipState.Removing)
        {
            var ownershipKey = ownership.PathOwnershipKey
                ?? throw new MoveNeedsAttentionException(
                    "The absent source ownership has no durable ownership key.");
            // An absent path is an observed outcome, never authority to delete.
            await directoryOwnershipStore.MarkRemovedAsync(
                ownership.Id, ownershipKey, cancellationToken);
        }
    }

    private async Task RetainMarkerlessSourceAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        bool adoptedPriorTarget,
        IReadOnlyCollection<MoveJobEntry> manifest,
        CancellationToken cancellationToken)
    {
        if (adoptedPriorTarget)
        {
            // Absent sources are observations; surviving sources lose retirement authority.
            await ReconcileRestartedMarkerlessSourceAsync(
                request, source, target, manifest, cancellationToken);
            return;
        }
        await VerifyMarkerlessTargetAsync(
            request,
            target,
            manifest,
            cancellationToken);

        foreach (var entry in manifest.Where(IsPhysicalManifestEntry))
        {
            if (entry.CleanupState is
                MoveJobEntryCleanupState.DeleteAuthorized
                    or MoveJobEntryCleanupState.Deleted)
            {
                throw new MoveNeedsAttentionException(
                    $"Source retention cannot replace destructive cleanup already recorded for: {entry.RelativePath}");
            }

            await RetainMarkerlessSourceEntryAsync(
                request,
                entry,
                cancellationToken);
            faultInjector?.OnSourceRetentionMutation(
                request.JobId,
                SourceRetentionFaultPoint.AfterEntryStateUpdate);
        }

        var endpoints = await GetEndpointObjectIdentitiesAsync(
            request.JobId,
            cancellationToken);
        if (endpoints.SourceDirectoryCleanupState is
            MoveJobEntryCleanupState.DeleteAuthorized
                or MoveJobEntryCleanupState.Deleted)
        {
            throw new MoveNeedsAttentionException(
                "Source retention cannot replace destructive source-root cleanup already recorded.");
        }
        if (endpoints.SourceDirectoryCleanupState
            != MoveJobEntryCleanupState.Retained)
        {
            await UpdateSourceDirectoryCleanupStateAsync(
                request.JobId,
                request.LeaseToken,
                MoveJobEntryCleanupState.Retained,
                cancellationToken);
        }

        await ReportProgressAsync(request, 90, "Source retained", cancellationToken);
    }

    private async Task DeleteMarkerlessSourceAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        bool targetInsideSource,
        IReadOnlyCollection<MoveJobEntry> manifest,
        MarkerlessSourceRetirementLease sourceRetirementLease,
        MarkerlessTargetVerificationLease targetVerificationLease,
        CancellationToken cancellationToken)
    {
        if (request.ForceCopyAndRetainSource)
        {
            throw new MoveNeedsAttentionException(
                "Forced source retention forbids destructive source cleanup.");
        }

        var files = manifest
            .Where(candidate => candidate.EntryType == MoveJobEntryType.File)
            .Where(IsPhysicalManifestEntry)
            .ToList();
        await VerifyMarkerlessTargetAsync(
            request,
            target,
            manifest,
            cancellationToken);

        var totalUnits = files.Sum(GetProgressUnits);
        var completedUnits = files
            .Where(entry => entry.CleanupState is
                MoveJobEntryCleanupState.Deleted or MoveJobEntryCleanupState.Retained)
            .Sum(GetProgressUnits);
        foreach (var entry in files)
        {
            var wasComplete = entry.CleanupState is
                MoveJobEntryCleanupState.Deleted or MoveJobEntryCleanupState.Retained;
            await DeleteMarkerlessSourceFileAsync(
                request,
                source,
                target,
                entry,
                sourceRetirementLease,
                targetVerificationLease,
                cancellationToken);
            if (!wasComplete && entry.CleanupState is
                MoveJobEntryCleanupState.Deleted or MoveJobEntryCleanupState.Retained)
            {
                completedUnits += GetProgressUnits(entry);
            }
            await ReportProgressAsync(
                request,
                CalculateWeightedProgress(75, 15, completedUnits, totalUnits),
                "Cleaning source",
                cancellationToken);
        }

        foreach (var entry in manifest
            .Where(candidate => candidate.EntryType == MoveJobEntryType.Directory)
            .Where(IsPhysicalManifestEntry)
            .OrderByDescending(candidate => candidate.RelativePath.Length))
        {
            await DeleteMarkerlessSourceDirectoryAsync(
                request,
                source,
                target,
                targetInsideSource,
                entry,
                sourceRetirementLease,
                cancellationToken);
        }

        await DeleteMarkerlessSourceRootAsync(
            request,
            source,
            target,
            targetInsideSource,
            sourceRetirementLease,
            cancellationToken);
        await ReportProgressAsync(request, 90, "Cleaning source", cancellationToken);
    }

    private async Task DeleteMarkerlessSourceFileAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        MoveJobEntry entry,
        MarkerlessSourceRetirementLease sourceRetirementLease,
        MarkerlessTargetVerificationLease targetVerificationLease,
        CancellationToken cancellationToken)
    {
        var sourcePath = ResolveManifestPath(
            source,
            entry,
            request.SourceSemantics,
            "source");
        var targetPath = ResolveManifestPath(
            target,
            entry,
            request.TargetSemantics,
            "target");
        if (!TryGetMarkerlessPathAttributes(sourcePath, out var sourceAttributes))
        {
            if (entry.CleanupState == MoveJobEntryCleanupState.DeleteAuthorized)
            {
                await UpdateCleanupStateAsync(
                    request.JobId,
                    request.LeaseToken,
                    entry.RelativePath,
                    MoveJobEntryCleanupState.Deleted,
                    cancellationToken);
                entry.CleanupState = MoveJobEntryCleanupState.Deleted;
                return;
            }
            if (entry.CleanupState == MoveJobEntryCleanupState.Deleted)
            {
                return;
            }
            if (await TryCompleteMarkerlessNativeRenameCleanupAsync(
                    request,
                    entry,
                    targetPath,
                    cancellationToken))
            {
                return;
            }
            throw new MoveNeedsAttentionException(
                $"A source file disappeared before markerless deletion was authorized: {entry.RelativePath}");
        }
        if ((sourceAttributes & FileAttributes.Directory) != 0
            || (sourceAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new MoveNeedsAttentionException(
                $"A source file changed type or became a link before markerless deletion: {entry.RelativePath}");
        }
        if (entry.CleanupState == MoveJobEntryCleanupState.Deleted)
        {
            throw new MoveNeedsAttentionException(
                $"A deleted source file path was recreated: {entry.RelativePath}");
        }
        if (entry.CleanupState == MoveJobEntryCleanupState.Retained)
        {
            throw new MoveNeedsAttentionException(
                $"A retained source file cannot be considered cleaned: {entry.RelativePath}");
        }

        if (!sourceRetirementLease.TryGet(entry.RelativePath, out var originalSource)
            || originalSource == null
            || !originalSource.VisiblePathMatches())
        {
            await RetainMarkerlessSourceEntryAsync(request, entry, cancellationToken);
            return;
        }

        var sourceParentPath = Path.GetDirectoryName(sourcePath)
            ?? throw new MoveNeedsAttentionException(
                "A markerless source file has no parent.");
        var targetParentPath = Path.GetDirectoryName(targetPath)
            ?? throw new MoveNeedsAttentionException(
                "A markerless target file has no parent.");
        using var sourceParent = OpenPinnedMoveDescendant(
            request,
            source,
            sourceParentPath,
            request.SourceSemantics,
            sourceEndpoint: true);
        using var sourceEntry = sourceParent.OpenExistingFile(
            Path.GetFileName(sourcePath),
            requireDeleteAccess: true);
        if (!originalSource.IdentifiesSameEntry(sourceEntry))
        {
            await RetainMarkerlessSourceEntryAsync(request, entry, cancellationToken);
            return;
        }
        ValidateMarkerlessSourceEntry(request, entry, sourceEntry);
        if (!await PinnedFileMatchesManifestAsync(
                sourceEntry,
                entry,
                cancellationToken))
        {
            throw new MoveNeedsAttentionException(
                $"A source file changed before markerless deletion: {entry.RelativePath}");
        }

        using var targetParent = OpenPinnedMoveDescendant(
            request,
            target,
            targetParentPath,
            request.TargetSemantics,
            sourceEndpoint: false);
        using var targetEntry = targetParent.OpenExistingFile(
            Path.GetFileName(targetPath),
            requireDeleteAccess: false);
        if (!targetVerificationLease.TryGet(entry.RelativePath, out var originalTarget)
            || originalTarget == null
            || !originalTarget.IdentifiesSameEntry(targetEntry)
            || !originalTarget.VisiblePathMatches())
        {
            throw new MoveNeedsAttentionException(
                $"The committed target publication changed before source deletion: {entry.RelativePath}");
        }
        ValidateMarkerlessTargetEntry(entry, targetEntry);
        if (!await PinnedFileMatchesManifestAsync(
                targetEntry,
                entry,
                cancellationToken))
        {
            throw new MoveNeedsAttentionException(
                $"The target file changed before source deletion: {entry.RelativePath}");
        }

        if (entry.CleanupState == MoveJobEntryCleanupState.Pending)
        {
            await UpdateCleanupStateAsync(
                request.JobId,
                request.LeaseToken,
                entry.RelativePath,
                MoveJobEntryCleanupState.DeleteAuthorized,
                cancellationToken);
            entry.CleanupState = MoveJobEntryCleanupState.DeleteAuthorized;
            faultInjector?.OnSourceCleanupMutation(
                request.JobId,
                SourceCleanupFaultPoint.AfterMarkerlessSourceDeleteAuthorizedState);
        }
        await EnsureMutationAuthorizedAsync(
            request,
            source,
            target,
            cancellationToken);
        ValidateMarkerlessSourceEntry(request, entry, sourceEntry);
        ValidateMarkerlessTargetEntry(entry, targetEntry);
        if (!await PinnedFileMatchesManifestAsync(
                sourceEntry,
                entry,
                cancellationToken))
        {
            throw new MoveNeedsAttentionException(
                $"The source file content changed after markerless deletion was authorized: {entry.RelativePath}");
        }
        if (!await PinnedFileMatchesManifestAsync(
                targetEntry,
                entry,
                cancellationToken))
        {
            throw new MoveNeedsAttentionException(
                $"The target file content changed after markerless deletion was authorized: {entry.RelativePath}");
        }
        if (!originalSource.VisiblePathMatches()
            || !originalTarget.VisiblePathMatches())
        {
            throw new MoveNeedsAttentionException(
                $"A live publication changed immediately before source deletion: {entry.RelativePath}");
        }
        sourceEntry.Delete();
        sourceRetirementLease.Release(entry.RelativePath);
        faultInjector?.OnSourceCleanupMutation(
            request.JobId,
            SourceCleanupFaultPoint
                .AfterMarkerlessSourceFileDeleteBeforeStateUpdate);
        await UpdateCleanupStateAsync(
            request.JobId,
            request.LeaseToken,
            entry.RelativePath,
            MoveJobEntryCleanupState.Deleted,
            cancellationToken);
        entry.CleanupState = MoveJobEntryCleanupState.Deleted;
        faultInjector?.OnSourceCleanupMutation(
            request.JobId,
            SourceCleanupFaultPoint.AfterMarkerlessSourceFileStateUpdate);
    }

}
