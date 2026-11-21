using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

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
    /// is the library root organize will confine the destination audiobook to. A record
    /// that owns nothing and has no folder gets its root from the first file it accepts.
    /// <see cref="Folder"/> is the destination's own folder, or null when the files will
    /// be placed in the canonical folder organize proposes once the destination owns them.
    /// </summary>
    private sealed class DestinationPlan(
        FileSystemPathSemantics semantics,
        string? allowedRoot,
        string? folder,
        IReadOnlyList<AudiobookFile> existingFiles,
        IReadOnlyList<string> organizeRoots)
    {
        public FileSystemPathSemantics Semantics { get; } = semantics;
        public string? AllowedRoot { get; set; } = allowedRoot;
        public string? Folder { get; } = folder;
        public IReadOnlyList<AudiobookFile> ExistingFiles { get; } = existingFiles;
        public IReadOnlyList<string> OrganizeRoots { get; } = organizeRoots;
    }

    /// <summary>
    /// Reads the destination the way organize will see it, so a file organize would
    /// refuse to place is turned away while it still belongs to the source. Returns
    /// null when the destination cannot be resolved; nothing is transferred then.
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
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(
                exception,
                "Audiobook {TargetId}'s folder could not be resolved before a file transfer",
                target.Id);
            return null;
        }

        if (preview?.CurrentFolderSemantics == null)
        {
            return null;
        }

        var semantics = new FileSystemPathSemantics(
            preview.CurrentFolderSemantics.Syntax,
            preview.CurrentFolderSemantics.CaseSensitivity);
        var organizeRoots = await GetOrganizeRootsAsync();
        // The preview resolves a record without a saved folder from the files it owns,
        // so a missing BasePath alone does not make the destination folderless.
        var current = preview.CurrentFolderPath;
        if (string.IsNullOrWhiteSpace(current))
        {
            return new DestinationPlan(semantics, null, null, [], organizeRoots);
        }

        var allowedRoot = ContainingRoot(current, organizeRoots, semantics) ?? current;
        var existingFiles = await audiobookFileRepository.GetByAudiobookIdAsync(target.Id, cancellationToken);
        var ownsFolder = !await IsLibraryRootAsync(current, semantics)
            && (!string.IsNullOrWhiteSpace(target.BasePath)
                || existingFiles.All(file => string.IsNullOrWhiteSpace(file.Path)
                    || PathsEqual(Path.GetDirectoryName(file.Path), current, semantics)));
        return new DestinationPlan(
            semantics,
            allowedRoot,
            ownsFolder ? current : null,
            ownsFolder ? existingFiles : [],
            organizeRoots);
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
            return Rejected(
                prepared.File,
                AudiobookFileTransferOutcome.DestinationUnavailable,
                "The destination audiobook's folder could not be resolved, so the file was not transferred. Rescan the destination audiobook first.");
        }

        // A destination with nothing of its own takes its root from the first file, so
        // files from two roots can never widen its folder to their common parent.
        plan.AllowedRoot ??= ContainingRoot(prepared.AbsolutePath, plan.OrganizeRoots, plan.Semantics)
            ?? Path.GetDirectoryName(prepared.AbsolutePath);
        if (!IsSameOrInside(prepared.AbsolutePath, plan.AllowedRoot, plan.Semantics))
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
            || fileSystem.DirectoryExists(destination)
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

    private static string? ContainingRoot(
        string path,
        IReadOnlyList<string> roots,
        FileSystemPathSemantics semantics) =>
        roots
            .Where(root => IsSameOrInside(path, root, semantics))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault();

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
