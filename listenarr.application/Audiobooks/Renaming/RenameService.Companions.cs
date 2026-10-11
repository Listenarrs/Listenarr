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
    /// Carries the non-audio companions (cover.jpg, metadata.json, ...) left in the previous
    /// folder after a folder-changing organize, mirroring what manual import already does.
    /// Runs only when every tracked file left the previous folder, honours the import
    /// blacklist, never overwrites, and removes the previous folder once it is empty.
    /// </summary>
    private async Task MoveCompanionFilesAfterRenameAsync(
        string? previousFolder,
        string? newFolder,
        ApplicationSettings settings,
        IReadOnlyCollection<string> allowedRoots,
        FileSystemPathSemantics semantics,
        RenameResult result)
    {
        if (result.RenamedFiles.Any(file => !file.Success || file.SourceRetained))
        {
            return;
        }

        var previous = NormalizePath(previousFolder);
        var next = NormalizePath(newFolder);
        if (string.IsNullOrWhiteSpace(previous)
            || string.IsNullOrWhiteSpace(next)
            || PathsEqual(previous, next, semantics)
            || !_fileSystem.DirectoryExists(previous)
            || !_fileSystem.DirectoryExists(next))
        {
            return;
        }

        List<string> companions;
        try
        {
            companions = _fileSystem
                .EnumerateFiles(previous, "*", SearchOption.TopDirectoryOnly)
                .Where(file => !FileUtils.IsAudioFile(file))
                .Where(file => !Path.GetFileName(file).StartsWith('.'))
                .Where(file => !FileUtils.IsBlacklistedFile(file, settings.ImportBlacklistExtensions))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not list companion files left in {Folder} after organize", previous);
            return;
        }

        foreach (var companion in companions)
        {
            var destination = Path.Combine(next, Path.GetFileName(companion));
            if (!IsPathWithinAllowedRoots(companion, allowedRoots, semantics)
                || !IsPathWithinAllowedRoots(destination, allowedRoots, semantics)
                || !_fileSystem.TryValidateMutationTarget(companion, allowedRoots, out var source, out _)
                || !_fileSystem.TryValidateMutationTarget(destination, allowedRoots, out var target, out _))
            {
                _logger.LogWarning("Skipping companion file {FilePath}: outside the allowed library roots", companion);
                continue;
            }

            if (_fileSystem.FileExists(target))
            {
                _logger.LogInformation("Keeping companion file {FilePath} in place: {Destination} already exists", source, target);
                continue;
            }

            try
            {
                if (!await _fileMover.PerformActionOn(FileAction.Move, source, target, Guid.NewGuid()))
                {
                    _logger.LogWarning("Companion file {FilePath} was not moved to {Destination}", source, target);
                }
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                _logger.LogWarning(ex, "Failed to move companion file {FilePath} to {Destination}", source, target);
            }
        }

        try
        {
            if (_fileSystem.DirectoryExists(previous) && !_fileSystem.EnumerateFileSystemEntries(previous).Any())
            {
                _fileSystem.DeleteDirectory(previous, recursive: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not remove empty previous folder {Folder} after organize", previous);
        }
    }
}
