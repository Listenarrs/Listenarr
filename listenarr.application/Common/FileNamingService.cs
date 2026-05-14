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
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models;
using Listenarr.Domain.Models.Configurations;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Common
{
    public class FileNamingService : IFileNamingService
    {
        private static readonly HashSet<char> PortableInvalidFileNameChars = BuildPortableInvalidFileNameChars();
        private static readonly HashSet<string> ReservedWindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        private readonly IConfigurationService _configService;
        private readonly ILogger<FileNamingService> _logger;

        public FileNamingService(IConfigurationService configService, ILogger<FileNamingService> logger)
        {
            _configService = configService;
            _logger = logger;
        }

        /// <summary>
        /// Apply the configured file naming pattern to generate the output path from settings
        /// </summary>
        public async Task<string> GenerateFilePathAsync(
            AudioMetadata metadata,
            string originalExtension = ".m4b")
        {
            var settings = await _configService.GetApplicationSettingsAsync() ?? new ApplicationSettings();
            return await GenerateFilePathAsync(metadata, settings.OutputPath, originalExtension);
        }

        public async Task<string> GenerateFilePathAsync(
            AudioMetadata metadata,
            string outputPath,
            string originalExtension = ".m4b")
        {
            var settings = await _configService.GetApplicationSettingsAsync() ?? new ApplicationSettings();
            var folderPattern = settings.FolderNamingPattern;

            // Determine if this is a multi-file import (has disk or chapter number)
            bool isMultiFile = metadata.DiscNumber.HasValue || metadata.TrackNumber.HasValue;
            var filePattern = isMultiFile
                ? settings.MultiFileNamingPattern
                : settings.FileNamingPattern;

            var effectiveFolderPattern = folderPattern;
            try
            {
                if (!string.IsNullOrWhiteSpace(outputPath) && !string.IsNullOrWhiteSpace(settings.OutputPath))
                {
                    var requestedRoot = Path.GetFullPath(outputPath);
                    var configuredRoot = Path.GetFullPath(settings.OutputPath);
                    if (!string.Equals(requestedRoot, configuredRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        // Caller provided a custom base path (e.g., audiobook BasePath) -> skip folder pattern
                        effectiveFolderPattern = string.Empty;
                    }
                }
            }
            catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException)
            {
                // If paths are invalid, fall back to configured folder pattern
                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }

            var variables = BuildVariables(metadata);

            // Diagnostic logging: record the variables used for pattern replacement
            try
            {
                var dbg = string.Join(", ", variables.Select(kv => $"{kv.Key}='{kv.Value}'"));
                _logger.LogInformation("FileNamingService variables: {Vars}", dbg);
            }
            catch (Exception caughtEx_3) when (caughtEx_3 is not OperationCanceledException && caughtEx_3 is not OutOfMemoryException && caughtEx_3 is not StackOverflowException)
            {
                // ignore logging errors
                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }

            string relativePath;
            if (string.IsNullOrWhiteSpace(effectiveFolderPattern))
            {
                // Legacy behavior: use FileNamingPattern as the full relative path pattern
                var legacyPattern = string.IsNullOrWhiteSpace(filePattern)
                    ? "{Author}/{Series}/{Title}"
                    : filePattern;

                relativePath = ApplyNamingPattern(legacyPattern, variables);
            }
            else
            {
                // New behavior: separate folder and file patterns
                var effectiveFilePattern = string.IsNullOrWhiteSpace(filePattern) ? "{Title}" : filePattern;

                var folderRelative = ApplyNamingPattern(effectiveFolderPattern, variables, treatAsFilename: false);

                // Normalize path separators to platform-specific ones
                if (!string.IsNullOrWhiteSpace(folderRelative))
                {
                    folderRelative = folderRelative.Replace('/', Path.DirectorySeparatorChar)
                                                   .Replace('\\', Path.DirectorySeparatorChar);
                }

                var patternAllowsSubfolders = effectiveFilePattern.IndexOf("DiskNumber", StringComparison.OrdinalIgnoreCase) >= 0
                    || effectiveFilePattern.IndexOf("ChapterNumber", StringComparison.OrdinalIgnoreCase) >= 0
                    || effectiveFilePattern.IndexOf('/') >= 0
                    || effectiveFilePattern.IndexOf('\\') >= 0;

                var fileRelative = ApplyNamingPattern(effectiveFilePattern, variables, treatAsFilename: !patternAllowsSubfolders);

                relativePath = string.IsNullOrWhiteSpace(folderRelative)
                    ? fileRelative
                    : CombineWithOptionalBase(folderRelative, fileRelative);
            }

            // Ensure it has the correct extension
            if (!relativePath.EndsWith(originalExtension, StringComparison.OrdinalIgnoreCase))
            {
                relativePath += originalExtension;
            }

            // Combine with the provided output path
            var fullPath = string.IsNullOrWhiteSpace(outputPath)
                ? relativePath
                : CombineWithOptionalBase(outputPath, relativePath);

            fullPath = EnsurePathWithinLimits(fullPath);

            _logger.LogInformation("Generated file path: {FilePath}", fullPath);
            return fullPath;
        }

        public string ApplyNamingPattern(string pattern, Dictionary<string, object> variables, bool treatAsFilename = false)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return "";
            }

            var result = pattern;

            // Regex to match variables: {VariableName} or {VariableName:Format}
            var variableRegex = new Regex(@"\{(\w+)(?::([^}]+))?\}", RegexOptions.IgnoreCase);

            // Replace variables. If a variable is empty, emit a sentinel so we can clean up surrounding
            // punctuation and separators (for example: remove "{Series}/" when Series is empty).
            const string EmptySentinel = "__EMPTY_VAR__";
            result = variableRegex.Replace(result, match =>
            {
                var variableName = match.Groups[1].Value;
                var format = match.Groups[2].Success ? match.Groups[2].Value : null;

                if (variables.TryGetValue(variableName, out var value))
                {
                    // Handle empty values
                    if (value == null || string.IsNullOrWhiteSpace(value.ToString()))
                    {
                        return EmptySentinel;
                    }

                    string renderedValue;

                    // Apply formatting if specified
                    if (!string.IsNullOrEmpty(format))
                    {
                        // For numeric values with format (e.g., {DiskNumber:00})
                        if (value is int intValue)
                        {
                            renderedValue = intValue.ToString(format);
                        }
                        else if (int.TryParse(value.ToString(), out var parsedInt))
                        {
                            renderedValue = parsedInt.ToString(format);
                        }
                        else
                        {
                            renderedValue = value.ToString() ?? string.Empty;
                        }
                    }
                    else
                    {
                        renderedValue = value.ToString() ?? string.Empty;
                    }

                    return SanitizePathComponent(renderedValue);
                }

                // Variable not found, return sentinel so we can optionally remove surrounding chars
                _logger.LogWarning("Variable {VariableName} not found in naming pattern", variableName);
                return EmptySentinel;
            });

            // Cleanup: remove empty sentinel inside any brackets (e.g. "(__EMPTY_VAR__)" -> "")
            result = Regex.Replace(result, @"[\(\[\{]\s*" + EmptySentinel + @"\s*[\)\]\}]", string.Empty);

            // Remove common separators adjacent to the sentinel (e.g. " - __EMPTY_VAR__" or "__EMPTY_VAR__ - ")
            result = Regex.Replace(result, @"\s*[-–—:_]\s*" + EmptySentinel, string.Empty);
            result = Regex.Replace(result, EmptySentinel + @"\s*[-–—:_]\s*", string.Empty);

            // Remove sentinel next to slashes
            result = Regex.Replace(result, @"/?" + EmptySentinel + @"/?", "/");

            // Finally remove any remaining sentinels
            result = result.Replace(EmptySentinel, string.Empty);

            // Clean up multiple consecutive slashes or spaces
            result = Regex.Replace(result, @"[\\/]{2,}", "/");
            result = Regex.Replace(result, @"\s{2,}", " ");

            if (treatAsFilename)
            {
                // If we're generating a filename (not a path), ensure no directory separators remain.
                // Split on any slashes and take the last segment to avoid creating directories from tokens.
                var partsForFilename = result.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                result = partsForFilename.Length > 0 ? partsForFilename.Last().Trim() : result.Trim();

                // Remove any stray separators and sanitize the filename component
                result = result.Replace("/", string.Empty).Replace("\\", string.Empty);
                result = SanitizePathComponent(result);
            }
            else
            {
                // Remove leading/trailing slashes and spaces from each path component
                var parts = result.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToList();

                // Collapse adjacent duplicate components (case-insensitive) to avoid
                // patterns producing repeated folders like "Title/Title (...)/Title"
                for (int i = parts.Count - 1; i > 0; i--)
                {
                    if (string.Equals(parts[i], parts[i - 1], StringComparison.OrdinalIgnoreCase))
                    {
                        parts.RemoveAt(i);
                    }
                }

                // Sanitize each path component to remove invalid characters
                var sanitizedParts = parts.Select(p => SanitizePathComponent(p)).ToList();
                result = string.Join(Path.DirectorySeparatorChar.ToString(), sanitizedParts);
            }

            return result;
        }

        public string ApplyNamingPattern(string pattern, AudioMetadata metadata, bool treatAsFilename = false)
        {
            var variables = BuildVariables(metadata);
            return ApplyNamingPattern(pattern, variables, treatAsFilename);
        }

        public string ApplyNamingPattern(string pattern, AudibleBookMetadata metadata, bool treatAsFilename = false)
        {
            var variables = BuildVariables(metadata);
            return ApplyNamingPattern(pattern, variables, treatAsFilename);
        }

        /// <summary>
        /// Remove invalid characters from path components
        /// </summary>
        private string SanitizePathComponent(string pathComponent)
        {
            if (string.IsNullOrWhiteSpace(pathComponent))
            {
                return "Unknown";
            }

            var sanitized = new StringBuilder();
            foreach (var c in pathComponent)
            {
                if (char.IsControl(c))
                {
                    continue;
                }

                if (c == ':' || c == '/' || c == '\\')
                {
                    sanitized.Append(" - ");
                }
                else if (PortableInvalidFileNameChars.Contains(c))
                {
                    sanitized.Append('_');
                }
                else
                {
                    sanitized.Append(c);
                }
            }

            var result = sanitized.ToString();
            result = Regex.Replace(result, @"\s+", " ");
            result = Regex.Replace(result, @"(?:\s*-\s*){2,}", " - ");
            result = Regex.Replace(result, @"_+", "_");
            result = result.Trim();
            result = result.TrimEnd('.', ' ');
            result = Regex.Replace(result, @"^\s*[-_]+\s*", string.Empty);
            result = Regex.Replace(result, @"\s*[-_]+\s*$", string.Empty);

            if (string.IsNullOrWhiteSpace(result))
            {
                return "Unknown";
            }

            var extensionSeparator = result.IndexOf('.');
            var deviceNameStem = extensionSeparator >= 0 ? result[..extensionSeparator] : result;
            if (ReservedWindowsDeviceNames.Contains(deviceNameStem))
            {
                result = extensionSeparator >= 0
                    ? deviceNameStem + "_" + result[extensionSeparator..]
                    : result + "_";
            }

            return result;
        }

        private Dictionary<string, object> BuildVariables(AudioMetadata metadata)
        {
            return new Dictionary<string, object>
            {
                // Keep multi-word author names as a single folder name (e.g. "Jane Austen")
                { "Author", SanitizePathComponent(FirstNonEmpty(ChooseAuthor(metadata), "Unknown Author")) },
                // For Series we must not fallback to Album or Title - when Series is blank we want
                // the variable to be empty so ApplyNamingPattern can remove any adjacent separators
                { "Series", string.IsNullOrWhiteSpace(metadata.Series) ? string.Empty : SanitizePathComponent(metadata.Series) },
                { "Title", SanitizePathComponent(FirstNonEmpty(metadata.Title, "Unknown Title")) },
                { "Subtitle", string.IsNullOrWhiteSpace(metadata.Subtitle) ? string.Empty : SanitizePathComponent(metadata.Subtitle) },
                { "Edition", string.IsNullOrWhiteSpace(metadata.Edition) ? string.Empty : SanitizePathComponent(metadata.Edition) },
                { "Narrator", string.IsNullOrWhiteSpace(metadata.Narrator) ? string.Empty : SanitizePathComponent(metadata.Narrator) },
                { "Publisher", string.IsNullOrWhiteSpace(metadata.Publisher) ? string.Empty : SanitizePathComponent(metadata.Publisher) },
                { "Language", string.IsNullOrWhiteSpace(metadata.Language) ? string.Empty : SanitizePathComponent(metadata.Language) },
                { "Asin", string.IsNullOrWhiteSpace(metadata.Asin) ? string.Empty : SanitizePathComponent(metadata.Asin) },
                { "SeriesNumber", FirstNonEmpty(metadata.SeriesPosition?.ToString(CultureInfo.InvariantCulture), metadata.TrackNumber?.ToString()) },
                { "Year", FirstNonEmpty(metadata.Year?.ToString()) },
                { "Quality", FirstNonEmpty(metadata.BitRate.HasValue ? metadata.BitRate + "kbps" : null, metadata.Format) },
                { "DiskNumber", metadata.DiscNumber?.ToString() ?? string.Empty },
                { "ChapterNumber", metadata.TrackNumber?.ToString() ?? string.Empty }
            };
        }

        private Dictionary<string, object> BuildVariables(AudibleBookMetadata metadata)
        {
            var author = metadata.Author ?? "Unknown Author";
            if (metadata.Authors != null && metadata.Authors.Count > 0)
            {
                // Assume first one is the main author
                author = metadata.Authors.First();
            }

            return new Dictionary<string, object>
            {
                { "Author", SanitizePathComponent(author) },
                { "Series", string.IsNullOrWhiteSpace(metadata.Series) ? string.Empty : SanitizePathComponent(metadata.Series) },
                { "Title", SanitizePathComponent(FirstNonEmpty(metadata.Title, "Unknown Title")) },
                { "Subtitle", string.IsNullOrWhiteSpace(metadata.Subtitle) ? string.Empty : SanitizePathComponent(metadata.Subtitle) },
                { "Edition", string.IsNullOrWhiteSpace(metadata.Edition) ? string.Empty : SanitizePathComponent(metadata.Edition) },
                { "Narrator", string.IsNullOrWhiteSpace(metadata.Narrator) ? string.Empty : SanitizePathComponent(metadata.Narrator) },
                { "Publisher", string.IsNullOrWhiteSpace(metadata.Publisher) ? string.Empty : SanitizePathComponent(metadata.Publisher) },
                { "Language", string.IsNullOrWhiteSpace(metadata.Language) ? string.Empty : SanitizePathComponent(metadata.Language) },
                { "Asin", string.IsNullOrWhiteSpace(metadata.Asin) ? string.Empty : SanitizePathComponent(metadata.Asin) },
                { "SeriesNumber", metadata.SeriesNumber?.ToString() ?? string.Empty },
                { "Year", metadata.PublishYear?.ToString() ?? string.Empty },
                { "Quality", string.Empty },
                { "DiskNumber", string.Empty },
                { "ChapterNumber", string.Empty }
            };
        }

        private static string FirstNonEmpty(params string?[] candidates)
        {
            return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? string.Empty;
        }

        // Heuristic: sometimes metadata.Artist can contain the title/series (noisy tags).
        // Prefer an AlbumArtist or alternate artist value if the primary artist looks like the title/series.
        private static string ChooseAuthor(AudioMetadata metadata)
        {
            var primary = NonNarratorAuthorCandidate(metadata.Artist, metadata.Narrator);
            var alternate = NonNarratorAuthorCandidate(metadata.AlbumArtist, metadata.Narrator);

            if (string.IsNullOrWhiteSpace(primary))
            {
                return alternate;
            }

            if (!string.IsNullOrWhiteSpace(metadata.Title) &&
                (primary.IndexOf(metadata.Title, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 (!string.IsNullOrWhiteSpace(metadata.Series) && string.Equals(primary, metadata.Series, StringComparison.OrdinalIgnoreCase)) ||
                 string.Equals(primary, metadata.Title, StringComparison.OrdinalIgnoreCase)))
                return !string.IsNullOrWhiteSpace(alternate) ? alternate : primary;

            return string.IsNullOrWhiteSpace(primary) ? alternate : primary;
        }

        private static string NonNarratorAuthorCandidate(string? candidate, string? narrator)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return string.Empty;
            }

            var trimmedCandidate = candidate.Trim();
            if (!string.IsNullOrWhiteSpace(narrator) &&
                string.Equals(trimmedCandidate, narrator.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return trimmedCandidate;
        }

        private static HashSet<char> BuildPortableInvalidFileNameChars()
        {
            var invalidChars = new HashSet<char>(Path.GetInvalidFileNameChars());

            foreach (var c in "<>:\"/\\|?*")
            {
                invalidChars.Add(c);
            }

            for (int i = 0; i < 32; i++)
            {
                invalidChars.Add((char)i);
            }

            return invalidChars;
        }

        /// <summary>
        /// Windows MAX_PATH limit (260 chars including null terminator).
        /// We use 259 as the effective usable limit.
        /// </summary>
        private const int WindowsMaxPath = 259;

        /// <summary>
        /// Maximum length for a single path component (file or folder name) on NTFS / most filesystems.
        /// </summary>
        private const int MaxComponentLength = 255;

        /// <summary>
        /// Ensure the generated path does not exceed platform limits.
        /// On Windows: total path ≤ 259 chars, each component ≤ 255 chars.
        /// Truncates the longest non-root components first while preserving the file extension.
        /// </summary>
        public string EnsurePathWithinLimits(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
                return fullPath;

            // Only enforce strict limits on Windows; other platforms support much longer paths
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return fullPath;

            var originalPath = fullPath;

            // Split into root (e.g. "D:\") and component parts
            var root = Path.GetPathRoot(fullPath) ?? string.Empty;
            var withoutRoot = fullPath.Substring(root.Length);
            var parts = withoutRoot.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            if (parts.Count == 0)
                return fullPath;

            // Preserve the file extension on the last component
            var extension = Path.GetExtension(parts.Last());

            // --- Step 1: Enforce per-component limit (255 chars) ---
            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i].Length <= MaxComponentLength)
                    continue;

                // Last component (filename): keep extension
                parts[i] = i == parts.Count - 1 && !string.IsNullOrEmpty(extension)
                    ? parts[i].Substring(0, MaxComponentLength - extension.Length) + extension
                    : parts[i].Substring(0, MaxComponentLength);
            }

            // --- Step 2: Enforce total path length ---
            // Iteratively shorten the longest non-root component until within limit
            const int maxIterations = 50; // safety valve
            for (int iter = 0; iter < maxIterations; iter++)
            {
                var currentPath = root + string.Join(Path.DirectorySeparatorChar.ToString(), parts);
                if (currentPath.Length <= WindowsMaxPath)
                    break;

                var excess = currentPath.Length - WindowsMaxPath;

                // Find the longest component (prefer earlier components for ties, but skip tiny ones)
                int longestIdx = -1;
                int longestLen = 0;
                for (int i = 0; i < parts.Count; i++)
                {
                    var effectiveLen = (i == parts.Count - 1 && !string.IsNullOrEmpty(extension))
                        ? parts[i].Length - extension.Length
                        : parts[i].Length;

                    if (effectiveLen > longestLen)
                    {
                        longestLen = effectiveLen;
                        longestIdx = i;
                    }
                }

                if (longestIdx < 0 || longestLen <= 1)
                {
                    // Nothing left to truncate
                    _logger.LogWarning("Cannot shorten path below Windows MAX_PATH limit ({Limit} chars). Path length: {Length}. Path: {Path}",
                        WindowsMaxPath, currentPath.Length, currentPath);
                    break;
                }

                var part = parts[longestIdx];
                bool isFilename = longestIdx == parts.Count - 1 && !string.IsNullOrEmpty(extension);
                var nameWithoutExt = isFilename ? part.Substring(0, part.Length - extension.Length) : part;

                var newLen = Math.Max(1, nameWithoutExt.Length - excess);
                parts[longestIdx] = isFilename
                    ? nameWithoutExt.Substring(0, newLen).TrimEnd() + extension
                    : nameWithoutExt.Substring(0, newLen).TrimEnd();
            }

            var result = root + string.Join(Path.DirectorySeparatorChar.ToString(), parts);

            if (result != originalPath)
            {
                _logger.LogWarning("Path truncated to fit Windows MAX_PATH limit ({Limit} chars). Original length: {OriginalLength}, New length: {NewLength}. Truncated path: {Path}",
                    WindowsMaxPath, originalPath.Length, result.Length, result);
            }

            return result;
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

