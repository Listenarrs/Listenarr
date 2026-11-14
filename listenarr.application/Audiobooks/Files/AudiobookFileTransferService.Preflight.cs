using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Files;

public sealed partial class AudiobookFileTransferService
{
    /// <summary>A file that passed every ownership check and may be re-homed.</summary>
    private sealed record PreparedTransfer(
        int Index,
        AudiobookFile File,
        string AbsolutePath,
        AudiobookFilePathIdentity Identity);

    /// <summary>
    /// What is known about the destination before anything changes. <see cref="AllowedRoot"/>
    /// is the library root organize will confine the destination audiobook to, and
    /// <see cref="Folder"/> is its own folder; either is null when it is only known once
    /// the destination owns files (a record with no folder, or one parked at a library root).
    /// </summary>
    private sealed record DestinationPlan(
        FileSystemPathSemantics Semantics,
        string? AllowedRoot,
        string? Folder,
        IReadOnlyList<AudiobookFile> ExistingFiles);

    /// <summary>
    /// Reads the destination the way organize will see it, so a file organize would
    /// refuse to place is turned away while it still belongs to the source.
    /// </summary>
    private async Task<DestinationPlan?> PlanDestinationAsync(
        Audiobook target,
        CancellationToken cancellationToken)
    {
        RenamePreview? preview;
        try
        {
            preview = (await renameService.PreviewRenameAsync([target.Id], cancellationToken))
                .FirstOrDefault();
        }
        catch (InvalidOperationException)
        {
            // Relocation reports this once it has files to plan for.
            return null;
        }

        if (preview?.CurrentFolderSemantics == null)
        {
            return null;
        }

        var semantics = new FileSystemPathSemantics(
            preview.CurrentFolderSemantics.Syntax,
            preview.CurrentFolderSemantics.CaseSensitivity);
        var current = preview.CurrentFolderPath;
        if (string.IsNullOrWhiteSpace(target.BasePath) || string.IsNullOrWhiteSpace(current))
        {
            // Organize derives a folderless record's root from the files it owns, so
            // the transferred files define the root rather than conflict with it.
            return new DestinationPlan(semantics, null, null, []);
        }

        var allowedRoot = (await GetOrganizeRootsAsync())
            .Where(root => IsSameOrInside(current, root, semantics))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault() ?? current;
        var folder = await IsLibraryRootAsync(current, semantics) ? null : current;
        var existingFiles = folder == null
            ? []
            : await audiobookFileRepository.GetByAudiobookIdAsync(target.Id, cancellationToken);
        return new DestinationPlan(semantics, allowedRoot, folder, existingFiles);
    }

    /// <summary>
    /// Rejects a file that could not end up in the destination's folder. Deciding this
    /// before ownership changes keeps the file with its source instead of leaving the
    /// destination owning audio outside its own folder.
    /// </summary>
    private AudiobookFileTransferItem? CheckPlacement(
        PreparedTransfer prepared,
        DestinationPlan? plan,
        List<string> plannedDestinations)
    {
        if (!fileSystem.FileExists(prepared.AbsolutePath))
        {
            return Rejected(
                prepared.File,
                AudiobookFileTransferOutcome.MissingOnDisk,
                "The file is missing on disk; rescan the source audiobook before transferring it.");
        }

        if (plan == null)
        {
            return null;
        }

        if (plan.AllowedRoot != null
            && !IsSameOrInside(prepared.AbsolutePath, plan.AllowedRoot, plan.Semantics))
        {
            return Rejected(
                prepared.File,
                AudiobookFileTransferOutcome.OutsideDestinationRoot,
                "The file is under a different library root than the destination audiobook, so it cannot be moved into the destination folder. Move the source audiobook to the destination's root folder first.");
        }

        if (plan.Folder == null)
        {
            return null;
        }

        var destination = FileUtils.NormalizeStoredPath(
            Path.Join(plan.Folder, Path.GetFileName(prepared.AbsolutePath)));
        if (PathsEqual(prepared.AbsolutePath, destination, plan.Semantics))
        {
            return null;
        }

        var destinationTaken = fileSystem.FileExists(destination)
            || plannedDestinations.Any(path => PathsEqual(path, destination, plan.Semantics))
            || plan.ExistingFiles.Any(file => !string.IsNullOrWhiteSpace(file.Path)
                && PathsEqual(file.Path, destination, plan.Semantics));
        if (destinationTaken)
        {
            return Rejected(
                prepared.File,
                AudiobookFileTransferOutcome.DestinationOccupied,
                "A file with the same name already exists in the destination folder, so the file was not transferred (delete the redundant copy if it is a duplicate).");
        }

        plannedDestinations.Add(destination);
        return null;
    }

    // The roots organize authorizes against: configured root folders, or the output
    // path while none are configured.
    private async Task<List<string>> GetOrganizeRootsAsync()
    {
        var roots = (await rootFolderService.GetAllAsync())
            .Select(root => root.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();
        if (roots.Count == 0)
        {
            var settings = await configurationService.GetApplicationSettingsAsync();
            if (!string.IsNullOrWhiteSpace(settings.OutputPath))
            {
                roots.Add(settings.OutputPath);
            }
        }

        return roots;
    }

    private static bool IsSameOrInside(
        string? path,
        string? boundary,
        FileSystemPathSemantics semantics)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(boundary))
        {
            return false;
        }

        try
        {
            return FileSystemPathIdentity.IsSameOrInside(
                FileUtils.NormalizeStoredPath(path),
                FileUtils.NormalizeStoredPath(boundary),
                semantics);
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
