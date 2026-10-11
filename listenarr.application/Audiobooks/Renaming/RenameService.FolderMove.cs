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
    /// When every tracked file of the book moves from its folder to the same folder (new or unchanged),
    /// renames the files instead of copying them. They keep their physical identity (no copy), and the
    /// other top-level files (companions) follow the book. The folder itself is never renamed: on
    /// rclone/pCloud WebDAV a renamed folder is listed again before the remote applies the move and stays
    /// empty in the mount cache, while file renames keep the cache coherent. The emptied previous folder
    /// is removed. Returns false, with the filesystem unchanged, when the shortcut does not apply or fails;
    /// the caller then runs the regular per-file organize.
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
            || string.IsNullOrWhiteSpace(newFolder))
        {
            return false;
        }

        var sameFolder = PathsEqual(oldFolder, newFolder, semantics);
        if (!IsPathWithinAllowedRoots(newFolder, allowedRoots, semantics)
            || !_fileSystem.TryValidateMutationTarget(oldFolder, allowedRoots, out _, out _)
            || !_fileSystem.TryValidateMutationTarget(newFolder, allowedRoots, out _, out _)
            || !_fileSystem.DirectoryExists(oldFolder)
            || _fileSystem.EnumerateDirectories(oldFolder).Any()
            || (!sameFolder
                && (FileSystemPathIdentity.IsSameOrInside(newFolder, oldFolder, semantics)
                    || FileSystemPathIdentity.IsSameOrInside(oldFolder, newFolder, semantics)
                    || _fileSystem.DirectoryExists(newFolder)
                    || _fileSystem.FileExists(newFolder))))
        {
            return false;
        }

        var plan = new List<(FileRenameOperation Operation, AudiobookFile? DbFile, string Source, string Target)>();
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

            plan.Add((fileOperation, dbFile, source, target));
        }

        if (plan.Select(step => step.Target).Distinct(StringComparer.Ordinal).Count() != plan.Count)
        {
            return false;
        }

        var moved = new List<(string From, string To)>();
        var createdFolder = false;
        var removedOldFolder = false;
        try
        {
            if (!sameFolder)
            {
                _fileSystem.CreateDirectory(newFolder);
                createdFolder = true;
            }

            foreach (var step in plan.Where(step => !PathsEqual(step.Source, step.Target, semantics)))
            {
                if (_fileSystem.FileExists(step.Target))
                {
                    throw new IOException($"Organize target already exists: {step.Target}");
                }

                _fileSystem.MoveFile(step.Source, step.Target);
                moved.Add((step.Source, step.Target));
            }

            if (!sameFolder)
            {
                // Companions follow the book, as they would with a folder rename.
                foreach (var file in _fileSystem.EnumerateFiles(oldFolder, "*", SearchOption.TopDirectoryOnly).ToList())
                {
                    var destination = NormalizePath(Path.Join(newFolder, Path.GetFileName(file)));
                    if (!_fileSystem.FileExists(destination))
                    {
                        _fileSystem.MoveFile(file, destination);
                        moved.Add((NormalizePath(file), destination));
                    }
                }

                if (!_fileSystem.EnumerateFiles(oldFolder, "*", SearchOption.TopDirectoryOnly).Any()
                    && !_fileSystem.EnumerateDirectories(oldFolder).Any())
                {
                    _fileSystem.DeleteDirectory(oldFolder, recursive: false);
                    removedOldFolder = true;
                }
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
                "Organized audiobook {AudiobookId} by renaming its files from {OldFolder} to {NewFolder}",
                audiobook.Id,
                oldFolder,
                newFolder);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Folder organize of audiobook {AudiobookId} by renames failed; reverting and organizing file by file", audiobook.Id);
            try
            {
                if (removedOldFolder)
                {
                    _fileSystem.CreateDirectory(oldFolder);
                }

                for (var index = moved.Count - 1; index >= 0; index--)
                {
                    _fileSystem.MoveFile(moved[index].To, moved[index].From);
                }

                if (createdFolder
                    && !_fileSystem.EnumerateFiles(newFolder, "*", SearchOption.TopDirectoryOnly).Any()
                    && !_fileSystem.EnumerateDirectories(newFolder).Any())
                {
                    _fileSystem.DeleteDirectory(newFolder, recursive: false);
                }
            }
            catch (Exception revertException) when (revertException is IOException or UnauthorizedAccessException)
            {
                _logger.LogCritical(revertException, "Could not revert folder organize of audiobook {AudiobookId}", audiobook.Id);
                throw;
            }

            result.RenamedFiles.Clear();
            return false;
        }
    }
}
