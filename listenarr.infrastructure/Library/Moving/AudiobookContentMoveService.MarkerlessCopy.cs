namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task CopyMarkerlessTargetFilesAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        IReadOnlyCollection<MoveJobEntry> manifest,
        bool retainSource,
        MarkerlessTargetVerificationLease targetVerificationLease,
        CancellationToken cancellationToken)
    {
        var endpoints = await GetEndpointObjectIdentitiesAsync(
            request.JobId,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(endpoints.SourceDirectoryObjectIdentity)
            || string.IsNullOrWhiteSpace(endpoints.TargetDirectoryObjectIdentity))
        {
            throw new MoveNeedsAttentionException(
                "Markerless copy requires persisted source and target endpoint generations.");
        }

        var files = manifest
            .Where(candidate => candidate.EntryType == MoveJobEntryType.File)
            .Where(IsPhysicalManifestEntry)
            .ToList();
        var totalWorkUnits = files.Sum(entry => checked(GetProgressUnits(entry) * 2));
        var completedWorkUnits = files
            .Where(entry => entry.CopyState == MoveJobEntryCopyState.Verified)
            .Sum(entry => checked(GetProgressUnits(entry) * 2));

        foreach (var entry in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                endpoints.SourceDirectoryObjectIdentity,
                sourceEndpoint: true);
            using var targetParent = OpenPinnedMoveDescendant(
                request,
                target,
                targetParentPath,
                request.TargetSemantics,
                endpoints.TargetDirectoryObjectIdentity,
                sourceEndpoint: false);
            using var existingTarget = targetParent.TryOpenExistingFile(
                Path.GetFileName(targetPath),
                requireDeleteAccess: false);
            using var sourceEntry = sourceParent.TryOpenExistingFile(
                Path.GetFileName(sourcePath),
                requireDeleteAccess: false);

            if (sourceEntry == null)
            {
                if (retainSource)
                {
                    throw new MoveNeedsAttentionException(
                        $"A source file disappeared during copy-and-retain publication: {entry.RelativePath}");
                }
                var wasVerified = entry.CopyState == MoveJobEntryCopyState.Verified;
                if (existingTarget == null
                    || !await TryRecoverMarkerlessNativeRenameAsync(
                        request,
                        entry,
                        existingTarget,
                        cancellationToken))
                {
                    throw new MoveNeedsAttentionException(
                        $"Source file disappeared before markerless publication completed: {entry.RelativePath}");
                }

                if (!wasVerified)
                {
                    completedWorkUnits += checked(GetProgressUnits(entry) * 2);
                }
                await ReportProgressAsync(
                    request,
                    CalculateWeightedProgress(
                        5,
                        65,
                        completedWorkUnits,
                        totalWorkUnits),
                    "Moving",
                    cancellationToken);
                continue;
            }

            ValidateMarkerlessSourceEntry(request, entry, sourceEntry);
            if (entry.CopyState == MoveJobEntryCopyState.Verified
                && existingTarget != null)
            {
                await HandleExistingMarkerlessTargetAsync(
                    request,
                    entry,
                    sourceEntry,
                    existingTarget,
                    completedWorkUnits,
                    totalWorkUnits,
                    cancellationToken);
                continue;
            }

            PinnedDirectoryCreation.PinnedFileEntry? stableRenameEntry = null;
            if (!retainSource
                && existingTarget == null
                && entry.CopyState == MoveJobEntryCopyState.Pending
                && string.IsNullOrWhiteSpace(entry.TargetPhysicalObjectIdentity))
            {
                stableRenameEntry = TryOpenMarkerlessStableNativeRenameSource(
                    entry,
                    sourceParent,
                    sourceEntry,
                    targetParent);
            }

            try
            {
                var observedProof = await ComputeMarkerlessSourceProofHashAsync(
                    request,
                    entry,
                    sourcePath,
                    sourceParent,
                    sourceEntry,
                    completedWorkUnits,
                    totalWorkUnits,
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(entry.Sha256)
                    && !string.Equals(
                        entry.Sha256,
                        observedProof.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new MoveNeedsAttentionException(
                        $"Source file changed before markerless publication: {entry.RelativePath}");
                }

                await UpdateSourceEntryProofAsync(
                    request.JobId,
                    request.LeaseToken,
                    entry.RelativePath,
                    entry.SourcePhysicalObjectIdentity
                        ?? sourceEntry.GetObjectIdentity(),
                    observedProof.Sha256,
                    observedProof.LastWriteTimeUtc,
                    cancellationToken);
                entry.Sha256 ??= observedProof.Sha256;
                entry.LastWriteTimeUtc = observedProof.LastWriteTimeUtc;
                completedWorkUnits += GetProgressUnits(entry);
                await ReportProgressAsync(
                    request,
                    CalculateWeightedProgress(
                        5,
                        65,
                        completedWorkUnits,
                        totalWorkUnits),
                    "Verifying source",
                    cancellationToken);

                if (stableRenameEntry != null)
                {
                    var nativeRename = await TryPublishMarkerlessNativeRenameAsync(
                        request,
                        source,
                        target,
                        entry,
                        sourceParent,
                        sourceEntry,
                        targetParent,
                        stableRenameEntry,
                        cancellationToken);
                    if (nativeRename.Published)
                    {
                        if (nativeRename.VerificationLease != null)
                        {
                            targetVerificationLease.Add(
                                entry.RelativePath,
                                nativeRename.VerificationLease);
                        }
                        completedWorkUnits += GetProgressUnits(entry);
                        await ReportProgressAsync(
                            request,
                            CalculateWeightedProgress(
                                5,
                                65,
                                completedWorkUnits,
                                totalWorkUnits),
                            "Moving",
                            cancellationToken);
                        continue;
                    }
                }
            }
            finally
            {
                stableRenameEntry?.Dispose();
            }

            if (existingTarget != null)
            {
                var wasVerified = entry.CopyState == MoveJobEntryCopyState.Verified;
                await HandleExistingMarkerlessTargetAsync(
                    request,
                    entry,
                    sourceEntry,
                    existingTarget,
                    completedWorkUnits,
                    totalWorkUnits,
                    cancellationToken);
                if (!wasVerified && entry.CopyState == MoveJobEntryCopyState.Verified)
                {
                    completedWorkUnits += GetProgressUnits(entry);
                }
                await ReportProgressAsync(
                    request,
                    CalculateWeightedProgress(
                        5,
                        65,
                        completedWorkUnits,
                        totalWorkUnits),
                    "Copying",
                    cancellationToken);
                continue;
            }

            if (entry.CopyState != MoveJobEntryCopyState.Pending
                || !string.IsNullOrWhiteSpace(entry.TargetPhysicalObjectIdentity))
            {
                throw new MoveNeedsAttentionException(
                    $"A markerless target file disappeared after publication began: {entry.RelativePath}");
            }

            await EnsureMutationAuthorizedAsync(
                request,
                source,
                target,
                cancellationToken);
            // Native rename may have just been observed as unsupported. Re-prove the
            // visible source generation immediately before beginning copy publication;
            // the pinned read handle alone is not authority to publish a source whose
            // namespace entry was replaced after the rename observation.
            ValidateMarkerlessSourceEntry(request, entry, sourceEntry);
            using var created = targetParent.CreateNewFile(
                Path.GetFileName(targetPath));
            var targetIdentity = created.GetObjectIdentity();
            try
            {
                // The final-name entry must be namespace-durable before its physical
                // generation is committed to SQLite. This is especially important
                // for copy fallback on remote filesystems where file-data fsync alone
                // does not prove the parent directory entry survived a crash.
                targetParent.FlushDirectoryEntry();
            }
            catch
            {
                TryRetireUncommittedMarkerlessFile(created);
                throw;
            }
            faultInjector?.OnCopyMutation(
                request.JobId,
                CopyMutationFaultPoint
                    .AfterMarkerlessFileCreationBeforeStateUpdate);
            try
            {
                await UpdateTargetEntryStateAsync(
                    request.JobId,
                    request.LeaseToken,
                    entry.RelativePath,
                    MoveJobEntryCopyState.Staged,
                    targetIdentity,
                    cancellationToken);
            }
            catch
            {
                TryRetireUncommittedMarkerlessFile(created);
                throw;
            }

            entry.CopyState = MoveJobEntryCopyState.Staged;
            entry.TargetPhysicalObjectIdentity = targetIdentity;
            faultInjector?.OnCopyMutation(
                request.JobId,
                CopyMutationFaultPoint.AfterMarkerlessFileStateUpdate);
            await WriteMarkerlessTargetAsync(
                request,
                entry,
                sourceEntry,
                created,
                completedWorkUnits,
                totalWorkUnits,
                cancellationToken);
            completedWorkUnits += GetProgressUnits(entry);
            await ReportProgressAsync(
                request,
                CalculateWeightedProgress(
                    5,
                    65,
                    completedWorkUnits,
                    totalWorkUnits),
                "Copying",
                cancellationToken);
        }
    }

}
