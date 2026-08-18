namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task DeleteMarkerlessSourceDirectoryAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        bool targetInsideSource,
        MoveJobEntry entry,
        string sourceEndpointIdentity,
        CancellationToken cancellationToken)
    {
        var sourcePath = ResolveManifestPath(
            source,
            entry,
            request.SourceSemantics,
            "source");
        var sourceExists = TryGetMarkerlessPathAttributes(
            sourcePath,
            out var sourceAttributes);
        if (sourceExists
            && ((sourceAttributes & FileAttributes.Directory) == 0
                || (sourceAttributes & FileAttributes.ReparsePoint) != 0))
        {
            throw new MoveNeedsAttentionException(
                $"A source directory changed type or became a link: {entry.RelativePath}");
        }

        var ownership = await ResolveMarkerlessSourceDirectoryOwnershipAsync(
            sourcePath,
            request.SourceSemantics,
            cancellationToken);
        if (!sourceExists)
        {
            if (entry.CleanupState is
                MoveJobEntryCleanupState.DeleteAuthorized
                    or MoveJobEntryCleanupState.Deleted)
            {
                if (ownership != null)
                {
                    if (ownership.State != LibraryDirectoryOwnershipState.Removing)
                    {
                        throw new MoveNeedsAttentionException(
                            $"A durably owned source directory disappeared before its ownership removal was authorized: {entry.RelativePath}");
                    }

                    _ = await RemoveMarkerlessOwnedDirectoryAsync(
                        request,
                        source,
                        target,
                        ownership,
                        cancellationToken);
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
            await RetainMarkerlessOwnedDirectoryIfRemovingAsync(
                ownership,
                "The source directory overlaps the retained markerless target.",
                cancellationToken);
            await RetainMarkerlessSourceEntryAsync(request, entry, cancellationToken);
            return;
        }
        if (Directory.EnumerateFileSystemEntries(sourcePath).Any())
        {
            await RetainMarkerlessOwnedDirectoryIfRemovingAsync(
                ownership,
                "The source directory gained content before markerless deletion.",
                cancellationToken);
            await RetainMarkerlessSourceEntryAsync(request, entry, cancellationToken);
            return;
        }

        var parentPath = Path.GetDirectoryName(sourcePath)
            ?? throw new MoveNeedsAttentionException(
                "A markerless source directory has no parent.");
        using (var parent = OpenPinnedMoveDescendant(
            request,
            source,
            parentPath,
            request.SourceSemantics,
            sourceEndpointIdentity,
            sourceEndpoint: true))
        using (var publication = parent.OpenExistingChildForPublication(
            Path.GetFileName(sourcePath)))
        using (var directory = publication.OpenCreatedDirectoryAnchor())
        {
            ValidateMarkerlessSourceDirectory(entry, directory);
            if (entry.CleanupState == MoveJobEntryCleanupState.Pending)
            {
                await UpdateCleanupStateAsync(
                    request.JobId,
                    request.LeaseToken,
                    entry.RelativePath,
                    MoveJobEntryCleanupState.DeleteAuthorized,
                    cancellationToken);
                entry.CleanupState = MoveJobEntryCleanupState.DeleteAuthorized;
            }

            if (ownership == null)
            {
                await EnsureMutationAuthorizedAsync(
                    request,
                    source,
                    target,
                    cancellationToken);
                ValidateMarkerlessSourceDirectory(entry, directory);
                if (Directory.EnumerateFileSystemEntries(sourcePath).Any())
                {
                    await RetainMarkerlessSourceEntryAsync(
                        request,
                        entry,
                        cancellationToken);
                    return;
                }

                publication.DeletePinnedEmptyDirectoryImmediately(
                    Path.GetFileName(sourcePath));
            }
        }

        if (ownership != null)
        {
            var removed = await RemoveMarkerlessOwnedDirectoryAsync(
                request,
                source,
                target,
                ownership,
                cancellationToken);
            if (!removed)
            {
                await RetainMarkerlessSourceEntryAsync(
                    request,
                    entry,
                    cancellationToken);
                return;
            }
        }

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
        var ownership = await ResolveMarkerlessSourceDirectoryOwnershipAsync(
            source,
            request.SourceSemantics,
            cancellationToken);
        if (!request.DeleteEmptySource
            || targetInsideSource
            || IsSourceCleanupBoundary(
                source,
                request.SourceCleanupBoundary,
                request.SourceSemantics))
        {
            await RetainMarkerlessOwnedDirectoryIfRemovingAsync(
                ownership,
                "The source root is retained by the markerless move cleanup policy.",
                cancellationToken);
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

        var sourceExists = TryGetMarkerlessPathAttributes(
            source,
            out var sourceAttributes);
        if (sourceExists
            && ((sourceAttributes & FileAttributes.Directory) == 0
                || (sourceAttributes & FileAttributes.ReparsePoint) != 0))
        {
            throw new MoveNeedsAttentionException(
                "The markerless source root changed type or became a link before deletion.");
        }
        if (!sourceExists)
        {
            if (endpoints.SourceDirectoryCleanupState is
                MoveJobEntryCleanupState.DeleteAuthorized
                    or MoveJobEntryCleanupState.Deleted)
            {
                if (ownership != null)
                {
                    if (ownership.State != LibraryDirectoryOwnershipState.Removing)
                    {
                        throw new MoveNeedsAttentionException(
                            "A durably owned source root disappeared before its ownership removal was authorized.");
                    }

                    _ = await RemoveMarkerlessOwnedDirectoryAsync(
                        request,
                        source,
                        target,
                        ownership,
                        cancellationToken);
                }

                if (endpoints.SourceDirectoryCleanupState
                    != MoveJobEntryCleanupState.Deleted)
                {
                    await UpdateSourceDirectoryCleanupStateAsync(
                        request.JobId,
                        request.LeaseToken,
                        MoveJobEntryCleanupState.Deleted,
                        cancellationToken);
                }
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
            await RetainMarkerlessOwnedDirectoryIfRemovingAsync(
                ownership,
                "The source root gained content before markerless deletion.",
                cancellationToken);
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
        using (var parent = OpenPinnedMoveBoundaryDescendant(
            request,
            parentPath,
            request.SourceSemantics,
            sourceBoundary: true))
        using (var publication = parent.OpenExistingChildForPublication(
            Path.GetFileName(source)))
        using (var directory = publication.OpenCreatedDirectoryAnchor())
        {
            if (string.IsNullOrWhiteSpace(endpoints.SourceDirectoryObjectIdentity)
                || !directory.MatchesDirectoryObjectIdentity(
                    endpoints.SourceDirectoryObjectIdentity)
                || !PinnedDirectoryVisibleOrThrowUnavailable(
                    directory,
                    "The markerless source root is temporarily unavailable before deletion."))
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
                    MoveJobEntryCleanupState.DeleteAuthorized,
                    cancellationToken);
            }

            if (ownership == null)
            {
                await EnsureMutationAuthorizedAsync(
                    request,
                    source,
                    target,
                    cancellationToken);
                if (Directory.EnumerateFileSystemEntries(source).Any())
                {
                    await UpdateSourceDirectoryCleanupStateAsync(
                        request.JobId,
                        request.LeaseToken,
                        MoveJobEntryCleanupState.Retained,
                        cancellationToken);
                    return;
                }
                if (!PinnedDirectoryVisibleOrThrowUnavailable(
                        directory,
                        "The markerless source root is temporarily unavailable immediately before deletion."))
                {
                    await UpdateSourceDirectoryCleanupStateAsync(
                        request.JobId,
                        request.LeaseToken,
                        MoveJobEntryCleanupState.Retained,
                        cancellationToken);
                    return;
                }

                publication.DeletePinnedEmptyDirectoryImmediately(
                    Path.GetFileName(source));
            }
        }

        if (ownership != null)
        {
            var removed = await RemoveMarkerlessOwnedDirectoryAsync(
                request,
                source,
                target,
                ownership,
                cancellationToken);
            if (!removed)
            {
                await UpdateSourceDirectoryCleanupStateAsync(
                    request.JobId,
                    request.LeaseToken,
                    MoveJobEntryCleanupState.Retained,
                    cancellationToken);
                return;
            }
        }

        await UpdateSourceDirectoryCleanupStateAsync(
            request.JobId,
            request.LeaseToken,
            MoveJobEntryCleanupState.Deleted,
            cancellationToken);
    }
}
