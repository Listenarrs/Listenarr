namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private enum MarkerlessNativeRenameFailureObservation
    {
        NotApplied,
        Published,
        Unavailable,
        Indeterminate
    }

    private static bool IsUnsupportedMarkerlessNativeRenameError(int nativeErrorCode) =>
        OperatingSystem.IsLinux()
        && nativeErrorCode is 18 or 22 or 38 or 95;

    private static MarkerlessNativeRenameFailureObservation ObserveFailedMarkerlessNativeRename(
        PinnedDirectoryCreation.PinnedDirectoryAnchor sourceParent,
        string sourceName,
        PinnedDirectoryCreation.PinnedDirectoryAnchor targetParent,
        string targetName,
        string expectedSourceIdentity)
    {
        var sourceParentVisibility = sourceParent.ProbeVisiblePathMatch();
        var targetParentVisibility = targetParent.ProbeVisiblePathMatch();
        if (sourceParentVisibility == RegistrationPublicationMatchOutcome.Unavailable
            || targetParentVisibility == RegistrationPublicationMatchOutcome.Unavailable)
        {
            return MarkerlessNativeRenameFailureObservation.Unavailable;
        }
        if (sourceParentVisibility != RegistrationPublicationMatchOutcome.Match
            || targetParentVisibility != RegistrationPublicationMatchOutcome.Match)
        {
            return MarkerlessNativeRenameFailureObservation.Indeterminate;
        }

        var sourceOutcome = sourceParent.TryOpenExistingFileWithOutcome(
            sourceName,
            requireDeleteAccess: false,
            out var observedSource);
        using (observedSource)
        {
            var targetOutcome = targetParent.TryOpenExistingFileWithOutcome(
                targetName,
                requireDeleteAccess: false,
                out var observedTarget);
            using (observedTarget)
            {
                if (sourceOutcome == PinnedFileOpenOutcome.Unavailable
                    || targetOutcome == PinnedFileOpenOutcome.Unavailable)
                {
                    return MarkerlessNativeRenameFailureObservation.Unavailable;
                }

                var sourceStillOwnsName = sourceOutcome == PinnedFileOpenOutcome.Opened
                    && observedSource != null
                    && observedSource.VisiblePathMatches()
                    && observedSource.MatchesObjectIdentity(expectedSourceIdentity);
                var targetNowOwnsSource = targetOutcome == PinnedFileOpenOutcome.Opened
                    && observedTarget != null
                    && observedTarget.VisiblePathMatches()
                    && observedTarget.MatchesObjectIdentity(expectedSourceIdentity);

                if (sourceStillOwnsName
                    && targetOutcome == PinnedFileOpenOutcome.NotFound)
                {
                    return MarkerlessNativeRenameFailureObservation.NotApplied;
                }
                if (sourceOutcome == PinnedFileOpenOutcome.NotFound
                    && targetNowOwnsSource)
                {
                    return MarkerlessNativeRenameFailureObservation.Published;
                }

                return MarkerlessNativeRenameFailureObservation.Indeterminate;
            }
        }
    }

    private async Task<(bool Published, PinnedDirectoryCreation.PinnedFileEntry? VerificationLease)>
        RecoverObservedMarkerlessNativeRenameAsync(
            AudiobookContentMoveRequest request,
            MoveJobEntry entry,
            PinnedDirectoryCreation.PinnedDirectoryAnchor sourceParent,
            PinnedDirectoryCreation.PinnedDirectoryAnchor targetParent,
            string targetName,
            CancellationToken cancellationToken)
    {
        sourceParent.FlushDirectoryEntry();
        if (!string.Equals(
                sourceParent.FullPath,
                targetParent.FullPath,
                StringComparison.Ordinal))
        {
            targetParent.FlushDirectoryEntry();
        }

        var verificationLease = targetParent.OpenExistingFileForVerificationLease(targetName);
        try
        {
            if (string.IsNullOrWhiteSpace(entry.SourcePhysicalObjectIdentity)
                || !verificationLease.MatchesObjectIdentity(
                    entry.SourcePhysicalObjectIdentity)
                || !await PinnedFileMatchesManifestAsync(
                    verificationLease,
                    entry,
                    cancellationToken)
                || !PinnedFileVisibleOrThrowUnavailable(
                    verificationLease,
                    $"The observed native-rename target is temporarily unavailable: {entry.RelativePath}"))
            {
                throw new MoveNeedsAttentionException(
                    $"The observed native rename target could not be verified: {entry.RelativePath}");
            }

            faultInjector?.OnCopyMutation(
                request.JobId,
                CopyMutationFaultPoint.AfterMarkerlessNativeRenameBeforeStateUpdate);
            await UpdateTargetEntryStateAsync(
                request.JobId,
                request.LeaseToken,
                entry.RelativePath,
                MoveJobEntryCopyState.Verified,
                entry.SourcePhysicalObjectIdentity,
                cancellationToken);
            entry.CopyState = MoveJobEntryCopyState.Verified;
            entry.TargetPhysicalObjectIdentity = entry.SourcePhysicalObjectIdentity;
            var result = (Published: true, VerificationLease: verificationLease);
            verificationLease = null;
            return result;
        }
        finally
        {
            verificationLease?.Dispose();
        }
    }
}
