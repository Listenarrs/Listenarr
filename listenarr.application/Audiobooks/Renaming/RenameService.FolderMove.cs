/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Renaming;

public partial class RenameService
{
    /// <summary>
    /// Opt-in (LISTENARR_ORGANIZE_MOVE_WHOLE_FOLDER=true): intended for storage where the verified per-file
    /// protocol can only copy (FUSE/cloud mounts), which changes every file's identity and is slow.
    /// </summary>
    internal static bool MoveWholeFolderEnabled { get; set; } =
        bool.TryParse(Environment.GetEnvironmentVariable("LISTENARR_ORGANIZE_MOVE_WHOLE_FOLDER"), out var enabled) && enabled;

    /// <summary>
    /// When every tracked file of the book moves from its folder to the same new folder, renames the
    /// folder itself and then renames files in place. Files keep their physical identity (no copy), so
    /// external tools tracking the folder (Audiobookshelf) see a rename, and companions follow the book.
    /// Returns false, with the filesystem unchanged, when the shortcut does not apply or fails; the
    /// caller then runs the regular per-file organize.
    /// </summary>
    private async Task<bool> TryMoveWholeFolderAsync(
        Audiobook audiobook,
        RenameOperation operation,
        string currentBasePath,
        IReadOnlyCollection<string> allowedRoots,
        FileSystemPathSemantics semantics,
        RenameResult result,
        CancellationToken cancellationToken)
    {
        if (!MoveWholeFolderEnabled)
        {
            return false;
        }

        var oldFolder = NormalizePath(currentBasePath);
        var newFolder = NormalizePath(operation.NewFolderPath);
        var fileOperations = operation.FileRenames ?? new List<FileRenameOperation>();
        if (fileOperations.Count == 0
            || string.IsNullOrWhiteSpace(oldFolder)
            || string.IsNullOrWhiteSpace(newFolder)
            || PathsEqual(oldFolder, newFolder, semantics)
            || FileSystemPathIdentity.IsSameOrInside(newFolder, oldFolder, semantics)
            || FileSystemPathIdentity.IsSameOrInside(oldFolder, newFolder, semantics)
            || !IsPathWithinAllowedRoots(newFolder, allowedRoots, semantics)
            || !_fileSystem.TryValidateMutationTarget(oldFolder, allowedRoots, out _, out _)
            || !_fileSystem.TryValidateMutationTarget(newFolder, allowedRoots, out _, out _)
            || !_fileSystem.DirectoryExists(oldFolder)
            || _fileSystem.DirectoryExists(newFolder)
            || _fileSystem.FileExists(newFolder)
            || _fileSystem.EnumerateDirectories(oldFolder).Any())
        {
            return false;
        }

        var plan = new List<(FileRenameOperation Operation, AudiobookFile? DbFile, string Moved, string Target)>();
        foreach (var fileOperation in fileOperations)
        {
            var source = NormalizePath(fileOperation.CurrentPath);
            var target = NormalizePath(fileOperation.NewPath);
            var trackedSource = ResolveTrackedSourcePath(audiobook, fileOperation, semantics, out var dbFile, out var error);
            if (error != null
                || !PathsEqual(source, trackedSource, semantics)
                || !PathsEqual(NormalizePath(Path.GetDirectoryName(source)), oldFolder, semantics)
                || !PathsEqual(NormalizePath(Path.GetDirectoryName(target)), newFolder, semantics)
                || !_fileSystem.FileExists(source))
            {
                return false;
            }

            plan.Add((fileOperation, dbFile, NormalizePath(Path.Join(newFolder, Path.GetFileName(source))), target));
        }

        var renamedInside = new List<(string From, string To)>();
        var folderMoved = false;
        try
        {
            var parent = Path.GetDirectoryName(newFolder);
            if (!string.IsNullOrWhiteSpace(parent) && !_fileSystem.DirectoryExists(parent))
            {
                _fileSystem.CreateDirectory(parent);
            }

            _fileSystem.MoveDirectory(oldFolder, newFolder);
            folderMoved = true;
            foreach (var step in plan.Where(step => !PathsEqual(step.Moved, step.Target, semantics)))
            {
                if (_fileSystem.FileExists(step.Target))
                {
                    throw new IOException($"Organize target already exists: {step.Target}");
                }

                _fileSystem.MoveFile(step.Moved, step.Target);
                renamedInside.Add((step.Moved, step.Target));
            }

            var identities = new List<AudiobookFilePathIdentity>();
            foreach (var step in plan)
            {
                var identity = await _filePathIdentityResolver.ResolveAsync(audiobook, step.Target, cancellationToken);
                if (identity.State != PathIdentityState.Valid)
                {
                    throw new IOException($"Destination filesystem identity is unavailable: {step.Target}");
                }

                identities.Add(identity);
            }

            for (var index = 0; index < plan.Count; index++)
            {
                var step = plan[index];
                if (step.DbFile != null)
                {
                    step.DbFile.ApplyPathIdentity(step.Target, identities[index]);
                    step.DbFile.ClearPhysicalObjectIdentity();
                }
                else if (step.Operation.FileId == 0 && !string.IsNullOrWhiteSpace(audiobook.FilePath))
                {
                    audiobook.FilePath = step.Target;
                }

                result.RenamedFiles.Add(new FileRenameResultItem
                {
                    FileId = step.Operation.FileId,
                    PreviousPath = NormalizePath(step.Operation.CurrentPath),
                    NewPath = step.Target,
                    Success = true
                });
            }

            _logger.LogInformation(
                "Organized audiobook {AudiobookId} by moving its folder {OldFolder} to {NewFolder}",
                audiobook.Id,
                oldFolder,
                newFolder);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Whole-folder organize of audiobook {AudiobookId} failed; reverting and organizing file by file", audiobook.Id);
            try
            {
                for (var index = renamedInside.Count - 1; index >= 0; index--)
                {
                    _fileSystem.MoveFile(renamedInside[index].To, renamedInside[index].From);
                }

                if (folderMoved)
                {
                    _fileSystem.MoveDirectory(newFolder, oldFolder);
                }
            }
            catch (Exception revertException) when (revertException is IOException or UnauthorizedAccessException)
            {
                _logger.LogCritical(revertException, "Could not revert whole-folder organize of audiobook {AudiobookId}", audiobook.Id);
                throw;
            }

            result.RenamedFiles.Clear();
            return false;
        }
    }
}
