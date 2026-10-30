using Listenarr.Application.Common.Exceptions;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Files;

public sealed partial class AudiobookFileTransferService
{
    /// <summary>
    /// Moves the re-homed files into the destination's folder as one organize
    /// batch of the destination audiobook. File names are preserved; a file whose
    /// destination is already taken, or that is missing on disk, keeps its new
    /// owner and stays in place with a reason.
    /// </summary>
    private async Task<(List<AudiobookFileTransferItem> Items, string? Error)> RelocateAsync(
        Audiobook target,
        List<AudiobookFileTransferItem> items,
        CancellationToken cancellationToken)
    {
        RenamePreview? preview;
        try
        {
            preview = (await renameService.PreviewRenameAsync([target.Id], cancellationToken))
                .FirstOrDefault();
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(
                exception,
                "Transferred files could not be planned into audiobook {TargetId}'s folder",
                target.Id);
            return (items, exception.Message);
        }

        if (preview?.CurrentFolderSemantics == null)
        {
            return (items, "The destination audiobook's folder could not be resolved on this host.");
        }

        var semantics = new FileSystemPathSemantics(
            preview.CurrentFolderSemantics.Syntax,
            preview.CurrentFolderSemantics.CaseSensitivity);
        var destinationFolder = await ResolveDestinationFolderAsync(
            target,
            preview,
            items,
            semantics);
        if (string.IsNullOrWhiteSpace(destinationFolder))
        {
            return (items, "The destination audiobook has no folder of its own; organize it once it has one.");
        }

        var targetFiles = await audiobookFileRepository.GetByAudiobookIdAsync(
            target.Id,
            cancellationToken);
        var renames = new List<FileRenameOperation>();
        var plannedDestinations = new List<string>();
        var planned = new List<AudiobookFileTransferItem>(items.Count);
        foreach (var item in items)
        {
            if (!item.Transferred || string.IsNullOrWhiteSpace(item.Path))
            {
                planned.Add(item);
                continue;
            }

            var destination = FileUtils.NormalizeStoredPath(
                Path.Join(destinationFolder, Path.GetFileName(item.Path)));
            if (PathsEqual(item.Path, destination, semantics))
            {
                planned.Add(item);
                continue;
            }

            if (!fileSystem.FileExists(item.Path))
            {
                planned.Add(item with
                {
                    Reason = "The file is missing on disk, so it was left in place."
                });
                continue;
            }

            var destinationTaken = fileSystem.FileExists(destination)
                || plannedDestinations.Any(path => PathsEqual(path, destination, semantics))
                || targetFiles.Any(file => file.Id != item.FileId
                    && !string.IsNullOrWhiteSpace(file.Path)
                    && PathsEqual(file.Path, destination, semantics));
            if (destinationTaken)
            {
                planned.Add(item with
                {
                    Reason = "A file with the same name already exists in the destination folder; the file was left in place (delete the redundant copy if it is a duplicate)."
                });
                continue;
            }

            plannedDestinations.Add(destination);
            renames.Add(new FileRenameOperation
            {
                FileId = item.FileId,
                CurrentPath = item.Path,
                NewPath = destination
            });
            planned.Add(item);
        }

        if (renames.Count == 0)
        {
            return (planned, null);
        }

        RenameResult? result;
        try
        {
            result = (await renameService.ExecuteRenameAsync(
                [
                    new RenameOperation
                    {
                        AudiobookId = target.Id,
                        CurrentFolderPath = preview.CurrentFolderPath,
                        CurrentFolderSemantics = preview.CurrentFolderSemantics,
                        FileRenames = renames
                    }
                ],
                cancellationToken)).FirstOrDefault();
        }
        catch (ApplicationConflictException exception)
        {
            logger.LogWarning(
                exception,
                "Transferred files could not be moved into audiobook {TargetId}'s folder",
                target.Id);
            return (planned, exception.SafeDetail);
        }

        if (result == null)
        {
            return (planned, "The organize service returned no result for the destination audiobook.");
        }

        var renamed = result.RenamedFiles.ToDictionary(file => file.FileId);
        var renamedIds = renames.Select(rename => rename.FileId).ToHashSet();
        var completed = planned
            .Select(item =>
            {
                if (!renamedIds.Contains(item.FileId))
                {
                    return item;
                }

                if (result.Success
                    && renamed.TryGetValue(item.FileId, out var moved)
                    && moved.Success
                    && !string.IsNullOrWhiteSpace(moved.NewPath))
                {
                    return item with { Path = moved.NewPath, Relocated = true };
                }

                var error = renamed.TryGetValue(item.FileId, out var failed) ? failed.Error : null;
                return item with
                {
                    Reason = error
                        ?? result.Error
                        ?? "The file could not be moved into the destination folder and was left in place."
                };
            })
            .ToList();
        if (!result.Success)
        {
            logger.LogWarning(
                "Transferred files were not moved into audiobook {TargetId}'s folder: {Error}",
                target.Id,
                result.Error);
        }

        return (completed, result.Success ? null : result.Error);
    }

    // The destination's own folder when it has one; otherwise the canonical folder
    // the organize planner proposes for the transferred files. A record parked at a
    // library root must never receive files there.
    private async Task<string?> ResolveDestinationFolderAsync(
        Audiobook target,
        RenamePreview preview,
        IReadOnlyList<AudiobookFileTransferItem> items,
        FileSystemPathSemantics semantics)
    {
        var current = preview.CurrentFolderPath;
        if (!string.IsNullOrWhiteSpace(target.BasePath)
            && !string.IsNullOrWhiteSpace(current)
            && !await IsLibraryRootAsync(current, semantics))
        {
            return current;
        }

        var transferredIds = items
            .Where(item => item.Transferred)
            .Select(item => item.FileId)
            .ToHashSet();
        var proposed = preview.FileRenames.FirstOrDefault(file =>
            transferredIds.Contains(file.FileId)
            && !string.IsNullOrWhiteSpace(file.NewPath));
        return proposed?.NewPath == null
            ? null
            : Path.GetDirectoryName(proposed.NewPath);
    }

    private async Task<bool> IsLibraryRootAsync(
        string path,
        FileSystemPathSemantics semantics)
    {
        var roots = (await rootFolderService.GetAllAsync())
            .Select(root => root.Path)
            .ToList();
        var settings = await configurationService.GetApplicationSettingsAsync();
        roots.Add(settings.OutputPath);
        return roots.Any(root => PathsEqual(path, root, semantics));
    }

    private static bool PathsEqual(
        string? left,
        string? right,
        FileSystemPathSemantics semantics)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return FileSystemPathIdentity.AreEquivalent(
                FileUtils.NormalizeStoredPath(left),
                FileUtils.NormalizeStoredPath(right),
                semantics);
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
