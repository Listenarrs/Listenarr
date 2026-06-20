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

using System.Text.RegularExpressions;
using Listenarr.Domain.Common;

namespace Listenarr.Api.Features.Library
{
    internal static class LibraryPathPlanner
    {
        public static string ComputeAudiobookBaseDirectoryFromPattern(
            Audiobook audiobook,
            string rootPath,
            string fileNamingPattern,
            IFileNamingService fileNamingService)
        {
            string directoryPattern;
            if (!string.IsNullOrWhiteSpace(fileNamingPattern))
            {
                directoryPattern = fileNamingPattern;
                directoryPattern = Regex.Replace(directoryPattern, @"\{DiskNumber[^}]*\}", "", RegexOptions.IgnoreCase);
                directoryPattern = Regex.Replace(directoryPattern, @"\{ChapterNumber[^}]*\}", "", RegexOptions.IgnoreCase);
                directoryPattern = CleanDirectoryPattern(directoryPattern);

                if (string.IsNullOrWhiteSpace(directoryPattern) || !directoryPattern.Contains("/"))
                {
                    directoryPattern = "{Author}/{Title}";
                }
            }
            else
            {
                directoryPattern = "{Author}/{Title}";
            }

            if (!string.IsNullOrWhiteSpace(audiobook.Series) && !directoryPattern.Contains("{Series}"))
            {
                if (directoryPattern.Contains("{Author}/{Title}"))
                {
                    directoryPattern = directoryPattern.Replace("{Author}/{Title}", "{Author}/{Series}/{Title}");
                }
                else if (directoryPattern.Contains("{Author}/"))
                {
                    directoryPattern = directoryPattern.Replace("{Author}/", "{Author}/{Series}/");
                }
            }

            if (string.IsNullOrWhiteSpace(audiobook.Series))
            {
                directoryPattern = Regex.Replace(directoryPattern, @"\{Series[^}]*\}", string.Empty, RegexOptions.IgnoreCase);
                directoryPattern = CleanDirectoryPattern(directoryPattern);
            }

            var variables = new Dictionary<string, object>
            {
                { "Author", SanitizeDirectoryName(audiobook.Authors?.FirstOrDefault() ?? "Unknown Author") },
                { "Series", SanitizeDirectoryName(!string.IsNullOrWhiteSpace(audiobook.Series) ? audiobook.Series! : string.Empty) },
                { "Title", SanitizeDirectoryName(audiobook.Title ?? "Unknown Title") },
                { "Subtitle", SanitizeDirectoryName(audiobook.Subtitle ?? string.Empty) },
                { "Edition", SanitizeDirectoryName(audiobook.Edition ?? string.Empty) },
                { "Narrator", SanitizeDirectoryName((audiobook.Narrators != null && audiobook.Narrators.Any()) ? string.Join(", ", audiobook.Narrators.Where(n => !string.IsNullOrWhiteSpace(n))) : string.Empty) },
                { "Publisher", SanitizeDirectoryName(audiobook.Publisher ?? string.Empty) },
                { "Language", SanitizeDirectoryName(audiobook.Language ?? string.Empty) },
                { "Asin", SanitizeDirectoryName(audiobook.Asin ?? string.Empty) },
                { "SeriesNumber", audiobook.SeriesNumber ?? string.Empty },
                { "Year", audiobook.PublishYear ?? string.Empty },
                { "Quality", string.Empty },
                { "DiskNumber", string.Empty },
                { "ChapterNumber", string.Empty }
            };

            var relative = fileNamingService.ApplyNamingPattern(directoryPattern, variables, false);
            return ResolvePathWithOptionalBase(rootPath, relative);
        }

        public static string CalculateBasePath(List<string> filePaths, IFileSystem fileSystem, ILogger logger)
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

            var commonPath = GetCommonPath(directories, fileSystem);
            var currentPath = commonPath;
            while (!string.IsNullOrEmpty(currentPath))
            {
                try
                {
                    var parent = fileSystem.GetParentDirectory(currentPath);
                    if (string.IsNullOrEmpty(parent))
                        break;

                    var subDirs = fileSystem.EnumerateDirectories(parent).Count();
                    var files = fileSystem.EnumerateFiles(parent).Count();

                    if (subDirs + files > 1)
                    {
                        return currentPath;
                    }

                    currentPath = parent;
                }
                catch (Exception traversalEx) when (
                    traversalEx is IOException
                    || traversalEx is UnauthorizedAccessException
                    || traversalEx is System.Security.SecurityException
                    || traversalEx is ArgumentException
                    || traversalEx is NotSupportedException)
                {
                    logger.LogDebug(traversalEx, "Stopping common-base-path ascent at {Path} due to traversal error", currentPath);
                    break;
                }
            }

            return commonPath;
        }

        internal static string SanitizeDirectoryName(string name)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            foreach (var c in invalidChars)
            {
                name = name.Replace(c, '_');
            }

            name = name.Replace(":", "_").Replace("*", "_").Replace("?", "_").Replace("\"", "_").Replace("<", "_").Replace(">", "_").Replace("|", "_");
            return name.Trim();
        }

        private static string GetCommonPath(List<string> paths, IFileSystem fileSystem)
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
                    commonLength = commonPath.LastIndexOf(Path.DirectorySeparatorChar, commonLength - 1) is var lastSep && lastSep >= 0
                        ? lastSep + 1
                        : 0;

                commonPath = commonPath.Substring(0, commonLength);

                if (string.IsNullOrEmpty(commonPath))
                    break;
            }

            if (!string.IsNullOrEmpty(commonPath) && !fileSystem.DirectoryExists(commonPath))
            {
                var parent = fileSystem.GetParentDirectory(commonPath);
                return parent ?? commonPath;
            }

            return commonPath;
        }

        private static string CleanDirectoryPattern(string pattern)
        {
            pattern = Regex.Replace(pattern, @"[\\/]\s*[\\/]", "/");
            pattern = Regex.Replace(pattern, @"^\s*[\\/]", "");
            return Regex.Replace(pattern, @"[\\/]\s*$", "");
        }

        private static string ResolvePathWithOptionalBase(string? basePath, string candidatePath)
        {
            return FileUtils.CombineWithOptionalBase(basePath, candidatePath);
        }
    }
}
