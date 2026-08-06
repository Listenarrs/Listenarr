namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task DeleteMarkerlessSourceAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        bool targetInsideSource,
        IReadOnlyCollection<MoveJobEntry> manifest,
        CancellationToken cancellationToken)
    {
        var files = manifest
            .Where(candidate => candidate.EntryType == MoveJobEntryType.File)
            .Where(IsPhysicalManifestEntry)
            .ToList();
        var resumingCleanup = manifest
            .Where(IsPhysicalManifestEntry)
            .Any(entry => entry.CleanupState != MoveJobEntryCleanupState.Pending);
        if (resumingCleanup)
        {
            await VerifyMarkerlessTargetAsync(
                request,
                target,
                manifest,
                cancellationToken);
        }

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
        if (!File.Exists(sourcePath))
        {
            if (entry.CleanupState == MoveJobEntryCleanupState.DeletionAuthorized)
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
        using var sourceParent = PinnedDirectoryCreation.OpenPinnedBoundary(
            sourceParentPath);
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

        using var targetParent = PinnedDirectoryCreation.OpenPinnedBoundary(
            targetParentPath);
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
                MoveJobEntryCleanupState.DeletionAuthorized,
                cancellationToken);
            entry.CleanupState = MoveJobEntryCleanupState.DeletionAuthorized;
        }
        await EnsureMutationAuthorizedAsync(
            request,
            source,
            target,
            cancellationToken);
        ValidateMarkerlessSourceEntry(request, entry, sourceEntry);
        ValidateMarkerlessTargetEntry(entry, targetEntry);
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

    private async Task DeleteMarkerlessSourceDirectoryAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        bool targetInsideSource,
        MoveJobEntry entry,
        CancellationToken cancellationToken)
    {
        var sourcePath = ResolveManifestPath(
            source,
            entry,
            request.SourceSemantics,
            "source");
        if (File.Exists(sourcePath))
        {
            throw new MoveNeedsAttentionException(
                $"A source directory changed into a file: {entry.RelativePath}");
        }
        if (!Directory.Exists(sourcePath))
        {
            if (entry.CleanupState == MoveJobEntryCleanupState.DeletionAuthorized)
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
            throw new MoveNeedsAttentionException(
                $"A source directory disappeared before markerless deletion was authorized: {entry.RelativePath}");
        }
        if (entry.CleanupState == MoveJobEntryCleanupState.Deleted)
        {
            throw new MoveNeedsAttentionException(
                $"A deleted source directory path was recreated: {entry.RelativePath}");
        }

        if (targetInsideSource
            && (IsSameOrInside(target, sourcePath, request.SourceSemantics)
                || IsSameOrInside(sourcePath, target, request.SourceSemantics)))
        {
            await RetainMarkerlessSourceEntryAsync(request, entry, cancellationToken);
            return;
        }
        if (Directory.EnumerateFileSystemEntries(sourcePath).Any())
        {
            await RetainMarkerlessSourceEntryAsync(request, entry, cancellationToken);
            return;
        }

        var parentPath = Path.GetDirectoryName(sourcePath)
            ?? throw new MoveNeedsAttentionException(
                "A markerless source directory has no parent.");
        using var parent = PinnedDirectoryCreation.OpenPinnedBoundary(parentPath);
        using var publication = parent.OpenExistingChildForPublication(
            Path.GetFileName(sourcePath));
        using var directory = publication.OpenCreatedDirectoryAnchor();
        ValidateMarkerlessSourceDirectory(entry, directory);
        if (entry.CleanupState == MoveJobEntryCleanupState.Pending)
        {
            await UpdateCleanupStateAsync(
                request.JobId,
                request.LeaseToken,
                entry.RelativePath,
                MoveJobEntryCleanupState.DeletionAuthorized,
                cancellationToken);
            entry.CleanupState = MoveJobEntryCleanupState.DeletionAuthorized;
        }
        await EnsureMutationAuthorizedAsync(
            request,
            source,
            target,
            cancellationToken);
        ValidateMarkerlessSourceDirectory(entry, directory);
        if (Directory.EnumerateFileSystemEntries(sourcePath).Any())
        {
            await RetainMarkerlessSourceEntryAsync(request, entry, cancellationToken);
            return;
        }
        publication.RetirePinnedEmptyDirectoryFromNamespace(
            Path.GetFileName(sourcePath));
        await UpdateCleanupStateAsync(
            request.JobId,
            request.LeaseToken,
            entry.RelativePath,
            MoveJobEntryCleanupState.Deleted,
            cancellationToken);
        entry.CleanupState = MoveJobEntryCleanupState.Deleted;
    }

    private async Task DeleteMarkerlessSourceRootAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        bool targetInsideSource,
        CancellationToken cancellationToken)
    {
        var endpoints = await GetEndpointObjectIdentitiesAsync(
            request.JobId,
            cancellationToken);
        if (!request.DeleteEmptySource
            || targetInsideSource
            || IsSourceCleanupBoundary(
                source,
                request.SourceCleanupBoundary,
                request.SourceSemantics))
        {
            if (endpoints.SourceDirectoryCleanupState
                == MoveJobEntryCleanupState.Pending)
            {
                await UpdateSourceDirectoryCleanupStateAsync(
                    request.JobId,
                    request.LeaseToken,
                    MoveJobEntryCleanupState.Retained,
                    cancellationToken);
            }
            return;
        }

        if (!Directory.Exists(source))
        {
            if (endpoints.SourceDirectoryCleanupState
                == MoveJobEntryCleanupState.DeletionAuthorized)
            {
                await UpdateSourceDirectoryCleanupStateAsync(
                    request.JobId,
                    request.LeaseToken,
                    MoveJobEntryCleanupState.Deleted,
                    cancellationToken);
                return;
            }
            if (endpoints.SourceDirectoryCleanupState
                == MoveJobEntryCleanupState.Deleted)
            {
                return;
            }
            throw new MoveNeedsAttentionException(
                "The source directory disappeared before markerless deletion was authorized.");
        }
        if (endpoints.SourceDirectoryCleanupState
            == MoveJobEntryCleanupState.Deleted)
        {
            throw new MoveNeedsAttentionException(
                "The deleted source directory path was recreated.");
        }
        if (Directory.EnumerateFileSystemEntries(source).Any())
        {
            await UpdateSourceDirectoryCleanupStateAsync(
                request.JobId,
                request.LeaseToken,
                MoveJobEntryCleanupState.Retained,
                cancellationToken);
            return;
        }

        var parentPath = Path.GetDirectoryName(source)
            ?? throw new MoveNeedsAttentionException(
                "The markerless source directory has no parent.");
        using var parent = PinnedDirectoryCreation.OpenPinnedBoundary(parentPath);
        using var publication = parent.OpenExistingChildForPublication(
            Path.GetFileName(source));
        using var directory = publication.OpenCreatedDirectoryAnchor();
        if (string.IsNullOrWhiteSpace(endpoints.SourceDirectoryObjectIdentity)
            || !string.Equals(
                endpoints.SourceDirectoryObjectIdentity,
                directory.GetDirectoryObjectIdentity(),
                StringComparison.Ordinal)
            || !directory.VisiblePathMatches())
        {
            throw new MoveNeedsAttentionException(
                "The markerless source root changed physical generation before deletion.");
        }
        if (endpoints.SourceDirectoryCleanupState
            == MoveJobEntryCleanupState.Pending)
        {
            await UpdateSourceDirectoryCleanupStateAsync(
                request.JobId,
                request.LeaseToken,
                MoveJobEntryCleanupState.DeletionAuthorized,
                cancellationToken);
        }
        await EnsureMutationAuthorizedAsync(
            request,
            source,
            target,
            cancellationToken);
        if (Directory.EnumerateFileSystemEntries(source).Any()
            || !directory.VisiblePathMatches())
        {
            await UpdateSourceDirectoryCleanupStateAsync(
                request.JobId,
                request.LeaseToken,
                MoveJobEntryCleanupState.Retained,
                cancellationToken);
            return;
        }
        publication.RetirePinnedEmptyDirectoryFromNamespace(Path.GetFileName(source));
        await UpdateSourceDirectoryCleanupStateAsync(
            request.JobId,
            request.LeaseToken,
            MoveJobEntryCleanupState.Deleted,
            cancellationToken);
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

    private static void ValidateMarkerlessSourceDirectory(
        MoveJobEntry entry,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory)
    {
        if (string.IsNullOrWhiteSpace(entry.SourcePhysicalObjectIdentity)
            || !string.Equals(
                entry.SourcePhysicalObjectIdentity,
                directory.GetDirectoryObjectIdentity(),
                StringComparison.Ordinal)
            || !directory.VisiblePathMatches())
        {
            throw new MoveNeedsAttentionException(
                $"A markerless source directory changed physical generation: {entry.RelativePath}");
        }
    }
}
