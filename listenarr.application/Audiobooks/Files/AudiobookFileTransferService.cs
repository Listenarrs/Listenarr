using System.Runtime.ExceptionServices;
using Listenarr.Application.Common;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Files;

/// <summary>
/// Transfers tracked audio files from one audiobook record to another.
/// Everything runs under the global filesystem boundary and the ordered locks of
/// both audiobooks. A cancelable preflight re-proves each row's identity for its
/// current path, checks that the destination may own that identity
/// (<see cref="IAudiobookFileRepository.CheckOwnershipAsync"/>) and turns away any
/// file that could not end up in the destination folder (missing on disk, a name
/// already taken there, or a different library root). Nothing has changed at that
/// point. The first owner change, a compare-and-swap against the complete persisted
/// identity, starts a non-cancelable phase that re-homes the accepted rows, moves
/// them into the destination folder as a regular organize of the destination
/// (<see cref="IRenameService"/>), so the physical move is verified, journaled and
/// owner-bound exactly like any other rename, and then records the result. If the
/// move itself fails, organize rolls it back and the files keep their new owner
/// where they are.
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

        // Everything that can turn a file away is decided first, while the request may
        // still be cancelled and nothing has changed.
        var destinationPlan = await PlanDestinationAsync(target, cancellationToken);
        var outcomes = new AudiobookFileTransferItem?[selected.Count];
        var prepared = new List<PreparedTransfer>(selected.Count);
        var plannedDestinations = new List<string>();
        for (var index = 0; index < selected.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (rejection, ready) = await PrepareTransferAsync(
                index,
                source,
                target,
                selected[index],
                cancellationToken);
            rejection ??= CheckPlacement(ready!, destinationPlan, plannedDestinations);
            if (rejection != null)
            {
                outcomes[index] = rejection;
                continue;
            }

            prepared.Add(ready!);
        }

        if (prepared.Count == 0)
        {
            return new AudiobookFileTransferResult(
                AudiobookFileTransferStatus.Completed,
                outcomes.Select(outcome => outcome!).ToList(),
                source.Title,
                target.Title);
        }

        // The first ownership change is the point of no return: from here the transfer
        // runs to a stable persisted state even if the request goes away. The organize
        // protocol is non-cancelable past its own preflight for the same reason.
        var completionToken = RequestCancellationBoundary.EnterNonCancelablePhase(
            cancellationToken);
        ExceptionDispatchInfo? failure = null;
        try
        {
            foreach (var ready in prepared)
            {
                outcomes[ready.Index] = await CommitOwnershipAsync(
                    source,
                    target,
                    ready,
                    completionToken);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            && exception is not StackOverflowException)
        {
            // Files re-homed before the failure still get relocated and recorded below.
            failure = ExceptionDispatchInfo.Capture(exception);
        }

        var items = outcomes
            .Where(outcome => outcome != null)
            .Select(outcome => outcome!)
            .ToList();
        string? relocationError = null;
        if (items.Any(item => item.Transferred))
        {
            try
            {
                (items, relocationError) = await RelocateAsync(target, items, completionToken);
            }
            finally
            {
                await ReconcileSourceSummaryAsync(source.Id, completionToken);
                await AddHistoryAsync(source, target, items.Count(item => item.Transferred));
            }
        }

        failure?.Throw();
        return new AudiobookFileTransferResult(
            AudiobookFileTransferStatus.Completed,
            items,
            source.Title,
            target.Title,
            relocationError);
    }

    private async Task<(AudiobookFileTransferItem? Rejection, PreparedTransfer? Ready)> PrepareTransferAsync(
        int index,
        Audiobook source,
        Audiobook target,
        AudiobookFile file,
        CancellationToken cancellationToken)
    {
        var check = await CheckOwnershipTransferAsync(source, target, file, cancellationToken);
        return check.Rejection != null
            ? (check.Rejection, null)
            : (null, new PreparedTransfer(index, file, check.AbsolutePath!, check.Identity!));
    }

    private async Task<(AudiobookFileTransferItem? Rejection, string? AbsolutePath, AudiobookFilePathIdentity? Identity)> CheckOwnershipTransferAsync(
        Audiobook source,
        Audiobook target,
        AudiobookFile file,
        CancellationToken cancellationToken)
    {
        if (file.AudiobookId != source.Id)
        {
            return Refused(
                file,
                AudiobookFileTransferOutcome.NotOwnedBySource,
                "The file does not belong to the source audiobook.");
        }

        if (string.IsNullOrWhiteSpace(file.Path))
        {
            return Refused(
                file,
                AudiobookFileTransferOutcome.IdentityUnavailable,
                "The file has no stored path.");
        }

        if (file.PathIdentityState != PathIdentityState.Valid
            || string.IsNullOrWhiteSpace(file.PathOwnershipKey))
        {
            return Refused(
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
            return Refused(file, AudiobookFileTransferOutcome.IdentityUnavailable, exception.Message);
        }

        if (identity.State != PathIdentityState.Valid
            || string.IsNullOrWhiteSpace(identity.OwnershipKey))
        {
            return Refused(
                file,
                AudiobookFileTransferOutcome.IdentityUnavailable,
                identity.Reason ?? "Filesystem identity is unavailable for the file's path.");
        }

        if (!string.Equals(identity.OwnershipKey, file.PathOwnershipKey, StringComparison.Ordinal))
        {
            // The same guard that keeps a scan from re-registering a moved file: a
            // row whose persisted key no longer describes its path is repaired by
            // identity reconciliation, never carried into another owner.
            return Refused(
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
            return Refused(
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

        return (null, absolutePath, identity);
    }

    private async Task<AudiobookFileTransferItem> CommitOwnershipAsync(
        Audiobook source,
        Audiobook target,
        PreparedTransfer ready,
        CancellationToken cancellationToken)
    {
        var file = ready.File;
        var absolutePath = ready.AbsolutePath;
        var identity = ready.Identity;
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

    private static (AudiobookFileTransferItem? Rejection, string? AbsolutePath, AudiobookFilePathIdentity? Identity) Refused(
        AudiobookFile file,
        AudiobookFileTransferOutcome outcome,
        string reason) =>
        (Rejected(file, outcome, reason), null, null);

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

    // The legacy single-file columns must not keep describing audio the source no
    // longer owns: they are repointed at a file that stayed, or cleared when none did.
    // Path-bearing columns only change through a tracked entity.
    private async Task ReconcileSourceSummaryAsync(
        int sourceId,
        CancellationToken cancellationToken)
    {
        var tracked = await audiobookRepository.GetByIdAsync(sourceId);
        if (tracked == null || (tracked.FilePath == null && tracked.FileSize == null))
        {
            return;
        }

        var remaining = (await audiobookFileRepository.GetByAudiobookIdAsync(
                sourceId,
                cancellationToken))
            .Select(file => (File: file, Path: TryResolveAbsolutePath(tracked, file.Path)))
            .Where(entry => entry.Path != null)
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToList();
        if (remaining.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(tracked.FilePath))
            {
                return;
            }

            var summaryPath = TryResolveAbsolutePath(tracked, tracked.FilePath);
            if (summaryPath != null
                && remaining.Any(entry => string.Equals(entry.Path, summaryPath, StringComparison.Ordinal)))
            {
                return;
            }
        }

        var primary = remaining.FirstOrDefault();
        tracked.FilePath = primary.Path;
        tracked.FileSize = primary.File?.Size > 0 ? primary.File.Size : null;
        await audiobookRepository.UpdateAsync(tracked);
    }

    private static string? TryResolveAbsolutePath(Audiobook owner, string? storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
        {
            return null;
        }

        try
        {
            return ResolveAbsolutePath(owner, storedPath);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
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
