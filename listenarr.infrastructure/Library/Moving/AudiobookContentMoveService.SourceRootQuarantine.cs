namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private const string LegacyEmptySourceQuarantineDirectoryName =
        ".listenarr-empty-source";
    private const string EmptySourceQuarantineDirectoryName =
        ".listenarr-empty-source.state";
    private const string EmptySourceClaimDirectoryName = "source.claim";

    private async Task<bool> RecoverEmptySourceDirectoryQuarantineAsync(
        AudiobookContentMoveRequest request,
        ValidatedQuarantineOwnership ownership,
        string sourceParent,
        CancellationToken cancellationToken)
    {
        var legacyPath = Path.Join(
            ownership.DirectoryPath,
            LegacyEmptySourceQuarantineDirectoryName);
        if (File.Exists(legacyPath) || Directory.Exists(legacyPath))
        {
            throw new MoveNeedsAttentionException(
                "A legacy empty-source quarantine lacks exact-object recovery evidence and was preserved.");
        }

        var statePath = Path.Join(
            ownership.DirectoryPath,
            EmptySourceQuarantineDirectoryName);
        ValidateQuarantineMutationPath(ownership, statePath);
        if (File.Exists(statePath))
        {
            throw new MoveNeedsAttentionException(
                "The empty-source cleanup state path is occupied by a file.");
        }

        var sourceExists = Directory.Exists(request.Source);
        if (File.Exists(request.Source))
        {
            throw new MoveNeedsAttentionException(
                "The source path was recreated as a file while empty-source cleanup state exists; both were preserved.");
        }
        if (!Directory.Exists(statePath))
        {
            return sourceExists;
        }

        using var state = PinnedDirectoryCreation.OpenExistingForPublication(
            ownership.DirectoryPath,
            EmptySourceQuarantineDirectoryName);
        using var stateAnchor = state.OpenCreatedDirectoryAnchor();
        if (!state.VisiblePathMatches() || !stateAnchor.VisiblePathMatches())
        {
            throw new MoveNeedsAttentionException(
                "The empty-source cleanup state changed while it was being pinned.");
        }

        var entries = Directory.EnumerateFileSystemEntries(statePath).ToList();
        var claimPath = Path.Join(statePath, EmptySourceClaimDirectoryName);
        if (entries.Count == 0)
        {
            await EnsureMutationAuthorizedAsync(
                request,
                request.Source,
                request.Target,
                cancellationToken);
            if (File.Exists(request.Source)
                || Directory.Exists(request.Source) != sourceExists
                || !stateAnchor.VisiblePathMatches())
            {
                throw new MoveNeedsAttentionException(
                    "The source or empty-source cleanup state changed before recovery.");
            }

            DeleteEmptySourcePrivateDirectory(
                request,
                state,
                EmptySourceQuarantineDirectoryName,
                SourceCleanupFaultPoint.BeforeEmptySourceStateDelete);
            return sourceExists;
        }
        if (sourceExists)
        {
            throw new MoveNeedsAttentionException(
                "Both the source directory and its interrupted cleanup claim exist; both were preserved.");
        }
        if (entries.Count != 1
            || !string.Equals(
                Path.GetFileName(entries[0]),
                EmptySourceClaimDirectoryName,
                StringComparison.Ordinal)
            || File.Exists(claimPath)
            || !Directory.Exists(claimPath))
        {
            throw new MoveNeedsAttentionException(
                "The empty-source cleanup state contains unexpected content.");
        }

        using var claim = stateAnchor.OpenExistingChildForPublication(
            EmptySourceClaimDirectoryName);
        using var claimAnchor = claim.OpenCreatedDirectoryAnchor();
        if (!claim.VisiblePathMatches() || !claimAnchor.VisiblePathMatches())
        {
            throw new MoveNeedsAttentionException(
                "The interrupted empty-source claim changed while it was being pinned.");
        }

        if (Directory.EnumerateFileSystemEntries(claimPath).Any())
        {
            await RestorePinnedEmptySourceClaimAsync(
                request,
                sourceParent,
                state,
                claim,
                cancellationToken);
            return true;
        }

        await EnsureMutationAuthorizedAsync(
            request,
            request.Source,
            request.Target,
            cancellationToken);
        if (Directory.Exists(request.Source)
            || Directory.EnumerateFileSystemEntries(claimPath).Any()
            || !claimAnchor.VisiblePathMatches()
            || !stateAnchor.VisiblePathMatches())
        {
            throw new MoveNeedsAttentionException(
                "The source path or empty-source claim changed before deletion.");
        }

        DeleteEmptySourcePrivateDirectory(
            request,
            claim,
            EmptySourceClaimDirectoryName,
            SourceCleanupFaultPoint.BeforeEmptySourceClaimDelete);
        claimAnchor.Dispose();
        claim.Dispose();
        DeleteEmptySourcePrivateDirectory(
            request,
            state,
            EmptySourceQuarantineDirectoryName,
            SourceCleanupFaultPoint.BeforeEmptySourceStateDelete);
        return false;
    }

    private async Task QuarantineAndDeleteEmptySourceDirectoryAsync(
        AudiobookContentMoveRequest request,
        ValidatedQuarantineOwnership ownership,
        string sourceParent,
        CancellationToken cancellationToken)
    {
        var statePath = Path.Join(
            ownership.DirectoryPath,
            EmptySourceQuarantineDirectoryName);
        ValidateQuarantineMutationPath(ownership, statePath);
        if (File.Exists(statePath) || Directory.Exists(statePath))
        {
            throw new MoveNeedsAttentionException(
                "The empty-source cleanup state path is already occupied.");
        }

        if (!FileSystemSafety.TryValidateMutationTarget(
                request.Source,
                [sourceParent],
                out var safeSource,
                out var reason))
        {
            throw new MoveNeedsAttentionException(reason);
        }

        using var source = PinnedDirectoryCreation.OpenExistingForPublication(
            sourceParent,
            Path.GetFileName(safeSource));
        using var sourceAnchor = source.OpenCreatedDirectoryAnchor();
        if (Directory.EnumerateFileSystemEntries(safeSource).Any()
            || !source.VisiblePathMatches()
            || !sourceAnchor.VisiblePathMatches())
        {
            throw new MoveNeedsAttentionException(
                "The empty source directory changed before quarantine.");
        }

        using var quarantineAnchor =
            PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
                ownership.DirectoryPath);
        using var state = quarantineAnchor.TryCreateChildForPublication(
            EmptySourceQuarantineDirectoryName);
        if (!state.Created || !state.VisiblePathMatches())
        {
            throw new MoveNeedsAttentionException(
                "The empty-source private cleanup state could not be created exclusively.");
        }
        state.RestrictToCurrentUser();

        using var stateAnchor = state.OpenCreatedDirectoryAnchor();
        await EnsureMutationAuthorizedAsync(
            request,
            request.Source,
            request.Target,
            cancellationToken);
        faultInjector?.OnSourceCleanupMutation(
            request.JobId,
            SourceCleanupFaultPoint.BeforeEmptySourceDirectoryQuarantine);
        if (Directory.EnumerateFileSystemEntries(safeSource).Any()
            || !sourceAnchor.VisiblePathMatches()
            || !stateAnchor.VisiblePathMatches())
        {
            throw new MoveNeedsAttentionException(
                "The empty source directory changed during final authorization.");
        }

        using var claim = source.MovePinnedDirectoryTo(
            stateAnchor,
            EmptySourceClaimDirectoryName);
        using var claimAnchor = claim.OpenCreatedDirectoryAnchor();
        faultInjector?.OnSourceCleanupMutation(
            request.JobId,
            SourceCleanupFaultPoint.AfterEmptySourceDirectoryQuarantine);
        if (Directory.EnumerateFileSystemEntries(claim.FullPath).Any())
        {
            await RestorePinnedEmptySourceClaimAsync(
                request,
                sourceParent,
                state,
                claim,
                cancellationToken);
            throw new MoveNeedsAttentionException(
                "The source directory gained content during quarantine and was restored.");
        }

        await EnsureMutationAuthorizedAsync(
            request,
            request.Source,
            request.Target,
            cancellationToken);
        if (Directory.Exists(safeSource)
            || Directory.EnumerateFileSystemEntries(claim.FullPath).Any()
            || !claimAnchor.VisiblePathMatches()
            || !stateAnchor.VisiblePathMatches())
        {
            throw new MoveNeedsAttentionException(
                "The source path or empty-source claim changed before deletion.");
        }

        DeleteEmptySourcePrivateDirectory(
            request,
            claim,
            EmptySourceClaimDirectoryName,
            SourceCleanupFaultPoint.BeforeEmptySourceClaimDelete);
        claimAnchor.Dispose();
        claim.Dispose();
        sourceAnchor.Dispose();
        source.Dispose();
        DeleteEmptySourcePrivateDirectory(
            request,
            state,
            EmptySourceQuarantineDirectoryName,
            SourceCleanupFaultPoint.BeforeEmptySourceStateDelete);
    }

    private async Task RestorePinnedEmptySourceClaimAsync(
        AudiobookContentMoveRequest request,
        string sourceParent,
        PinnedDirectoryCreation state,
        PinnedDirectoryCreation claim,
        CancellationToken cancellationToken)
    {
        await EnsureMutationAuthorizedAsync(
            request,
            request.Source,
            request.Target,
            cancellationToken);
        if (File.Exists(request.Source) || Directory.Exists(request.Source))
        {
            throw new MoveNeedsAttentionException(
                "The source path was recreated while its cleanup claim existed; both paths were preserved.");
        }

        using var sourceParentAnchor =
            PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(sourceParent);
        using var restored = claim.MovePinnedDirectoryTo(
            sourceParentAnchor,
            Path.GetFileName(request.Source));
        if (!restored.VisiblePathMatches())
        {
            throw new MoveNeedsAttentionException(
                "The empty-source claim could not be restored to the source path.");
        }

        DeleteEmptySourcePrivateDirectory(
            request,
            state,
            EmptySourceQuarantineDirectoryName,
            SourceCleanupFaultPoint.BeforeEmptySourceStateDelete);
    }

    private void DeleteEmptySourcePrivateDirectory(
        AudiobookContentMoveRequest request,
        PinnedDirectoryCreation directory,
        string directoryName,
        SourceCleanupFaultPoint faultPoint)
    {
        try
        {
            faultInjector?.OnSourceCleanupMutation(request.JobId, faultPoint);
            directory.RetirePinnedEmptyDirectoryFromNamespace(
                directoryName);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new IOException(
                "Verified empty-source cleanup state could not be retired; durable recovery state was preserved for retry.",
                exception);
        }
    }
}
