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
using Listenarr.Application.Common;
using Listenarr.Application.Interfaces;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Listenarr.Infrastructure.FileSystem
{
    public class ArchiveExtractor : IArchiveExtractor
    {
        private readonly ILogger<ArchiveExtractor> _logger;
        private static readonly string[] KnownArchiveExtensions = new[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz" };

        public ArchiveExtractor(ILogger<ArchiveExtractor>? logger = null)
        {
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ArchiveExtractor>.Instance;
        }

        public bool IsArchive(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            var ext = Path.GetExtension(filePath);
            return KnownArchiveExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
        }

        public async Task<TempDirectory?> ExtractArchiveToTempDirAsync(string archivePath)
        {
            try
            {
                if (!File.Exists(archivePath)) return null;
                if (!IsArchive(archivePath)) return null;

                var tmp = Path.Join(Path.GetTempPath(), "listenarr-extract", Guid.NewGuid().ToString());
                Directory.CreateDirectory(tmp);

                // Use SharpCompress to extract safely
                using var archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions());
                var tmpRoot = Path.GetFullPath(tmp);
                foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                {
                    try
                    {
                        var entryPath = (entry.Key ?? string.Empty)
                            .Replace('\\', Path.DirectorySeparatorChar)
                            .Trim();

                        if (string.IsNullOrWhiteSpace(entryPath))
                        {
                            continue;
                        }

                        var relativeEntryPath = entryPath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        if (Path.IsPathRooted(relativeEntryPath))
                        {
                            _logger.LogWarning(
                                "ArchiveExtractor: skipping rooted entry path {Entry} in archive {Archive}",
                                entry.Key,
                                archivePath);
                            continue;
                        }

                        var combinedPath = tmpRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            + Path.DirectorySeparatorChar
                            + relativeEntryPath;
                        var destPath = Path.GetFullPath(combinedPath);
                        if (!destPath.StartsWith(tmpRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(destPath, tmpRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning(
                                "ArchiveExtractor: skipping out-of-root entry {Entry} in archive {Archive}",
                                entry.Key,
                                archivePath);
                            continue;
                        }

                        var destDir = Path.GetDirectoryName(destPath) ?? string.Empty;
                        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
                        entry.WriteToFile(destPath, new ExtractionOptions() { ExtractFullPath = true, Overwrite = true });
                    }
                    catch (Exception exEntry) when (exEntry is not OperationCanceledException && exEntry is not OutOfMemoryException && exEntry is not StackOverflowException)
                    {
                        _logger.LogDebug(exEntry, "ArchiveExtractor: failed to extract entry {Entry} from archive {Archive}", entry.Key, archivePath);
                    }
                }

                return await Task.FromResult(new TempDirectory(tmp));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "ArchiveExtractor: failed to extract archive {Archive}", archivePath);
                return null;
            }
        }
    }
}
































