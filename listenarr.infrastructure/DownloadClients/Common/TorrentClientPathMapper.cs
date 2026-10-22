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

using System.Text.Json;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.DownloadClients.Common
{
    internal static class TorrentClientPathMapper
    {
        public static List<string> BuildQbittorrentSourceFiles(
            string savePath,
            List<Dictionary<string, JsonElement>> files)
        {
            if (string.IsNullOrEmpty(savePath) || files == null || files.Count == 0)
            {
                return new List<string>();
            }

            // External client paths are filesystem identifiers, not user text. Do not trim
            // whitespace from path segments; only strip separators when intentionally
            // converting a rooted-looking child path into a relative child path.
            return files
                .Where(IsQbittorrentFileDownloaded)
                .Select(file => file.TryGetValue("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => CombineClientReportedPath(savePath, name))
                .Where(path => !string.IsNullOrEmpty(path))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        public static List<string> BuildTransmissionSourceFiles(string? downloadDir, JsonElement filesElement)
        {
            if (string.IsNullOrEmpty(downloadDir) || filesElement.ValueKind != JsonValueKind.Array)
            {
                return new List<string>();
            }

            var sourceFiles = new List<string>();
            foreach (var file in filesElement.EnumerateArray())
            {
                if (!file.TryGetProperty("name", out var nameProp))
                {
                    continue;
                }

                var relativePath = nameProp.GetString();
                if (string.IsNullOrEmpty(relativePath))
                {
                    continue;
                }

                var sourceFile = CombineClientReportedPath(downloadDir, relativePath);
                if (!string.IsNullOrEmpty(sourceFile))
                {
                    sourceFiles.Add(sourceFile);
                }
            }

            return sourceFiles;
        }

        public static string ResolveQbittorrentContentPath(
            string savePath,
            List<Dictionary<string, JsonElement>> files)
        {
            if (string.IsNullOrEmpty(savePath) || files == null || files.Count == 0)
            {
                return string.Empty;
            }

            var fileNames = files
                .Where(IsQbittorrentFileDownloaded)
                .Select(f => f.TryGetValue("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty)
                .Where(name => !string.IsNullOrEmpty(name))
                .Where(name => !FileUtils.ContainsParentDirectorySegment(name, '/', '\\'))
                .ToList();

            if (fileNames.Count == 0)
            {
                return string.Empty;
            }

            var firstFile = fileNames[0];
            var firstParts = firstFile.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var hasNestedPath = firstParts.Length > 1;

            if (fileNames.Count == 1)
            {
                return hasNestedPath
                    ? CombineClientReportedPath(savePath, firstParts[0])
                    : CombineClientReportedPath(savePath, firstFile);
            }

            if (!hasNestedPath)
            {
                return savePath;
            }

            var topLevel = firstParts[0];
            var allShareTopLevel = fileNames.All(name =>
            {
                var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length > 1 && string.Equals(parts[0], topLevel, StringComparison.Ordinal);
            });

            return allShareTopLevel
                ? CombineClientReportedPath(savePath, topLevel)
                : savePath;
        }

        // qBittorrent lists every file in the torrent, including ones the user excluded —
        // priority 0 ("Do not download"), set manually or by the "Excluded file names" setting.
        // Those are never written to disk, so counting them as source files makes the on-disk
        // file count never match and the import stalls out (#1005). Treat only priority-0 as
        // skipped; an absent or non-zero priority is a file qBittorrent was asked to download.
        private static bool IsQbittorrentFileDownloaded(Dictionary<string, JsonElement> file)
        {
            if (!file.TryGetValue("priority", out var priorityElement))
            {
                return true;
            }

            return priorityElement.ValueKind switch
            {
                JsonValueKind.Number => !(priorityElement.TryGetInt32(out var priority) && priority == 0),
                JsonValueKind.String => !(int.TryParse(priorityElement.GetString(), out var priority) && priority == 0),
                _ => true
            };
        }

        private static string CombineClientReportedPath(string? basePath, string candidatePath)
        {
            if (string.IsNullOrEmpty(candidatePath) || string.IsNullOrEmpty(basePath))
            {
                return candidatePath;
            }

            var semantics = GetClientPathSemantics(basePath);
            var separators = semantics.Syntax == FileSystemPathSyntax.Windows
                ? new[] { '\\', '/' }
                : new[] { '/' };
            var relativePath = candidatePath.TrimStart(separators);
            if (HasDriveRootedPrefix(relativePath))
            {
                relativePath = relativePath[2..]
                    .TrimStart(separators);
            }

            if (relativePath.Split(separators).Any(segment => segment is "." or ".."))
            {
                return string.Empty;
            }

            return FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                basePath,
                relativePath,
                semantics,
                out var combinedPath)
                    ? combinedPath
                    : string.Empty;
        }

        private static FileSystemPathSemantics GetClientPathSemantics(string path)
        {
            var windowsSyntax = HasDriveRootedPrefix(path)
                || path.StartsWith("\\\\", StringComparison.Ordinal)
                || path.StartsWith("//", StringComparison.Ordinal);
            return new FileSystemPathSemantics(
                windowsSyntax ? FileSystemPathSyntax.Windows : FileSystemPathSyntax.Unix,
                windowsSyntax
                    ? FileSystemCaseSensitivity.Insensitive
                    : FileSystemCaseSensitivity.Sensitive);
        }

        private static bool HasDriveRootedPrefix(string path)
        {
            return path.Length >= 2
                && char.IsLetter(path[0])
                && path[1] == ':'
                && (path.Length == 2 || path[2] is '/' or '\\');
        }
    }
}
