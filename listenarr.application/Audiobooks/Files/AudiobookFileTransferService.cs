using Listenarr.Application.Common;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Files;

/// <summary>
/// Transfers tracked audio files from one audiobook record to another.
/// Phase one re-homes each row under the global filesystem boundary and the
/// ordered locks of both audiobooks: the row's identity is re-proven for its
/// current path, the destination must be allowed to own that identity
/// (<see cref="IAudiobookFileRepository.CheckOwnershipAsync"/>) and the owner
/// change is a compare-and-swap against the complete persisted identity.
/// Phase two moves the files into the destination folder as a regular organize
/// of the destination (<see cref="IRenameService"/>), so the physical move is
/// verified, journaled and owner-bound exactly like any other rename. A file
/// that cannot be moved keeps its new owner and stays where it is.
/// </summary>
public sealed partial class AudiobookFileTransferService(
    IAudiobookRepository audiobookRepository,
    IAudiobookFileRepository audiobookFileRepository,
    IAudiobookFilePathIdentityResolver filePathIdentityResolver,
    IFilesystemMutationCoordinator filesystemMutationCoordinator,
    IAudiobookOperationCoordinator audiobookOperationCoordinator,
    IMoveQueueService moveQueueService,
    IRenameService renameService,
    IRootFolderService rootFolderService,
    IConfigurationService configurationService,
    IFileSystem fileSystem,
    ILogger<AudiobookFileTransferService> logger,
    IHistoryRepository? historyRepository = null) : IAudiobookFileTransferService
{
    public Task<AudiobookFileTransferResult> TransferAsync(
        AudiobookFileTransferRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SourceAudiobookId == request.TargetAudiobookId)
        {
            return Task.FromResult(AudiobookFileTransferResult.Failed(
                AudiobookFileTransferStatus.SameAudiobook));
        }

        return filesystemMutationCoordinator.ExecuteExclusiveAsync(
            globalToken => audiobookOperationCoordinator.ExecuteExclusiveAsync(
                [request.SourceAudiobookId, request.TargetAudiobookId],
                token => TransferCoreAsync(request, token),
                globalToken),
            cancellationToken);
    }

    private async Task<AudiobookFileTransferResult> TransferCoreAsync(
        AudiobookFileTransferRequest request,
        CancellationToken cancellationToken)
    {
        await moveQueueService.EnsureFilesystemMutationAllowedAsync(
            request.SourceAudiobookId,
            cancellationToken);
        await moveQueueService.EnsureFilesystemMutationAllowedAsync(
            request.TargetAudiobookId,
            cancellationToken);

        var source = await audiobookRepository.GetByIdSnapshotAsync(
            request.SourceAudiobookId,
            cancellationToken);
        if (source == null)
        {
            return AudiobookFileTransferResult.Failed(AudiobookFileTransferStatus.SourceNotFound);
        }

        var target = await audiobookRepository.GetByIdSnapshotAsync(
            request.TargetAudiobookId,
            cancellationToken);
        if (target == null)
        {
            return AudiobookFileTransferResult.Failed(AudiobookFileTransferStatus.TargetNotFound);
        }

        var sourceFiles = await audiobookFileRepository.GetByAudiobookIdAsync(
            source.Id,
            cancellationToken);
        List<AudiobookFile> selected;
        if (request.FileIds is { Count: > 0 })
        {
            var requested = request.FileIds.ToHashSet();
            selected = sourceFiles.Where(file => requested.Contains(file.Id)).ToList();
            if (selected.Count != requested.Count)
            {
                return AudiobookFileTransferResult.Failed(AudiobookFileTransferStatus.UnknownFileIds);
            }
        }
        else
        {
            selected = sourceFiles;
        }

        if (selected.Count == 0)
        {
            return AudiobookFileTransferResult.Failed(AudiobookFileTransferStatus.NoMatchingFiles);
        }

        var items = new List<AudiobookFileTransferItem>(selected.Count);
        foreach (var file in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(await TransferOwnershipAsync(source, target, file, cancellationToken));
        }

        string? relocationError = null;
        if (items.Any(item => item.Transferred))
        {
            // Ownership has moved; finish to a stable persisted state even if the
            // request goes away. The organize protocol is non-cancelable past its
            // own preflight for the same reason.
            var completionToken = RequestCancellationBoundary.EnterNonCancelablePhase(
                cancellationToken);
            (items, relocationError) = await RelocateAsync(target, items, completionToken);
            await ClearSourceLegacyColumnsIfEmptyAsync(source.Id, completionToken);
            await AddHistoryAsync(source, target, items.Count(item => item.Transferred));
        }

        return new AudiobookFileTransferResult(
            AudiobookFileTransferStatus.Completed,
            items,
            source.Title,
            target.Title,
            relocationError);
    }

    private async Task<AudiobookFileTransferItem> TransferOwnershipAsync(
        Audiobook source,
        Audiobook target,
        AudiobookFile file,
        CancellationToken cancellationToken)
    {
        if (file.AudiobookId != source.Id)
        {
            return Rejected(
                file,
                AudiobookFileTransferOutcome.NotOwnedBySource,
                "The file does not belong to the source audiobook.");
        }

        if (string.IsNullOrWhiteSpace(file.Path))
        {
            return Rejected(
                file,
                AudiobookFileTransferOutcome.IdentityUnavailable,
                "The file has no stored path.");
        }

        if (file.PathIdentityState != PathIdentityState.Valid
            || string.IsNullOrWhiteSpace(file.PathOwnershipKey))
        {
            return Rejected(
                file,
                AudiobookFileTransferOutcome.IdentityUnavailable,
                file.PathIdentityReason
                    ?? "The file's ownership identity has not been proven; scan the source audiobook first.");
        }

        string absolutePath;
        AudiobookFilePathIdentity identity;
        try
        {
            absolutePath = ResolveAbsolutePath(source, file.Path);
            identity = await filePathIdentityResolver.ResolveAsync(
                source,
                file.Path,
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            return Rejected(file, AudiobookFileTransferOutcome.IdentityUnavailable, exception.Message);
        }

        if (identity.State != PathIdentityState.Valid
            || string.IsNullOrWhiteSpace(identity.OwnershipKey))
        {
            return Rejected(
                file,
                AudiobookFileTransferOutcome.IdentityUnavailable,
                identity.Reason ?? "Filesystem identity is unavailable for the file's path.");
        }

        if (!string.Equals(identity.OwnershipKey, file.PathOwnershipKey, StringComparison.Ordinal))
        {
            // The same guard that keeps a scan from re-registering a moved file: a
            // row whose persisted key no longer describes its path is repaired by
            // identity reconciliation, never carried into another owner.
            return Rejected(
                file,
                AudiobookFileTransferOutcome.IdentityStale,
                "The file's persisted ownership identity no longer matches its path; rescan the source audiobook before transferring it.");
        }

        var ownership = await audiobookFileRepository.CheckOwnershipAsync(
            target.Id,
            file.Id,
            identity,
            cancellationToken);
        if (ownership.Outcome != AudiobookFileOwnershipCheckOutcome.Available)
        {
            return Rejected(
                file,
                AudiobookFileTransferOutcome.OwnershipConflict,
                ownership.Outcome switch
                {
                    AudiobookFileOwnershipCheckOutcome.OwnedByOtherAudiobook =>
                        "Another audiobook already owns a file with this identity.",
                    AudiobookFileOwnershipCheckOutcome.AlreadyOwnedByAudiobook =>
                        "The destination audiobook already owns a file with this identity.",
                    AudiobookFileOwnershipCheckOutcome.IdentityConflict =>
                        ownership.Reason ?? "The file identity conflicts with existing ownership data.",
                    _ => ownership.Reason ?? "The file identity is unavailable."
                });
        }

        var transferred = await audiobookFileRepository.TransferOwnershipAsync(
            file.Id,
            source.Id,
            file.CapturePathState(),
            target.Id,
            absolutePath,
            identity,
            cancellationToken);
        if (!transferred)
        {
            return Rejected(
                file,
                AudiobookFileTransferOutcome.Changed,
                "The file changed while the transfer was running; retry the transfer.");
        }

        logger.LogInformation(
            "Transferred audiobook file {FileId} from audiobook {SourceId} to {TargetId}",
            file.Id,
            source.Id,
            target.Id);
        return new AudiobookFileTransferItem(
            file.Id,
            absolutePath,
            AudiobookFileTransferOutcome.Transferred);
    }

    private static AudiobookFileTransferItem Rejected(
        AudiobookFile file,
        AudiobookFileTransferOutcome outcome,
        string reason) =>
        new(file.Id, file.Path, outcome, Reason: reason);

    // Mirrors the identity resolver's path resolution so the stored path becomes
    // absolute under the new owner: a path relative to the source's folder would
    // otherwise be re-read relative to the destination's folder.
    private static string ResolveAbsolutePath(Audiobook owner, string storedPath)
    {
        if (FileSystemPathIdentity.TryDetectAbsoluteSyntax(storedPath, out _))
        {
            return FileUtils.NormalizeStoredPath(storedPath);
        }

        if (string.IsNullOrWhiteSpace(owner.BasePath)
            || !FileSystemPathIdentity.TryDetectAbsoluteSyntax(owner.BasePath, out var baseSyntax))
        {
            throw new InvalidOperationException(
                "A relative audiobook file path requires an authoritative absolute audiobook base path.");
        }

        var containmentSemantics = new FileSystemPathSemantics(
            baseSyntax,
            FileSystemCaseSensitivity.Sensitive);
        if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                owner.BasePath,
                storedPath,
                containmentSemantics,
                out var resolvedPath))
        {
            throw new InvalidOperationException(
                "The relative audiobook file path could not be resolved safely within the audiobook base path.");
        }

        return FileUtils.NormalizeStoredPath(resolvedPath);
    }

    private async Task ClearSourceLegacyColumnsIfEmptyAsync(
        int sourceId,
        CancellationToken cancellationToken)
    {
        var remaining = await audiobookFileRepository.GetByAudiobookIdAsync(
            sourceId,
            cancellationToken);
        if (remaining.Count > 0)
        {
            return;
        }

        // The legacy single-file columns describe audio the source no longer owns.
        // Path-bearing columns only change through a tracked entity.
        var tracked = await audiobookRepository.GetByIdAsync(sourceId);
        if (tracked == null || (tracked.FilePath == null && tracked.FileSize == null))
        {
            return;
        }

        tracked.FilePath = null;
        tracked.FileSize = null;
        await audiobookRepository.UpdateAsync(tracked);
    }

    private async Task AddHistoryAsync(Audiobook source, Audiobook target, int transferred)
    {
        if (historyRepository == null || transferred == 0)
        {
            return;
        }

        try
        {
            var timestamp = DateTime.UtcNow;
            await historyRepository.AddAsync(new History
            {
                AudiobookId = source.Id,
                AudiobookTitle = source.Title ?? "Unknown Title",
                EventType = "Files Transferred",
                Message = $"Moved {transferred} file(s) to '{target.Title}' (id {target.Id}).",
                Source = "transfer-files",
                Timestamp = timestamp
            }, CancellationToken.None);
            await historyRepository.AddAsync(new History
            {
                AudiobookId = target.Id,
                AudiobookTitle = target.Title ?? "Unknown Title",
                EventType = "Files Received",
                Message = $"Received {transferred} file(s) from '{source.Title}' (id {source.Id}).",
                Source = "transfer-files",
                Timestamp = timestamp
            }, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            && exception is not OutOfMemoryException
            && exception is not StackOverflowException)
        {
            logger.LogWarning(
                exception,
                "Failed to record transfer history for audiobooks {SourceId} and {TargetId}",
                source.Id,
                target.Id);
        }
    }
}
