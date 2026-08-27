using System.ComponentModel;
using Microsoft.Extensions.Logging;

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
            if (TryGetMarkerlessPathAttributes(sourcePath, out _))
            {
                continue;
            }

            var targetPath = ResolveManifestPath(
                target,
                entry,
                request.TargetSemantics,
                "target");
            var targetParentPath = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrWhiteSpace(targetParentPath))
            {
                continue;
            }
            if (!TryGetMarkerlessPathAttributes(
                    targetParentPath,
                    out var targetParentAttributes))
            {
                continue;
            }
            if ((targetParentAttributes & FileAttributes.Directory) == 0
                || (targetParentAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new MoveNeedsAttentionException(
                    "Interrupted native-rename recovery target parent changed type or became linked.");
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
        if (!(OperatingSystem.IsWindows()
                || OperatingSystem.IsLinux()
                || OperatingSystem.IsMacOS())
            || (faultInjector != null && !faultInjector.AllowMarkerlessFileRename))
        {
            return null;
        }

        var stableOpenOutcome =
            sourceParent.TryOpenExistingFileForStableDeleteWithOutcome(
                Path.GetFileName(sourceEntry.FullPath),
                out var stableEntry);
        if (stableOpenOutcome == PinnedFileOpenOutcome.Unavailable)
        {
            throw new IOException(
                $"The markerless source is temporarily unavailable for stable retirement before publication: {entry.RelativePath}");
        }
        if (stableOpenOutcome != PinnedFileOpenOutcome.Opened
            || stableEntry == null)
        {
            throw new MoveNeedsAttentionException(
                $"The markerless source changed before stable retirement could be authorized: {entry.RelativePath}");
        }

        try
        {
            if (!stableEntry.IdentifiesSameEntry(sourceEntry)
                || !PinnedFileVisibleOrThrowUnavailable(
                    stableEntry,
                    $"The markerless native-rename source is temporarily unavailable: {entry.RelativePath}")
                || !PinnedDirectoryVisibleOrThrowUnavailable(
                    sourceParent,
                    $"The markerless native-rename source parent is temporarily unavailable: {entry.RelativePath}")
                || !PinnedDirectoryVisibleOrThrowUnavailable(
                    targetParent,
                    $"The markerless native-rename target parent is temporarily unavailable: {entry.RelativePath}")
                || !PinnedFileLengthMatchesManifest(stableEntry, entry)
                || string.IsNullOrWhiteSpace(entry.SourcePhysicalObjectIdentity)
                || !stableEntry.MatchesObjectIdentity(
                    entry.SourcePhysicalObjectIdentity))
            {
                throw new MoveNeedsAttentionException(
                    $"The markerless source changed before stable native-rename publication: {entry.RelativePath}");
            }

            if (!sourceEntry.IsOnSameVolume(targetParent))
            {
                stableEntry.Dispose();
                return null;
            }

            return stableEntry;
        }
        catch
        {
            stableEntry.Dispose();
            throw;
        }
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
            PinnedDirectoryCreation.PinnedFileEntry stableRenameEntry,
            CancellationToken cancellationToken)
    {
        if ((faultInjector != null && !faultInjector.AllowMarkerlessFileRename)
            || !sourceEntry.IsOnSameVolume(targetParent))
        {
            return (false, null);
        }

        PinnedDirectoryCreation.PinnedFileEntry? verificationLease = null;
        var renameEntry = stableRenameEntry;

        try
        {
            if (!renameEntry.IdentifiesSameEntry(sourceEntry)
                || !PinnedFileVisibleOrThrowUnavailable(
                    renameEntry,
                    $"The markerless native-rename source is temporarily unavailable before publication: {entry.RelativePath}")
                || !PinnedDirectoryVisibleOrThrowUnavailable(
                    sourceParent,
                    $"The markerless native-rename source parent is temporarily unavailable before publication: {entry.RelativePath}")
                || !PinnedDirectoryVisibleOrThrowUnavailable(
                    targetParent,
                    $"The markerless native-rename target parent is temporarily unavailable before publication: {entry.RelativePath}")
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
            faultInjector?.OnCopyMutation(
                request.JobId,
                CopyMutationFaultPoint.BeforeMarkerlessNativeRenameMutation);
            var targetName = Path.GetFileName(ResolveManifestPath(
                target,
                entry,
                request.TargetSemantics,
                "target"));
            PinnedDirectoryCreation.PinnedRenameAttempt renameAttempt;
            if (faultInjector?.MarkerlessNativeRenameErrorForTest is int injectedError)
            {
                if (faultInjector.MarkerlessNativeRenamePublishesBeforeErrorForTest)
                {
                    var publishedAttempt = renameEntry.TryMoveToNoReplace(
                        targetParent,
                        targetName);
                    if (!publishedAttempt.Published)
                    {
                        throw new InvalidOperationException(
                            "The native-rename published-error test hook could not publish its source entry.");
                    }
                }
                renameAttempt = new PinnedDirectoryCreation.PinnedRenameAttempt(
                    false,
                    injectedError);
            }
            else
            {
                renameAttempt = renameEntry.TryMoveToNoReplace(
                    targetParent,
                    targetName);
            }
            if (!renameAttempt.Published)
            {
                faultInjector?.OnCopyMutation(
                    request.JobId,
                    CopyMutationFaultPoint.AfterMarkerlessNativeRenameFailureBeforeObservation);
                var observation = ObserveFailedMarkerlessNativeRename(
                    sourceParent,
                    Path.GetFileName(sourceEntry.FullPath),
                    targetParent,
                    targetName,
                    entry.SourcePhysicalObjectIdentity!);
                if (observation == MarkerlessNativeRenameFailureObservation.Published)
                {
                    return await RecoverObservedMarkerlessNativeRenameAsync(
                        request,
                        entry,
                        sourceParent,
                        targetParent,
                        targetName,
                        cancellationToken);
                }
                if (observation == MarkerlessNativeRenameFailureObservation.NotApplied
                    && IsUnsupportedMarkerlessNativeRenameError(
                        renameAttempt.NativeErrorCode))
                {
                    if (!await PinnedFileMatchesManifestAsync(
                            sourceEntry,
                            entry,
                            cancellationToken))
                    {
                        throw new MoveNeedsAttentionException(
                            $"The source content changed before native-rename fallback could be authorized: {entry.RelativePath}");
                    }
                    ValidateMarkerlessSourceEntry(request, entry, sourceEntry);
                    faultInjector?.OnCopyMutation(
                        request.JobId,
                        CopyMutationFaultPoint.AfterMarkerlessNativeRenameFallbackAuthorized);
                    logger.LogDebug(
                        "Native no-replace rename is unsupported for {File} (errno {Error}); using verified copy fallback",
                        LogRedaction.SanitizeFilePath(entry.RelativePath),
                        renameAttempt.NativeErrorCode);
                    return (false, null);
                }
                if (observation == MarkerlessNativeRenameFailureObservation.Unavailable)
                {
                    throw new IOException(
                        $"The filesystem became temporarily unavailable while determining whether native rename was applied: {entry.RelativePath}");
                }
                if (observation == MarkerlessNativeRenameFailureObservation.NotApplied)
                {
                    throw new Win32Exception(
                        renameAttempt.NativeErrorCode,
                        "Could not publish a pinned filesystem entry relative to its owned directory.");
                }

                throw new MoveNeedsAttentionException(
                    $"The native rename result is ambiguous and copy fallback is not authorized: {entry.RelativePath}");
            }

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
            if (!PinnedFileVisibleOrThrowUnavailable(
                    renameEntry,
                    $"The markerless native-rename target is temporarily unavailable after publication: {entry.RelativePath}")
                || !PinnedDirectoryVisibleOrThrowUnavailable(
                    sourceParent,
                    $"The markerless native-rename source parent is temporarily unavailable after publication: {entry.RelativePath}")
                || !PinnedDirectoryVisibleOrThrowUnavailable(
                    targetParent,
                    $"The markerless native-rename target parent is temporarily unavailable after publication: {entry.RelativePath}")
                || !renameEntry.MatchesObjectIdentity(
                    entry.SourcePhysicalObjectIdentity!))
            {
                throw new MoveNeedsAttentionException(
                    $"The markerless native rename target changed physical generation: {entry.RelativePath}");
            }

            verificationLease = targetParent.OpenExistingFileForVerificationLease(
                Path.GetFileName(renameEntry.FullPath));
            if (!verificationLease.IdentifiesSameEntry(renameEntry)
                || !PinnedFileVisibleOrThrowUnavailable(
                    verificationLease,
                    $"The markerless native-rename verification lease is temporarily unavailable: {entry.RelativePath}"))
            {
                throw new MoveNeedsAttentionException(
                    $"The markerless native rename verification lease did not capture the published generation: {entry.RelativePath}");
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
        }
    }

    private async Task<bool> TryRecoverMarkerlessNativeRenameAsync(
        AudiobookContentMoveRequest request,
        MoveJobEntry entry,
        PinnedDirectoryCreation.PinnedFileEntry targetEntry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entry.SourcePhysicalObjectIdentity)
            || !PinnedFileVisibleOrThrowUnavailable(
                targetEntry,
                $"The interrupted markerless native-rename target is temporarily unavailable: {entry.RelativePath}")
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
            || !await PinnedFileMatchesManifestAsync(
                targetEntry,
                entry,
                cancellationToken))
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
            || !await PinnedFileMatchesManifestAsync(
                targetEntry,
                entry,
                cancellationToken))
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
        PinnedFileVisibleOrThrowUnavailable(
            targetEntry,
            $"The markerless native-rename target is temporarily unavailable during cleanup: {entry.RelativePath}")
        && !string.IsNullOrWhiteSpace(entry.SourcePhysicalObjectIdentity)
        && !string.IsNullOrWhiteSpace(entry.TargetPhysicalObjectIdentity)
        && targetEntry.MatchesObjectIdentity(entry.SourcePhysicalObjectIdentity)
        && targetEntry.MatchesObjectIdentity(entry.TargetPhysicalObjectIdentity);
}
