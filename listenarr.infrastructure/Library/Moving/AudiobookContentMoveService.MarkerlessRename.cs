namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task RecoverInterruptedMarkerlessNativeRenamesAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        IReadOnlyCollection<MoveJobEntry> manifest,
        CancellationToken cancellationToken)
    {
        var endpoints = await GetEndpointObjectIdentitiesAsync(
            request.JobId,
            cancellationToken);

        foreach (var entry in manifest
            .Where(candidate => candidate.EntryType == MoveJobEntryType.File)
            .Where(IsPhysicalManifestEntry))
        {
            var sourcePath = ResolveManifestPath(
                source,
                entry,
                request.SourceSemantics,
                "source");
            if (File.Exists(sourcePath))
            {
                continue;
            }

            var targetPath = ResolveManifestPath(
                target,
                entry,
                request.TargetSemantics,
                "target");
            var targetParentPath = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrWhiteSpace(targetParentPath)
                || !Directory.Exists(targetParentPath))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(endpoints.TargetDirectoryObjectIdentity))
            {
                throw new MoveNeedsAttentionException(
                    "Interrupted native-rename recovery requires a persisted target endpoint generation.");
            }
            using var targetParent = OpenPinnedMoveDescendant(
                request,
                target,
                targetParentPath,
                request.TargetSemantics,
                endpoints.TargetDirectoryObjectIdentity,
                sourceEndpoint: false);
            using var targetEntry = targetParent.TryOpenExistingFile(
                Path.GetFileName(targetPath),
                requireDeleteAccess: false);
            if (targetEntry != null)
            {
                _ = await TryRecoverMarkerlessNativeRenameAsync(
                    request,
                    entry,
                    targetEntry,
                    cancellationToken);
            }
        }
    }

    private PinnedDirectoryCreation.PinnedFileEntry? TryOpenMarkerlessStableNativeRenameSource(
        MoveJobEntry entry,
        PinnedDirectoryCreation.PinnedDirectoryAnchor sourceParent,
        PinnedDirectoryCreation.PinnedFileEntry sourceEntry,
        PinnedDirectoryCreation.PinnedDirectoryAnchor targetParent)
    {
        if (!OperatingSystem.IsWindows()
            || (faultInjector != null && !faultInjector.AllowMarkerlessFileRename)
            || !sourceEntry.IsOnSameVolume(targetParent)
            || sourceParent.TryOpenExistingFileForStableDeleteWithOutcome(
                Path.GetFileName(sourceEntry.FullPath),
                out var stableEntry) != PinnedFileOpenOutcome.Opened
            || stableEntry == null)
        {
            return null;
        }

        if (!stableEntry.IdentifiesSameEntry(sourceEntry)
            || !stableEntry.VisiblePathMatches()
            || !stableEntry.MatchesMetadata(entry.Length, entry.LastWriteTimeUtc)
            || string.IsNullOrWhiteSpace(entry.SourcePhysicalObjectIdentity)
            || !stableEntry.MatchesObjectIdentity(
                entry.SourcePhysicalObjectIdentity))
        {
            stableEntry.Dispose();
            return null;
        }

        return stableEntry;
    }

    private async Task<(bool Published, PinnedDirectoryCreation.PinnedFileEntry? VerificationLease)>
        TryPublishMarkerlessNativeRenameAsync(
            AudiobookContentMoveRequest request,
            string source,
            string target,
            MoveJobEntry entry,
            PinnedDirectoryCreation.PinnedDirectoryAnchor sourceParent,
            PinnedDirectoryCreation.PinnedFileEntry sourceEntry,
            PinnedDirectoryCreation.PinnedDirectoryAnchor targetParent,
            PinnedDirectoryCreation.PinnedFileEntry? stableRenameEntry,
            CancellationToken cancellationToken)
    {
        if ((faultInjector != null && !faultInjector.AllowMarkerlessFileRename)
            || !sourceEntry.IsOnSameVolume(targetParent))
        {
            return (false, null);
        }

        PinnedDirectoryCreation.PinnedFileEntry? ownedRenameEntry = null;
        PinnedDirectoryCreation.PinnedFileEntry? verificationLease = null;
        var renameEntry = stableRenameEntry;
        if (renameEntry == null)
        {
            ownedRenameEntry = sourceParent.TryOpenExistingFile(
                Path.GetFileName(sourceEntry.FullPath),
                requireDeleteAccess: true);
            renameEntry = ownedRenameEntry;
        }

        try
        {
            if (renameEntry == null
                || !renameEntry.IdentifiesSameEntry(sourceEntry)
                || !renameEntry.VisiblePathMatches()
                || string.IsNullOrWhiteSpace(entry.SourcePhysicalObjectIdentity)
                || !renameEntry.MatchesObjectIdentity(
                    entry.SourcePhysicalObjectIdentity))
            {
                return (false, null);
            }

            await EnsureMutationAuthorizedAsync(
                request,
                source,
                target,
                cancellationToken);
            renameEntry.MoveTo(
                targetParent,
                Path.GetFileName(ResolveManifestPath(
                    target,
                    entry,
                    request.TargetSemantics,
                    "target")));
            sourceParent.FlushDirectoryEntry();
            if (!string.Equals(
                    sourceParent.FullPath,
                    targetParent.FullPath,
                    StringComparison.Ordinal))
            {
                targetParent.FlushDirectoryEntry();
            }

            faultInjector?.OnCopyMutation(
                request.JobId,
                CopyMutationFaultPoint.AfterMarkerlessNativeRenameBeforeStateUpdate);
            if (!renameEntry.VisiblePathMatches()
                || !renameEntry.MatchesObjectIdentity(
                    entry.SourcePhysicalObjectIdentity!))
            {
                throw new MoveNeedsAttentionException(
                    $"The markerless native rename target changed physical generation: {entry.RelativePath}");
            }

            if (stableRenameEntry != null)
            {
                verificationLease = targetParent.OpenExistingFileForVerificationLease(
                    Path.GetFileName(renameEntry.FullPath));
                if (!verificationLease.IdentifiesSameEntry(renameEntry)
                    || !verificationLease.VisiblePathMatches())
                {
                    throw new MoveNeedsAttentionException(
                        $"The markerless native rename verification lease did not capture the published generation: {entry.RelativePath}");
                }
            }

            var durableTargetIdentity = entry.SourcePhysicalObjectIdentity!;
            await UpdateTargetEntryStateAsync(
                request.JobId,
                request.LeaseToken,
                entry.RelativePath,
                MoveJobEntryCopyState.Verified,
                durableTargetIdentity,
                cancellationToken);
            entry.CopyState = MoveJobEntryCopyState.Verified;
            entry.TargetPhysicalObjectIdentity = durableTargetIdentity;
            var result = (Published: true, VerificationLease: verificationLease);
            verificationLease = null;
            return result;
        }
        finally
        {
            verificationLease?.Dispose();
            ownedRenameEntry?.Dispose();
        }
    }

    private async Task<bool> TryRecoverMarkerlessNativeRenameAsync(
        AudiobookContentMoveRequest request,
        MoveJobEntry entry,
        PinnedDirectoryCreation.PinnedFileEntry targetEntry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entry.SourcePhysicalObjectIdentity)
            || !targetEntry.VisiblePathMatches()
            || !targetEntry.MatchesObjectIdentity(
                entry.SourcePhysicalObjectIdentity)
            || entry.CopyState is not (
                MoveJobEntryCopyState.Pending or MoveJobEntryCopyState.Verified)
            || (entry.CopyState == MoveJobEntryCopyState.Pending
                && !string.IsNullOrWhiteSpace(
                    entry.TargetPhysicalObjectIdentity))
            || (entry.CopyState == MoveJobEntryCopyState.Verified
                && (string.IsNullOrWhiteSpace(entry.TargetPhysicalObjectIdentity)
                    || !targetEntry.MatchesObjectIdentity(
                        entry.TargetPhysicalObjectIdentity)))
            || !targetEntry.MatchesMetadata(
                entry.Length,
                entry.LastWriteTimeUtc))
        {
            return false;
        }

        if (entry.CopyState == MoveJobEntryCopyState.Pending)
        {
            await UpdateTargetEntryStateAsync(
                request.JobId,
                request.LeaseToken,
                entry.RelativePath,
                MoveJobEntryCopyState.Verified,
                entry.SourcePhysicalObjectIdentity,
                cancellationToken);
            entry.CopyState = MoveJobEntryCopyState.Verified;
            entry.TargetPhysicalObjectIdentity =
                entry.SourcePhysicalObjectIdentity;
        }

        return true;
    }

    private async Task<bool> TryCompleteMarkerlessNativeRenameCleanupAsync(
        AudiobookContentMoveRequest request,
        MoveJobEntry entry,
        string targetPath,
        CancellationToken cancellationToken)
    {
        if (entry.CopyState != MoveJobEntryCopyState.Verified
            || string.IsNullOrWhiteSpace(entry.SourcePhysicalObjectIdentity)
            || string.IsNullOrWhiteSpace(entry.TargetPhysicalObjectIdentity))
        {
            return false;
        }

        var targetParentPath = Path.GetDirectoryName(targetPath)
            ?? throw new MoveNeedsAttentionException(
                "A markerless native-rename target has no parent.");
        var endpoints = await GetEndpointObjectIdentitiesAsync(
            request.JobId,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(endpoints.TargetDirectoryObjectIdentity))
        {
            return false;
        }
        using var targetParent = OpenPinnedMoveDescendant(
            request,
            request.Target,
            targetParentPath,
            request.TargetSemantics,
            endpoints.TargetDirectoryObjectIdentity,
            sourceEndpoint: false);
        using var targetEntry = targetParent.TryOpenExistingFile(
            Path.GetFileName(targetPath),
            requireDeleteAccess: false);
        if (targetEntry == null
            || !TargetMatchesMarkerlessRenameEntry(entry, targetEntry)
            || !targetEntry.MatchesMetadata(
                entry.Length,
                entry.LastWriteTimeUtc))
        {
            return false;
        }

        await UpdateCleanupStateAsync(
            request.JobId,
            request.LeaseToken,
            entry.RelativePath,
            MoveJobEntryCleanupState.DeleteAuthorized,
            cancellationToken);
        entry.CleanupState = MoveJobEntryCleanupState.DeleteAuthorized;
        await UpdateCleanupStateAsync(
            request.JobId,
            request.LeaseToken,
            entry.RelativePath,
            MoveJobEntryCleanupState.Deleted,
            cancellationToken);
        entry.CleanupState = MoveJobEntryCleanupState.Deleted;
        return true;
    }

    private static bool TargetMatchesMarkerlessRenameEntry(
        MoveJobEntry entry,
        PinnedDirectoryCreation.PinnedFileEntry targetEntry) =>
        targetEntry.VisiblePathMatches()
        && !string.IsNullOrWhiteSpace(entry.SourcePhysicalObjectIdentity)
        && !string.IsNullOrWhiteSpace(entry.TargetPhysicalObjectIdentity)
        && targetEntry.MatchesObjectIdentity(entry.SourcePhysicalObjectIdentity)
        && targetEntry.MatchesObjectIdentity(entry.TargetPhysicalObjectIdentity);
}
