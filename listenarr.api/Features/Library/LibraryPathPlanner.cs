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
