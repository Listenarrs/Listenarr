namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task RetainMarkerlessSourceAsync(
        AudiobookContentMoveRequest request,
        string target,
        IReadOnlyCollection<MoveJobEntry> manifest,
        CancellationToken cancellationToken)
    {
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
        CancellationToken cancellationToken)
    {
        if (request.ForceCopyAndRetainSource)
        {
            throw new MoveNeedsAttentionException(
                "Forced source retention forbids destructive source cleanup.");
        }

        var endpoints = await GetEndpointObjectIdentitiesAsync(
            request.JobId,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(endpoints.SourceDirectoryObjectIdentity)
            || string.IsNullOrWhiteSpace(endpoints.TargetDirectoryObjectIdentity))
        {
            throw new MoveNeedsAttentionException(
                "Markerless cleanup requires persisted source and target endpoint generations.");
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
                endpoints.SourceDirectoryObjectIdentity,
                endpoints.TargetDirectoryObjectIdentity,
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
                endpoints.SourceDirectoryObjectIdentity,
                cancellationToken);
        }

        await DeleteMarkerlessSourceRootAsync(
            request,
            source,
            target,
            targetInsideSource,
            cancellationToken);
        await ReportProgressAsync(request, 90, "Cleaning source", cancellationToken);
    }

    private async Task DeleteMarkerlessSourceFileAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        MoveJobEntry entry,
        string sourceEndpointIdentity,
        string targetEndpointIdentity,
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
            sourceEndpointIdentity,
            sourceEndpoint: true);
        using var sourceEntry = sourceParent.OpenExistingFile(
            Path.GetFileName(sourcePath),
            requireDeleteAccess: true);
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
            targetEndpointIdentity,
            sourceEndpoint: false);
        using var targetEntry = targetParent.OpenExistingFile(
            Path.GetFileName(targetPath),
            requireDeleteAccess: false);
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
        sourceEntry.Delete();
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

    private async Task RetainMarkerlessSourceEntryAsync(
        AudiobookContentMoveRequest request,
        MoveJobEntry entry,
        CancellationToken cancellationToken)
    {
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
    }

    private static bool TryGetMarkerlessPathAttributes(
        string path,
        out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (System.ComponentModel.Win32Exception exception) when (
            OperatingSystem.IsWindows()
                ? exception.NativeErrorCode is 2 or 3
                : exception.NativeErrorCode == 2)
        {
            attributes = default;
            return false;
        }
    }

    private static void ValidateMarkerlessSourceDirectory(
        MoveJobEntry entry,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory)
    {
        if (string.IsNullOrWhiteSpace(entry.SourcePhysicalObjectIdentity)
            || !directory.MatchesDirectoryObjectIdentity(
                entry.SourcePhysicalObjectIdentity)
            || !PinnedDirectoryVisibleOrThrowUnavailable(
                directory,
                $"A markerless source directory is temporarily unavailable: {entry.RelativePath}"))
        {
            throw new MoveNeedsAttentionException(
                $"A markerless source directory changed physical generation: {entry.RelativePath}");
        }
    }
}
