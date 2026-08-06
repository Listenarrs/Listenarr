using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    public async Task RetainTargetScaffoldingAsync(
        AudiobookContentMoveRequest request,
        CancellationToken cancellationToken)
    {
        var scaffolding = (await GetCreatedDirectoriesAsync(request.JobId, cancellationToken))
            .OrderBy(directory => GetPathDepth(directory.Path))
            .ToList();
        if (scaffolding.Count == 0)
        {
            return;
        }

        var publishedRoot = scaffolding[0].Path;
        foreach (var directory in scaffolding.Where(directory =>
            directory.State is MoveCreatedDirectoryState.Created or MoveCreatedDirectoryState.Planned))
        {
            await UpdateCreatedDirectoryStateAsync(
                request.JobId,
                request.LeaseToken,
                directory.Path,
                MoveCreatedDirectoryState.Retained,
                cancellationToken);
            directory.State = MoveCreatedDirectoryState.Retained;
        }

        if (!Directory.Exists(publishedRoot))
        {
            return;
        }

        var marker = ReadScaffoldMarker(publishedRoot);
        if (marker == null)
        {
            return;
        }

        ValidateScaffoldMarker(
            marker,
            request.JobId,
            request.Target,
            publishedRoot,
            request.TargetSemantics);
        foreach (var directory in scaffolding.Where(directory =>
            directory.State == MoveCreatedDirectoryState.Retained
            && !FileSystemPathIdentity.AreEquivalent(
                directory.Path,
                request.Target,
                request.TargetSemantics)))
        {
            if (!Directory.Exists(directory.Path))
            {
                throw new MoveNeedsAttentionException(
                    "A move-created retained directory disappeared before durable ownership could be recorded.");
            }

            ValidateExistingMoveDirectory(
                directory.Path,
                "move-created retained directory");
            await directoryOwnershipStore.ClaimRetainedAsync(
                new LibraryDirectoryOwnershipClaim(
                    directory.Path,
                    request.TargetSemantics,
                    "move",
                    request.JobId),
                CancellationToken.None);
        }

        await EnsureMutationAuthorizedAsync(
            request,
            request.Source,
            request.Target,
            cancellationToken);
        var markerPath = Path.Join(publishedRoot, ScaffoldOwnerFileName);
        if (File.Exists(markerPath))
        {
            await RetirePinnedArtifactAsync(
                markerPath,
                entry => ValidateScaffoldMarker(
                    ReadScaffoldMarker(entry),
                    request.JobId,
                    request.Target,
                    publishedRoot,
                    request.TargetSemantics),
                () => EnsureMutationAuthorizedAsync(
                    request,
                    request.Source,
                    request.Target,
                    cancellationToken));
        }
    }

    public async Task CleanupTerminalTargetScaffoldingAsync(
        AudiobookContentMoveRequest request,
        CancellationToken cancellationToken)
    {
        if (await GetExecutionProtocolVersionAsync(
                request.JobId,
                cancellationToken)
            >= MoveExecutionProtocol.MarkerlessDatabaseState)
        {
            await CleanupTerminalMarkerlessTargetDirectoriesAsync(
                request,
                cancellationToken);
            return;
        }

        var scaffolding = (await GetCreatedDirectoriesAsync(request.JobId, cancellationToken))
            .OrderBy(directory => GetPathDepth(directory.Path))
            .ToList();
        if (scaffolding.Count == 0)
        {
            return;
        }

        var publishedRoot = scaffolding[0].Path;
        var parent = Path.GetDirectoryName(publishedRoot)
            ?? throw new MoveNeedsAttentionException(
                "The target scaffold root has no parent directory.");
        var temporaryRoot = GetTemporaryScaffoldRoot(parent, request.JobId);
        var quarantine = Path.Join(parent, $".listenarr-scaffold-cleanup-{request.JobId:N}");
        await CleanupTargetScaffoldArtifactAsync(
            temporaryRoot,
            publishedRoot,
            scaffolding,
            request,
            TargetScaffoldTemporaryArtifactType,
            injectQuarantineDeleteFaults: false,
            cancellationToken);

        var quarantineTombstonePath = GetCleanupTombstonePath(
            quarantine,
            TargetScaffoldQuarantineArtifactType,
            request.JobId);
        var hasQuarantineTombstone = HasCleanupTombstoneEvidence(quarantineTombstonePath);
        var publishedExists = IsSafeExistingScaffoldDirectory(
            publishedRoot,
            "published target scaffold");
        var quarantineExists = IsSafeExistingScaffoldDirectory(
            quarantine,
            "target scaffold cleanup quarantine");

        if (publishedExists && quarantineExists)
        {
            throw new MoveNeedsAttentionException(
                "Both the published target scaffold and its cleanup quarantine exist.");
        }

        if (quarantineExists)
        {
            await ResumeTargetScaffoldQuarantineAsync(
                request,
                scaffolding,
                publishedRoot,
                quarantine,
                cancellationToken);
            return;
        }

        if (!publishedExists)
        {
            if (hasQuarantineTombstone)
            {
                await CleanupTargetScaffoldArtifactAsync(
                    quarantine,
                    publishedRoot,
                    scaffolding,
                    request,
                    TargetScaffoldQuarantineArtifactType,
                    injectQuarantineDeleteFaults: true,
                    cancellationToken);
            }

            await MarkRemovedScaffoldingAsync(
                request,
                scaffolding,
                publishedRoot,
                quarantine,
                cancellationToken);
            return;
        }

        var marker = ReadScaffoldMarker(publishedRoot);
        if (marker == null)
        {
            if (scaffolding.All(directory => directory.State == MoveCreatedDirectoryState.Retained))
            {
                if (hasQuarantineTombstone)
                {
                    await CleanupTargetScaffoldArtifactAsync(
                        quarantine,
                        publishedRoot,
                        scaffolding,
                        request,
                        TargetScaffoldQuarantineArtifactType,
                        injectQuarantineDeleteFaults: true,
                        cancellationToken);
                }
                return;
            }

            throw new MoveNeedsAttentionException(
                "Target scaffolding cannot be cleaned because its ownership marker is missing.");
        }

        ValidateScaffoldMarker(
            marker,
            request.JobId,
            request.Target,
            publishedRoot,
            request.TargetSemantics);
        if (!IsPublishedScaffoldEmpty(
                publishedRoot,
                scaffolding,
                request.Target,
                request.TargetSemantics))
        {
            if (hasQuarantineTombstone)
            {
                await CleanupTargetScaffoldArtifactAsync(
                    quarantine,
                    publishedRoot,
                    scaffolding,
                    request,
                    TargetScaffoldQuarantineArtifactType,
                    injectQuarantineDeleteFaults: true,
                    cancellationToken);
            }

            foreach (var directory in scaffolding.Where(directory =>
                directory.State != MoveCreatedDirectoryState.Retained))
            {
                await UpdateCreatedDirectoryStateAsync(
                    request.JobId,
                    request.LeaseToken,
                    directory.Path,
                    MoveCreatedDirectoryState.Retained,
                    cancellationToken);
            }

            await EnsureMutationAuthorizedAsync(
                request,
                request.Source,
                request.Target,
                cancellationToken);
            var markerPath = Path.Join(publishedRoot, ScaffoldOwnerFileName);
            if (File.Exists(markerPath))
            {
                await RetirePinnedArtifactAsync(
                    markerPath,
                    entry => ValidateScaffoldMarker(
                        ReadScaffoldMarker(entry),
                        request.JobId,
                        request.Target,
                        publishedRoot,
                        request.TargetSemantics),
                    () => EnsureMutationAuthorizedAsync(
                        request,
                        request.Source,
                        request.Target,
                        cancellationToken));
            }
            return;
        }

        await MarkScaffoldingCleanupIntentAsync(
            request,
            scaffolding,
            cancellationToken);
        await EnsureTargetScaffoldCleanupTombstoneAsync(
            quarantine,
            publishedRoot,
            request,
            TargetScaffoldQuarantineArtifactType,
            cancellationToken);
        faultInjector?.OnTargetScaffoldCleanup(
            request.JobId,
            TargetScaffoldCleanupFaultPoint.BeforeQuarantineRename);
        using (var publication = PinnedDirectoryCreation.OpenExistingForPublication(
            parent,
            Path.GetFileName(publishedRoot)))
        {
            using var publishedAnchor = publication.OpenCreatedDirectoryAnchor();
            await EnsureMutationAuthorizedAsync(
                request,
                request.Source,
                request.Target,
                cancellationToken);
            IsSafeExistingScaffoldDirectory(
                publishedRoot,
                "published target scaffold");
            ValidateScaffoldMarker(
                ReadScaffoldMarker(publishedRoot),
                request.JobId,
                request.Target,
                publishedRoot,
                request.TargetSemantics);
            if (!IsPublishedScaffoldEmpty(
                    publishedRoot,
                    scaffolding,
                    request.Target,
                    request.TargetSemantics)
                || !publishedAnchor.VisiblePathMatches())
            {
                throw new MoveNeedsAttentionException(
                    "Published target scaffolding changed before cleanup quarantine publication.");
            }

            using var quarantineAnchor = publication.RepublishPinnedDirectory(
                Path.GetFileName(publishedRoot),
                Path.GetFileName(quarantine));
        }
        faultInjector?.OnTargetScaffoldCleanup(
            request.JobId,
            TargetScaffoldCleanupFaultPoint.AfterQuarantineRename);
        await ResumeTargetScaffoldQuarantineAsync(
            request,
            scaffolding,
            publishedRoot,
            quarantine,
            cancellationToken);
    }

    private async Task ResumeTargetScaffoldQuarantineAsync(
        AudiobookContentMoveRequest request,
        IReadOnlyCollection<MoveJobCreatedDirectory> scaffolding,
        string publishedRoot,
        string quarantine,
        CancellationToken cancellationToken)
    {
        if (TryGetExistingPathAttributes(publishedRoot, out _))
        {
            throw new MoveNeedsAttentionException(
                "The published target scaffold was recreated after cleanup began.");
        }

        faultInjector?.OnTargetScaffoldCleanup(
            request.JobId,
            TargetScaffoldCleanupFaultPoint.BeforeQuarantineValidation);
        var tombstonePath = GetCleanupTombstonePath(
            quarantine,
            TargetScaffoldQuarantineArtifactType,
            request.JobId);
        ValidateTargetScaffoldArtifactTree(
            quarantine,
            publishedRoot,
            scaffolding,
            request,
            requireScaffoldMarker: !HasCleanupTombstoneEvidence(tombstonePath));
        await MarkScaffoldingCleanupIntentAsync(
            request,
            scaffolding,
            cancellationToken);
        await EnsureMutationAuthorizedAsync(
            request,
            request.Source,
            request.Target,
            cancellationToken);
        if (TryGetExistingPathAttributes(publishedRoot, out _))
        {
            throw new MoveNeedsAttentionException(
                "The published target scaffold was recreated while cleanup was being validated.");
        }

        faultInjector?.OnTargetScaffoldCleanup(
            request.JobId,
            TargetScaffoldCleanupFaultPoint.BeforeQuarantineDelete);
        await CleanupTargetScaffoldArtifactAsync(
            quarantine,
            publishedRoot,
            scaffolding,
            request,
            TargetScaffoldQuarantineArtifactType,
            injectQuarantineDeleteFaults: true,
            cancellationToken);
        faultInjector?.OnTargetScaffoldCleanup(
            request.JobId,
            TargetScaffoldCleanupFaultPoint.AfterQuarantineDelete);
        await MarkRemovedScaffoldingAsync(
            request,
            scaffolding,
            publishedRoot,
            quarantine,
            cancellationToken);
    }

    private async Task MarkScaffoldingCleanupIntentAsync(
        AudiobookContentMoveRequest request,
        IEnumerable<MoveJobCreatedDirectory> scaffolding,
        CancellationToken cancellationToken)
    {
        foreach (var directory in scaffolding.Where(directory =>
            directory.State is MoveCreatedDirectoryState.Planned
                or MoveCreatedDirectoryState.Retained))
        {
            faultInjector?.OnTargetScaffoldCleanup(
                request.JobId,
                TargetScaffoldCleanupFaultPoint.BeforeCleanupIntentStateUpdate);
            await UpdateCreatedDirectoryStateAsync(
                request.JobId,
                request.LeaseToken,
                directory.Path,
                MoveCreatedDirectoryState.Created,
                cancellationToken);
            directory.State = MoveCreatedDirectoryState.Created;
        }
    }

    private async Task MarkRemovedScaffoldingAsync(
        AudiobookContentMoveRequest request,
        IEnumerable<MoveJobCreatedDirectory> scaffolding,
        string publishedRoot,
        string quarantine,
        CancellationToken cancellationToken)
    {
        foreach (var directory in scaffolding.Where(directory =>
            directory.State is not (
                MoveCreatedDirectoryState.Removed or MoveCreatedDirectoryState.Retained)))
        {
            faultInjector?.OnTargetScaffoldCleanup(
                request.JobId,
                TargetScaffoldCleanupFaultPoint.BeforeRemovedStateUpdate);
            if (TryGetExistingPathAttributes(publishedRoot, out _)
                || TryGetExistingPathAttributes(quarantine, out _))
            {
                throw new MoveNeedsAttentionException(
                    "Target scaffold cleanup state cannot be marked removed while an owned artifact still exists or the published path was recreated.");
            }

            await UpdateCreatedDirectoryStateAsync(
                request.JobId,
                request.LeaseToken,
                directory.Path,
                MoveCreatedDirectoryState.Removed,
                cancellationToken);
            directory.State = MoveCreatedDirectoryState.Removed;
        }
    }
}
