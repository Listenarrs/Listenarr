using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace Listenarr.Api.Services
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

        public async Task<string?> ExtractArchiveToTempDirAsync(string archivePath)
        {
            try
            {
                if (!File.Exists(archivePath)) return null;
                if (!IsArchive(archivePath)) return null;

                var tmp = Path.Combine(Path.GetTempPath(), "listenarr-extract", Guid.NewGuid().ToString());
                Directory.CreateDirectory(tmp);

                // Use SharpCompress to extract safely
                using var archive = ArchiveFactory.Open(archivePath);
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
                    catch (Exception exEntry) when (exEntry is not OperationCanceledException && exEntry is not OutOfMemoryException && exEntry is not StackOverflowException) {
                        _logger.LogDebug(exEntry, "ArchiveExtractor: failed to extract entry {Entry} from archive {Archive}", entry.Key, archivePath);
                    }
                }

                return await Task.FromResult(tmp);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogWarning(ex, "ArchiveExtractor: failed to extract archive {Archive}", archivePath);
                return null;
            }
        }
    }
}































