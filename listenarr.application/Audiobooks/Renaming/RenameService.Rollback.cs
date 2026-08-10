using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Renaming;

public partial class RenameService
{
    private static AudiobookPathRollbackState CaptureAudiobookPathRollbackState(
        Audiobook audiobook) =>
        new(
            audiobook.BasePath,
            audiobook.FilePath,
            audiobook.FileSize,
            (audiobook.Files ?? [])
                .ToDictionary(file => file.Id, file => file.CapturePathState()));

    private static void RestoreAudiobookPathState(
        Audiobook audiobook,
        AudiobookPathRollbackState rollbackState)
    {
        audiobook.BasePath = rollbackState.BasePath;
        audiobook.FilePath = rollbackState.LegacyFilePath;
        audiobook.FileSize = rollbackState.FileSize;
        foreach (var file in audiobook.Files ?? [])
        {
            if (rollbackState.FileStates.TryGetValue(file.Id, out var fileState))
            {
                file.RestorePathState(fileState);
            }
        }
    }

    private async Task<bool> RollBackFileRenamesAsync(
        Audiobook audiobook,
        IReadOnlyList<FileRenameResultItem> completedItems,
        AudiobookPathRollbackState rollbackState,
        IReadOnlyCollection<string> allowedRoots,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken)
    {
        var rollbackSucceeded = true;
        foreach (var item in completedItems.Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!item.Success
                || string.IsNullOrWhiteSpace(item.PreviousPath)
                || string.IsNullOrWhiteSpace(item.NewPath))
            {
                continue;
            }

            try
            {
                if (!PathsEqual(item.PreviousPath, item.NewPath, semantics))
                {
                    if (!_fileSystem.FileExists(item.NewPath))
                    {
                        rollbackSucceeded = false;
                        item.Error = "Rollback failed because the moved file could not be found.";
                        continue;
                    }

                    if (!_fileSystem.TryValidateMutationTarget(
                            item.NewPath,
                            allowedRoots,
                            out var rollbackSource,
                            out _)
                        || !_fileSystem.TryValidateMutationTarget(
                            item.PreviousPath,
                            allowedRoots,
                            out var rollbackDestination,
                            out _))
                    {
                        rollbackSucceeded = false;
                        item.Error = "Rollback paths could not be resolved safely within the allowed library roots.";
                        continue;
                    }

                    var parent = Path.GetDirectoryName(rollbackDestination);
                    if (!string.IsNullOrWhiteSpace(parent))
                    {
                        await EnsureOwnedRenameHierarchyAsync(
                            parent,
                            allowedRoots,
                            semantics,
                            audiobook.Id,
                            Guid.NewGuid(),
                            cancellationToken);
                    }

                    // Compensation is also owner-bound and startup-discoverable. A fresh ID
                    // keeps completed compensation history from colliding with a later retry.
                    var rollbackOperationId = Guid.NewGuid();
                    item.RollbackOperationId = rollbackOperationId;
                    bool moved;
                    if (item.FileId == 0)
                    {
                        moved = await _fileMover.PerformActionOn(
                            FileAction.Move,
                            rollbackSource,
                            rollbackDestination,
                            rollbackOperationId,
                            audiobook.Id,
                            audiobookFileId: 0);
                    }
                    else
                    {
                        var trackedFile = audiobook.Files?.FirstOrDefault(
                            candidate => candidate.Id == item.FileId);
                        if (string.IsNullOrWhiteSpace(
                                trackedFile?.PhysicalObjectIdentity))
                        {
                            rollbackSucceeded = false;
                            item.Error =
                                "Rollback could not prove the tracked file generation.";
                            continue;
                        }

                        moved = await _fileMover
                            .MoveFilePreservingPhysicalIdentityAsync(
                                rollbackSource,
                                rollbackDestination,
                                trackedFile.PhysicalObjectIdentity,
                                rollbackOperationId,
                                audiobook.Id,
                                item.FileId);
                    }
                    if (!moved)
                    {
                        rollbackSucceeded = false;
                        item.Error = "Rollback file move failed.";
                        continue;
                    }
                }

                if (item.FileId == 0)
                {
                    audiobook.FilePath = rollbackState.LegacyFilePath;
                }
                else
                {
                    var file = audiobook.Files?.FirstOrDefault(candidate => candidate.Id == item.FileId);
                    if (file == null
                        || !rollbackState.FileStates.TryGetValue(item.FileId, out var fileState))
                    {
                        rollbackSucceeded = false;
                        item.Error = "Rollback could not restore the tracked audiobook file state.";
                        continue;
                    }

                    file.RestorePathState(fileState);
                }

                item.Success = false;
                item.RolledBack = true;
                item.Error = "The file move was rolled back because the organize operation did not complete.";
            }
            catch (Exception exception) when (exception is not OperationCanceledException
                && exception is not OutOfMemoryException
                && exception is not StackOverflowException)
            {
                rollbackSucceeded = false;
                item.Error = "Rollback failed.";
                _logger.LogError(
                    exception,
                    "Failed to roll back organize operation for audiobook {AudiobookId}, file {FileId}",
                    audiobook.Id,
                    item.FileId);
            }
        }

        if (rollbackSucceeded)
        {
            RestoreAudiobookPathState(audiobook, rollbackState);
        }
        else
        {
            // The caller persists this actual partial state together with the
            // terminal state of every journal whose rollback was proven.
            UpdateAudiobookPathSummary(audiobook, null, semantics);
        }

        return rollbackSucceeded;
    }

    private sealed record AudiobookPathRollbackState(
        string? BasePath,
        string? LegacyFilePath,
        long? FileSize,
        IReadOnlyDictionary<int, AudiobookFilePathState> FileStates);
}
