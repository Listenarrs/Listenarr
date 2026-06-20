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

namespace Listenarr.Application.Audiobooks.Renaming
{
    public partial class RenameService : IRenameService
    {
        private const int MaxAudiobookIds = 500;

        private readonly IConfigurationService _configService;
        private readonly IFileNamingService _fileNamingService;
        private readonly IFileMover _fileMover;
        private readonly IAudiobookRepository _audiobookRepository;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger<RenameService> _logger;
        private readonly IRootFolderService? _rootFolderService;
        private readonly IHistoryRepository? _historyRepository;

        public RenameService(
            IConfigurationService configService,
            IFileNamingService fileNamingService,
            IFileMover fileMover,
            IAudiobookRepository audiobookRepository,
            IFileSystem fileSystem,
            ILogger<RenameService> logger,
            IRootFolderService? rootFolderService = null,
            IHistoryRepository? historyRepository = null)
        {
            _configService = configService;
            _fileNamingService = fileNamingService;
            _fileMover = fileMover;
            _audiobookRepository = audiobookRepository;
            _fileSystem = fileSystem;
            _logger = logger;
            _rootFolderService = rootFolderService;
            _historyRepository = historyRepository;
        }

        public async Task<List<RenamePreview>> PreviewRenameAsync(int[] audiobookIds, CancellationToken ct = default)
        {
            if (audiobookIds == null || audiobookIds.Length == 0) return new();
            if (audiobookIds.Length > MaxAudiobookIds) throw new ArgumentException($"Cannot preview more than {MaxAudiobookIds} audiobooks at once.");

            var settings = await _configService.GetApplicationSettingsAsync();
            var rootFolders = await LoadRootFoldersAsync();

            var audiobooks = await _audiobookRepository.GetByIdsWithFilesAsync(audiobookIds, ct);

            return audiobooks.Select(a => BuildPreview(a, settings, rootFolders)).ToList();
        }

        public async Task<List<RenameResult>> ExecuteRenameAsync(List<RenameOperation> operations, CancellationToken ct = default)
        {
            if (operations == null || operations.Count == 0) return new();
            if (operations.Count > MaxAudiobookIds) throw new ArgumentException($"Cannot execute more than {MaxAudiobookIds} rename operations at once.");

            var settings = await _configService.GetApplicationSettingsAsync();
            var rootFolders = await LoadRootFoldersAsync();
            var results = new List<RenameResult>();
            foreach (var op in operations) results.Add(await ExecuteSingleAsync(op, settings, rootFolders, ct));
            return results;
        }

        private RenamePreview BuildPreview(Audiobook audiobook, ApplicationSettings settings, List<RootFolder> rootFolders)
        {
            var preview = new RenamePreview
            {
                AudiobookId = audiobook.Id,
                AudiobookTitle = audiobook.Title,
                CurrentFolderPath = ComputeCurrentBasePath(audiobook)
            };

            var files = GetFileEntries(audiobook);
            if (files.Count == 0)
            {
                preview.NewFolderPath = preview.CurrentFolderPath;
                return preview;
            }

            var namingBase = ResolveNamingBasePath(preview.CurrentFolderPath, settings, rootFolders);
            var isMultiFile = files.Count > 1;
            var expectedPaths = new List<string>();

            foreach (var file in files)
            {
                var expectedPath = BuildExpectedPath(audiobook, file, settings, namingBase.BasePath, namingBase.IsCustomBasePath, isMultiFile);
                expectedPaths.Add(expectedPath);
                preview.FileRenames.Add(new FileRenamePreview
                {
                    FileId = file.FileId,
                    CurrentPath = file.CurrentPath,
                    NewPath = expectedPath,
                    CurrentFilename = Path.GetFileName(file.CurrentPath),
                    NewFilename = Path.GetFileName(expectedPath),
                    Changed = !PathsEqual(file.CurrentPath, expectedPath)
                });
            }

            preview.NewFolderPath = ComputeCommonBasePath(expectedPaths);
            preview.FolderChanged = !PathsEqual(preview.CurrentFolderPath, preview.NewFolderPath);
            preview.HasChanges = preview.FolderChanged || preview.FileRenames.Any(f => f.Changed);
            return preview;
        }

        private async Task<RenameResult> ExecuteSingleAsync(RenameOperation operation, ApplicationSettings settings, List<RootFolder> rootFolders, CancellationToken ct)
        {
            var result = new RenameResult { AudiobookId = operation.AudiobookId };
            try
            {
                var audiobook = await _audiobookRepository.GetByIdAsync(operation.AudiobookId);
                if (audiobook == null)
                {
                    result.Success = false;
                    result.Error = "Audiobook not found.";
                    return result;
                }

                var currentBasePath = ComputeCurrentBasePath(audiobook);
                var allowedRoots = BuildAllowedRoots(settings, rootFolders, currentBasePath);
                var anySucceeded = false;
                var hasFileOperations = operation.FileRenames != null && operation.FileRenames.Count > 0;

                foreach (var fileOp in operation.FileRenames ?? new())
                {
                    var fileResult = await ExecuteFileRenameAsync(audiobook, fileOp, allowedRoots);
                    result.RenamedFiles.Add(fileResult);
                    anySucceeded |= fileResult.Success;
                }

                if ((operation.FileRenames == null || operation.FileRenames.Count == 0)
                    && !string.IsNullOrWhiteSpace(operation.NewFolderPath)
                    && !PathsEqual(currentBasePath, operation.NewFolderPath))
                {
                    var dirMove = await ExecuteDirectoryMoveAsync(audiobook, operation.NewFolderPath!, allowedRoots);
                    result.Success = dirMove.Success;
                    result.Error = dirMove.Error;
                    anySucceeded |= dirMove.Success;
                }

                if (result.RenamedFiles.Count > 0)
                {
                    result.Success = result.RenamedFiles.All(f => f.Success);
                    if (!result.Success && string.IsNullOrWhiteSpace(result.Error))
                    {
                        result.Error = "One or more file organize operations failed.";
                    }
                }
                else if (!result.Success)
                {
                    result.Success = anySucceeded;
                }

                if (anySucceeded)
                {
                    var shouldTrustRequestedBasePath = !string.IsNullOrWhiteSpace(operation.NewFolderPath)
                        && (!hasFileOperations || result.Success);
                    UpdateAudiobookPathSummary(audiobook, shouldTrustRequestedBasePath ? operation.NewFolderPath : null);
                    await _audiobookRepository.SaveChangesAsync(ct);
                    await AddHistoryAsync(audiobook, result);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to execute organize operation for audiobook {AudiobookId}", operation.AudiobookId);
                result.Success = false;
                result.Error = ex.Message;
            }

            return result;
        }

        private async Task<FileRenameResultItem> ExecuteFileRenameAsync(Audiobook audiobook, FileRenameOperation fileOperation, IReadOnlyCollection<string> allowedRoots)
        {
            var source = NormalizePath(fileOperation.CurrentPath);
            var dest = NormalizePath(fileOperation.NewPath);
            var item = new FileRenameResultItem { FileId = fileOperation.FileId, PreviousPath = source, NewPath = dest };
            var trackedSourcePath = string.Empty;
            AudiobookFile? dbFile = null;

            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(dest))
            {
                item.Success = false;
                item.Error = "File organize operation is missing a source or destination path.";
                return item;
            }

            if (fileOperation.FileId == 0)
            {
                trackedSourcePath = NormalizePath(audiobook.FilePath);
                if (string.IsNullOrWhiteSpace(trackedSourcePath))
                {
                    item.Success = false;
                    item.Error = "Legacy file organize operation does not match a tracked audiobook file.";
                    return item;
                }
            }
            else
            {
                dbFile = audiobook.Files?.FirstOrDefault(f => f.Id == fileOperation.FileId);
                if (dbFile == null)
                {
                    item.Success = false;
                    item.Error = "File does not belong to this audiobook.";
                    return item;
                }

                trackedSourcePath = NormalizePath(dbFile.Path);
                if (string.IsNullOrWhiteSpace(trackedSourcePath))
                {
                    item.Success = false;
                    item.Error = "Tracked audiobook file path is missing.";
                    return item;
                }
            }

            if (!PathsEqual(source, trackedSourcePath))
            {
                item.Success = false;
                item.Error = "Source path does not match the tracked audiobook file.";
                return item;
            }

            if (!IsPathWithinAllowedRoots(source, allowedRoots) || !IsPathWithinAllowedRoots(dest, allowedRoots))
            {
                item.Success = false;
                item.Error = "File path is outside the allowed library roots.";
                return item;
            }

            if (!_fileSystem.FileExists(source))
            {
                item.Success = false;
                item.Error = "Source file not found.";
                return item;
            }

            if (_fileSystem.FileExists(dest) && !PathsEqual(source, dest))
            {
                item.Success = false;
                item.Error = "Target file already exists.";
                return item;
            }

            try
            {
                var targetDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrWhiteSpace(targetDir)) _fileSystem.CreateDirectory(targetDir);

                if (!PathsEqual(source, dest))
                {
                    var moved = await _fileMover.PerformActionOn(FileAction.Move, source, dest);
                    if (!moved)
                    {
                        item.Success = false;
                        item.Error = "File move operation failed.";
                        return item;
                    }
                }

                if (dbFile != null) dbFile.Path = dest;
                else if (fileOperation.FileId == 0 && !string.IsNullOrWhiteSpace(audiobook.FilePath)) audiobook.FilePath = dest;

                item.Success = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to organize file {FileId} for audiobook {AudiobookId}", fileOperation.FileId, audiobook.Id);
                item.Success = false;
                item.Error = ex.Message;
            }

            return item;
        }

        private async Task<(bool Success, string? Error)> ExecuteDirectoryMoveAsync(Audiobook audiobook, string newFolderPath, IReadOnlyCollection<string> allowedRoots)
        {
            var currentBase = ComputeCurrentBasePath(audiobook);
            if (string.IsNullOrWhiteSpace(currentBase))
            {
                audiobook.BasePath = NormalizePath(newFolderPath);
                return (true, null);
            }

            var normalizedCurrent = NormalizePath(currentBase);
            var normalizedNew = NormalizePath(newFolderPath);
            if (!IsPathWithinAllowedRoots(normalizedCurrent, allowedRoots) || !IsPathWithinAllowedRoots(normalizedNew, allowedRoots))
                return (false, "Destination path is outside the allowed library roots.");
            if (!_fileSystem.DirectoryExists(normalizedCurrent))
            {
                audiobook.BasePath = normalizedNew;
                return (true, null);
            }
            if (_fileSystem.DirectoryExists(normalizedNew) && _fileSystem.EnumerateFileSystemEntries(normalizedNew).Any())
                return (false, "Target folder already exists and is not empty.");

            var parent = Path.GetDirectoryName(normalizedNew);
            if (!string.IsNullOrWhiteSpace(parent)) _fileSystem.CreateDirectory(parent);

            var moved = await _fileMover.MoveDirectoryAsync(normalizedCurrent, normalizedNew);
            if (!moved) return (false, "Folder move operation failed.");

            audiobook.BasePath = normalizedNew;
            if (audiobook.Files != null)
            {
                foreach (var file in audiobook.Files.Where(f => !string.IsNullOrWhiteSpace(f.Path)
                    && (PathsEqual(f.Path, normalizedCurrent) || FileUtils.IsPathInsideOf(f.Path!, normalizedCurrent))))
                {
                    var relative = Path.GetRelativePath(normalizedCurrent, file.Path!);
                    file.Path = CombineRelativePath(normalizedNew, relative);
                }
            }
            if (!string.IsNullOrWhiteSpace(audiobook.FilePath)
                && (PathsEqual(audiobook.FilePath, normalizedCurrent) || FileUtils.IsPathInsideOf(audiobook.FilePath, normalizedCurrent)))
            {
                var relative = Path.GetRelativePath(normalizedCurrent, audiobook.FilePath);
                audiobook.FilePath = CombineRelativePath(normalizedNew, relative);
            }

            return (true, null);
        }

    }
}
