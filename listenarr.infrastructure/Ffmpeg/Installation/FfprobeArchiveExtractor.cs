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
using Listenarr.Domain.Common;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Listenarr.Infrastructure.Ffmpeg.Installation
{
    internal static class FfprobeArchiveExtractor
    {
        public static async Task<bool> ExtractAsync(
            string downloadUrl,
            string tmpFile,
            string baseDir,
            string ffprobePath,
            ILogger logger)
        {
            if (downloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || downloadUrl.EndsWith(".ffmpeg.zip", StringComparison.OrdinalIgnoreCase))
            {
                using var archive = SharpCompress.Archives.Zip.ZipArchive.OpenArchive(tmpFile, new ReaderOptions());
                var baseRoot = Path.GetFullPath(baseDir);
                foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                {
                    var entryPath = entry.Key ?? string.Empty;
                    if (!FileUtils.TryResolveRelativePathWithinBase(baseRoot, entryPath, out var outPath))
                    {
                        logger.LogWarning("Skipping archive entry outside extraction root: {Entry}", entryPath);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? baseDir);
                    entry.WriteToFile(outPath, new ExtractionOptions() { ExtractFullPath = true, Overwrite = true });
                    logger.LogDebug("Extracted archive entry to {OutPath}", outPath);
                }

                await TryDeleteFileAsync(tmpFile);
                return true;
            }

            if (downloadUrl.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase) || downloadUrl.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || downloadUrl.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var stream = File.OpenRead(tmpFile);
                    var readerOptions = new ReaderOptions { LeaveStreamOpen = false };
                    using var reader = ReaderFactory.OpenReader(stream, readerOptions);
                    var baseRoot = Path.GetFullPath(baseDir);
                    while (reader.MoveToNextEntry())
                    {
                        if (reader.Entry.IsDirectory)
                        {
                            continue;
                        }

                        var entryPath = reader.Entry.Key ?? string.Empty;
                        if (!FileUtils.TryResolveRelativePathWithinBase(baseRoot, entryPath, out var outPath))
                        {
                            logger.LogWarning("Skipping archive entry outside extraction root: {Entry}", entryPath);
                            continue;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? baseDir);
                        using var entryStream = reader.OpenEntryStream();
                        await using var outFs = File.Create(outPath);
                        await entryStream.CopyToAsync(outFs);
                        logger.LogDebug("Extracted archive entry to {OutPath}", outPath);
                    }

                    await TryDeleteFileAsync(tmpFile);
                    return true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    logger.LogWarning(ex, "Managed extraction failed for {Tmp}; skipping system tar fallback to avoid unsafe archive extraction", tmpFile);
                    await TryDeleteFileAsync(tmpFile);
                    return false;
                }
            }

            if (!FileSystemSafety.TryValidateMutationTarget(ffprobePath, [baseDir], out var safeFfprobePath, out var reason))
            {
                logger.LogWarning("Blocked ffprobe install target outside base directory: {Reason}", reason);
                await TryDeleteFileAsync(tmpFile);
                return false;
            }

            File.Move(tmpFile, safeFfprobePath);
            return true;
        }

        private static async Task TryDeleteFileAsync(string path, int retries = 3, int delayMs = 100, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(path)) return;
            for (var i = 0; i < retries; i++)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                catch (Exception) when (i < retries - 1)
                {
                    try { await Task.Delay(delayMs, cancellationToken); } catch (OperationCanceledException) { return; }
                }
                catch (Exception)
                {
                    return;
                }
            }
        }

    }
}
