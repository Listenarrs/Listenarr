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
// csharp
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem
{
    public class FileStorage : IFileStorage
    {
        private readonly IApplicationPathService _applicationPathService;
        private readonly ILogger<FileStorage> _logger;

        public FileStorage(IApplicationPathService applicationPathService, ILogger<FileStorage> logger)
        {
            _applicationPathService = applicationPathService;
            _logger = logger;
        }

        public async Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default)
        {
            path = ValidateStorageMutation(path, "write file");
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(path, contents ?? string.Empty, cancellationToken).ConfigureAwait(false);
        }

        public Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        {
            sourcePath = ValidateStorageMutation(sourcePath, "move source");
            destinationPath = ValidateStorageMutation(destinationPath, "move destination");
            // Ensure destination directory exists
            var destDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            // Synchronous move is fine here; wrap in Task for interface contract.
            File.Move(sourcePath, destinationPath, overwrite: true);
            return Task.CompletedTask;
        }

        public bool FileExists(string path) => File.Exists(path);

        public void CreateDirectory(string path)
        {
            path = ValidateStorageMutation(path, "create directory");
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        public void DeleteFile(string path)
        {
            path = ValidateStorageMutation(path, "delete file");
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        public void DeleteDirectory(string path, bool recursive)
        {
            path = ValidateStorageMutation(path, "delete directory");
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive);
            }
        }

        private string ValidateStorageMutation(string path, string action)
        {
            var roots = new[]
            {
                _applicationPathService.ContentRootPath,
                _applicationPathService.ConfigRootPath
            };

            if (FileSystemSafety.TryValidateMutationTarget(path, roots, out var normalizedPath, out var reason))
            {
                return normalizedPath;
            }

            _logger.LogWarning(
                "Blocked FileStorage {Action} for {Path}: {Reason}",
                action,
                LogRedaction.SanitizeFilePath(path),
                LogRedaction.SanitizeText(reason));
            throw new IOException($"Blocked FileStorage {action}: {reason}");
        }
    }
}
