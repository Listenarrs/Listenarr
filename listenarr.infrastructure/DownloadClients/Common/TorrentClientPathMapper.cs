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
            if (string.IsNullOrWhiteSpace(savePath) || files == null || files.Count == 0)
            {
                return new List<string>();
            }

            return files
                .Select(file => file.TryGetValue("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => CombineWithOptionalBase(savePath, name.Replace('/', Path.DirectorySeparatorChar)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static List<string> BuildTransmissionSourceFiles(string? downloadDir, JsonElement filesElement)
        {
            if (string.IsNullOrWhiteSpace(downloadDir) || filesElement.ValueKind != JsonValueKind.Array)
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
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    continue;
                }

                sourceFiles.Add(FileUtils.CombineWithOptionalBase(downloadDir, relativePath));
            }

            return sourceFiles;
        }

        public static string ResolveQbittorrentContentPath(
            string savePath,
            List<Dictionary<string, JsonElement>> files)
        {
            if (string.IsNullOrWhiteSpace(savePath) || files == null || files.Count == 0)
            {
                return string.Empty;
            }

            var fileNames = files
                .Select(f => f.TryGetValue("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty)
                .Where(name => !string.IsNullOrWhiteSpace(name))
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
                    ? CombineWithOptionalBase(savePath, firstParts[0])
                    : CombineWithOptionalBase(savePath, firstFile);
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
                ? CombineWithOptionalBase(savePath, topLevel)
                : savePath;
        }

        private static string CombineWithOptionalBase(string? basePath, string candidatePath)
        {
            var normalizedPath = candidatePath.Trim();

            if (string.IsNullOrEmpty(normalizedPath))
            {
                return normalizedPath;
            }

            if (Path.IsPathRooted(normalizedPath) || string.IsNullOrWhiteSpace(basePath))
            {
                return normalizedPath;
            }

            var relativePath = normalizedPath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Path.IsPathRooted(relativePath))
            {
                return relativePath;
            }

            var normalizedBasePath = basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrEmpty(normalizedBasePath)
                ? relativePath
                : normalizedBasePath + Path.DirectorySeparatorChar + relativePath;
        }
    }
}
