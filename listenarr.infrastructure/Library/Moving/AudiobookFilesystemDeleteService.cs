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

using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving
{
    public sealed partial class AudiobookFilesystemDeleteService : IAudiobookFilesystemDeleteService
    {
        private readonly IAudiobookRepository _audiobookRepository;
        private readonly IAudiobookFileRepository _audioFileRepository;
        private readonly IRootFolderService _rootFolderService;
        private readonly IConfigurationService _configurationService;
        private readonly ILogger<AudiobookFilesystemDeleteService> _logger;

        public AudiobookFilesystemDeleteService(
            IAudiobookRepository audiobookRepository,
            IAudiobookFileRepository audioFileRepository,
            IRootFolderService rootFolderService,
            IConfigurationService configurationService,
            ILogger<AudiobookFilesystemDeleteService> logger)
        {
            _audiobookRepository = audiobookRepository;
            _audioFileRepository = audioFileRepository;
            _rootFolderService = rootFolderService;
            _configurationService = configurationService;
            _logger = logger;
        }

        public async Task<AudiobookFilesystemDeleteResult> DeleteAsync(Audiobook audiobook, bool deleteFolder)
        {
            var result = new AudiobookFilesystemDeleteResult();
            var trackedFilePaths = CollectTrackedFilePaths(audiobook);
            var deleteTarget = await ResolveDeleteFolderTargetAsync(audiobook, trackedFilePaths, result);

            if (deleteTarget != null)
            {
                TryDeleteFolderContents(deleteTarget.FolderPath, result);

                if (deleteFolder)
                {
                    await TryDeleteAudiobookFolderAsync(audiobook, deleteTarget, result);
                }
            }
            else
            {
                var protectedRoots = await GetProtectedRootPathsAsync();
                var fallbackFolderRoot = ResolveAudiobookFolderPath(audiobook, trackedFilePaths);
                var allowedRoots = protectedRoots
                    .Concat(string.IsNullOrWhiteSpace(fallbackFolderRoot) ? [] : [fallbackFolderRoot])
                    .ToList();
                foreach (var trackedFilePath in trackedFilePaths)
                {
                    TryDeleteFile(trackedFilePath, result, allowedRoots);
                }
            }

            return result;
        }

        private sealed class DeleteFolderTarget
        {
            public required string FolderPath { get; init; }
            public required IReadOnlyCollection<string> ProtectedRoots { get; init; }
        }

        private static IReadOnlyList<string> CollectTrackedFilePaths(Audiobook audiobook)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(audiobook.FilePath))
            {
                var normalizedLegacy = NormalizePath(audiobook.FilePath);
                if (!string.IsNullOrWhiteSpace(normalizedLegacy))
                {
                    paths.Add(normalizedLegacy);
                }
            }

            if (audiobook.Files != null)
            {
                foreach (var normalizedTracked in audiobook.Files
                    .Select(file => NormalizePath(file.Path))
                    .Where(normalizedTracked => !string.IsNullOrWhiteSpace(normalizedTracked)))
                {
                    paths.Add(normalizedTracked!);
                }
            }

            return paths.ToList();
        }

        private void TryDeleteFile(string path, AudiobookFilesystemDeleteResult result, IEnumerable<string>? allowedRoots = null)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                var originalPath = path;
                if (allowedRoots != null
                    && !FileSystemSafety.TryValidateMutationTarget(path, allowedRoots, out path, out var reason))
                {
                    result.Warnings.Add("Refused to delete a file outside the allowed library roots.");
                    _logger.LogWarning(
                        "Blocked audiobook file delete for {Path}: {Reason}",
                        LogRedaction.SanitizeFilePath(originalPath),
                        LogRedaction.SanitizeText(reason));
                    return;
                }

                File.Delete(path);
                result.DeletedFiles++;
                _logger.LogInformation("Deleted audiobook file {Path}", LogRedaction.SanitizeFilePath(path));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                var warning = $"Could not delete file '{Path.GetFileName(path)}'.";
                result.Warnings.Add(warning);
                _logger.LogWarning(ex, "Failed to delete audiobook file {Path}", LogRedaction.SanitizeFilePath(path));
            }
        }

        private void TryDeleteFolderContents(string folderPath, AudiobookFilesystemDeleteResult result)
        {
            if (!Directory.Exists(folderPath))
            {
                return;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                result.Warnings.Add("Could not enumerate the audiobook folder contents for deletion.");
                _logger.LogWarning(ex, "Failed to enumerate audiobook folder contents for {FolderPath}", LogRedaction.SanitizeFilePath(folderPath));
                return;
            }

            foreach (var filePath in files)
            {
                TryDeleteFile(filePath, result, [folderPath]);
            }

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(folderPath, "*", SearchOption.AllDirectories)
                    .OrderByDescending(path => path.Length)
                    .ToArray();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                result.Warnings.Add("Some nested folders could not be cleaned up after file deletion.");
                _logger.LogWarning(ex, "Failed to enumerate nested audiobook directories for {FolderPath}", LogRedaction.SanitizeFilePath(folderPath));
                return;
            }

            foreach (var directoryPath in directories)
            {
                try
                {
                    if (!Directory.Exists(directoryPath))
                    {
                        continue;
                    }

                    if (!Directory.EnumerateFileSystemEntries(directoryPath).Any())
                    {
                        Directory.Delete(directoryPath, recursive: false);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Failed to remove nested audiobook directory {FolderPath}", LogRedaction.SanitizeFilePath(directoryPath));
                }
            }
        }

    }
}
