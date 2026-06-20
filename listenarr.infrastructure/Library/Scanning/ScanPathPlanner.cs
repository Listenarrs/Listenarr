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

namespace Listenarr.Infrastructure.Library.Scanning
{
    internal static class ScanPathPlanner
    {
        public static string CalculateBasePath(List<string> filePaths)
        {
            if (!filePaths.Any())
                return string.Empty;

            var directories = filePaths
                .Select(p => FileUtils.NormalizeStoredPath(Path.GetDirectoryName(p) ?? p))
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (directories.Count == 1)
            {
                return directories[0];
            }

            var commonPath = GetCommonPath(directories);
            var currentPath = commonPath;
            while (!string.IsNullOrEmpty(currentPath))
            {
                try
                {
                    var parent = Directory.GetParent(currentPath)?.FullName;
                    if (string.IsNullOrEmpty(parent))
                        break;

                    var subDirs = Directory.GetDirectories(parent).Length;
                    var files = Directory.GetFiles(parent).Length;
                    if (subDirs + files > 1)
                    {
                        return currentPath;
                    }

                    currentPath = parent;
                }
                catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
                {
                    break;
                }
            }

            return commonPath;
        }

        private static string GetCommonPath(List<string> paths)
        {
            if (!paths.Any())
                return string.Empty;

            var firstPath = FileUtils.NormalizeStoredPath(paths[0]);
            var commonPath = firstPath;

            foreach (var path in paths.Skip(1).Select(rawPath => FileUtils.NormalizeStoredPath(rawPath)))
            {
                var minLength = Math.Min(commonPath.Length, path.Length);
                var commonLength = 0;

                for (int i = 0; i < minLength; i++)
                {
                    if (commonPath[i] == path[i])
                        commonLength++;
                    else
                        break;
                }

                if (commonLength < commonPath.Length)
                {
                    var lastSep = commonPath.LastIndexOf(Path.DirectorySeparatorChar, commonLength - 1);
                    commonLength = lastSep >= 0 ? lastSep + 1 : 0;
                }

                commonPath = commonPath.Substring(0, commonLength);

                if (string.IsNullOrEmpty(commonPath))
                    break;
            }

            if (!string.IsNullOrEmpty(commonPath) && !Directory.Exists(commonPath))
            {
                var parent = Directory.GetParent(commonPath)?.FullName;
                return parent ?? commonPath;
            }

            return commonPath;
        }
    }
}
