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
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Common;
using Listenarr.Domain.Models;
using Listenarr.Domain.Models.Configurations;
using Listenarr.Domain.Models.Naming;
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

            var options = new NamingOptions
            {
                OutputRoot = string.IsNullOrWhiteSpace(outputPath) ? settings.OutputPath : outputPath,
                // A custom output root means the destination folder is already chosen -> file pattern only.
                IsCustomBasePath = IsCustomOutputRoot(outputPath, settings.OutputPath),
                IsMultiFile = metadata.DiscNumber.HasValue || metadata.TrackNumber.HasValue,
                SequenceNumber = null,
                Extension = originalExtension,
            };

            var result = BuildPath(NamingContext.From(metadata), settings, options);
            _logger.LogInformation("Generated file path: {FilePath}", result.FullPath);
            return result.FullPath;
        }

        public string BuildDirectory(NamingContext context, ApplicationSettings settings)
        {
            // Folder-only computation (the audiobook's BasePath). Fall back to the file pattern when no
            // folder pattern is configured, then rely on ApplyNamingPattern for empty-token cleanup.
            var folderPattern = string.IsNullOrWhiteSpace(settings.FolderNamingPattern)
                ? settings.FileNamingPattern
                : settings.FolderNamingPattern;
            var variables = BuildVariables(context);
            return ApplyNamingPattern(folderPattern ?? string.Empty, variables, treatAsFilename: false);
        }

        public NamingResult BuildPath(NamingContext context, ApplicationSettings settings, NamingOptions options)
        {
            var variables = BuildVariables(context);
            var folderPattern = settings.FolderNamingPattern;
            var filePattern = options.IsMultiFile ? settings.MultiFileNamingPattern : settings.FileNamingPattern;

            var patternHasNumberTokens = !string.IsNullOrWhiteSpace(filePattern)
                && (filePattern.IndexOf("DiskNumber", StringComparison.OrdinalIgnoreCase) >= 0
                    || filePattern.IndexOf("ChapterNumber", StringComparison.OrdinalIgnoreCase) >= 0);

            string relativePath;
            if (string.IsNullOrWhiteSpace(folderPattern))
            {
                // Legacy behavior: use the file naming pattern as the full relative path.
                var legacyPattern = string.IsNullOrWhiteSpace(filePattern)
                    ? "{Author}/{Series}/{Title}"
                    : filePattern;
                relativePath = ApplyNamingPattern(legacyPattern, variables, treatAsFilename: false);
            }
            else if (options.IsCustomBasePath)
            {
                // The destination folder is already decided; apply the file pattern only.
                var effectiveFilePattern = string.IsNullOrWhiteSpace(filePattern) ? "{Title}" : filePattern;
                relativePath = ApplyNamingPattern(effectiveFilePattern, variables, treatAsFilename: !PatternAllowsSubfolders(effectiveFilePattern));
            }
            else
            {
                // Separate folder and file patterns.
                var effectiveFilePattern = string.IsNullOrWhiteSpace(filePattern) ? "{Title}" : filePattern;

                var folderRelative = ApplyNamingPattern(folderPattern, variables, treatAsFilename: false);
                if (!string.IsNullOrWhiteSpace(folderRelative))
                {
                    folderRelative = folderRelative.Replace('/', Path.DirectorySeparatorChar)
                                                   .Replace('\\', Path.DirectorySeparatorChar);
                }

                var fileRelative = ApplyNamingPattern(effectiveFilePattern, variables, treatAsFilename: !PatternAllowsSubfolders(effectiveFilePattern));
                if (options.IsMultiFile && !patternHasNumberTokens && options.SequenceNumber.HasValue)
                    fileRelative = FileUtils.AppendSequenceSuffix(fileRelative, options.SequenceNumber.Value);

                relativePath = string.IsNullOrWhiteSpace(folderRelative)
                    ? fileRelative
                    : CombineWithOptionalBase(folderRelative, fileRelative);
            }

            // For the legacy and custom-base branches the multi-file suffix is appended to the whole path.
            if ((string.IsNullOrWhiteSpace(folderPattern) || options.IsCustomBasePath)
                && options.IsMultiFile && !patternHasNumberTokens && options.SequenceNumber.HasValue)
            {
                relativePath = FileUtils.AppendSequenceSuffix(relativePath, options.SequenceNumber.Value);
            }

            if (!relativePath.EndsWith(options.Extension, StringComparison.OrdinalIgnoreCase))
                relativePath += options.Extension;

            var fullPath = string.IsNullOrWhiteSpace(options.OutputRoot)
                ? relativePath
                : CombineWithOptionalBase(options.OutputRoot, relativePath);

            fullPath = EnsurePathWithinLimits(fullPath);
            return new NamingResult(relativePath, fullPath);
        }

        private static bool PatternAllowsSubfolders(string pattern)
            => pattern.IndexOf("DiskNumber", StringComparison.OrdinalIgnoreCase) >= 0
                || pattern.IndexOf("ChapterNumber", StringComparison.OrdinalIgnoreCase) >= 0
                || pattern.IndexOf('/') >= 0
                || pattern.IndexOf('\\') >= 0;

        private static bool IsCustomOutputRoot(string? outputPath, string? configuredOutput)
        {
            if (string.IsNullOrWhiteSpace(outputPath) || string.IsNullOrWhiteSpace(configuredOutput))
                return false;
            try
            {
                return !string.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(configuredOutput), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                return false;
            }
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

        public string ApplyNamingPattern(string pattern, NamingContext context, bool treatAsFilename = false)
        {
            var variables = BuildVariables(context);
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

        // Unified variable builder used by the orchestrator (BuildDirectory/BuildPath). All flows map their
        // source type into a NamingContext, so token sanitization and empty-handling live in one place.
        // The dictionary is case-insensitive so patterns like {author} resolve as well as {Author}.
        private Dictionary<string, object> BuildVariables(NamingContext context)
        {
            var author = context.Authors?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
            var narrator = context.Narrators != null
                ? string.Join(", ", context.Narrators.Where(n => !string.IsNullOrWhiteSpace(n)))
                : string.Empty;

            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                { "Author", SanitizePathComponent(FirstNonEmpty(author, "Unknown Author")) },
                { "Series", string.IsNullOrWhiteSpace(context.Series) ? string.Empty : SanitizePathComponent(context.Series) },
                { "Title", SanitizePathComponent(FirstNonEmpty(context.Title, "Unknown Title")) },
                { "Subtitle", string.IsNullOrWhiteSpace(context.Subtitle) ? string.Empty : SanitizePathComponent(context.Subtitle) },
                { "Edition", string.IsNullOrWhiteSpace(context.Edition) ? string.Empty : SanitizePathComponent(context.Edition) },
                { "Narrator", string.IsNullOrWhiteSpace(narrator) ? string.Empty : SanitizePathComponent(narrator) },
                { "Publisher", string.IsNullOrWhiteSpace(context.Publisher) ? string.Empty : SanitizePathComponent(context.Publisher) },
                { "Language", string.IsNullOrWhiteSpace(context.Language) ? string.Empty : SanitizePathComponent(context.Language) },
                { "Asin", string.IsNullOrWhiteSpace(context.Asin) ? string.Empty : SanitizePathComponent(context.Asin) },
                { "SeriesNumber", context.SeriesNumber ?? string.Empty },
                { "Year", context.Year ?? string.Empty },
                { "Quality", context.Quality ?? string.Empty },
                { "DiskNumber", context.DiskNumber?.ToString() ?? string.Empty },
                { "ChapterNumber", context.ChapterNumber?.ToString() ?? string.Empty }
            };
        }

        private static string FirstNonEmpty(params string?[] candidates)
        {
            return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? string.Empty;
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

