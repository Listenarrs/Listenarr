using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Listenarr.Domain.Models;

namespace Listenarr.Api.Services
{
    public class FileNamingService : IFileNamingService
    {
        private readonly IConfigurationService _configService;
        private readonly ILogger<FileNamingService> _logger;

        public FileNamingService(IConfigurationService configService, ILogger<FileNamingService> logger)
        {
            _configService = configService;
            _logger = logger;
        }

        /// <summary>
        /// Apply the configured file naming pattern to generate the final file path
        /// </summary>
        public async Task<string> GenerateFilePathAsync(
            AudioMetadata metadata,
            int? diskNumber = null,
            int? chapterNumber = null,
            string originalExtension = ".m4b")
        {
            var settings = await _configService.GetApplicationSettingsAsync() ?? new ApplicationSettings();
            var folderPattern = settings.FolderNamingPattern;
            
            // Determine if this is a multi-file import (has disk or chapter number)
            bool isMultiFile = diskNumber.HasValue || chapterNumber.HasValue;
            var filePattern = isMultiFile 
                ? settings.MultiFileNamingPattern 
                : settings.FileNamingPattern;
            
            var outputPath = settings.OutputPath;

            // Helper to pick the first non-empty value
            string FirstNonEmpty(params string?[] candidates)
            {
                foreach (var c in candidates)
                {
                    if (!string.IsNullOrWhiteSpace(c)) return c!;
                }
                return string.Empty;
            }

            // Build variable dictionary
            // Heuristic: sometimes metadata.Artist can contain the title/series (noisy tags).
            // Prefer an AlbumArtist or alternate artist value if the primary artist looks like the title/series.
            string ChooseAuthor(AudioMetadata md)
            {
                var primary = FirstNonEmpty(md.Artist, md.AlbumArtist);
                var alternate = FirstNonEmpty(md.AlbumArtist, md.Artist);

                if (!string.IsNullOrWhiteSpace(primary) && !string.IsNullOrWhiteSpace(md.Title))
                {
                    // If the primary artist contains the title or equals the series/title, prefer the alternate.
                    if (primary.IndexOf(md.Title, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (!string.IsNullOrWhiteSpace(md.Series) && string.Equals(primary, md.Series, StringComparison.OrdinalIgnoreCase)) ||
                        string.Equals(primary, md.Title, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(alternate)) return alternate;
                        return primary;
                    }
                }

                return string.IsNullOrWhiteSpace(primary) ? alternate : primary;
            }

            var variables = new Dictionary<string, object>
            {
                // Keep multi-word author names as a single folder name (e.g. "Jane Austen")
                { "Author", SanitizePathComponent(FirstNonEmpty(ChooseAuthor(metadata), "Unknown Author")) },
                // For Series we must not fallback to Album or Title - when Series is blank we want
                // the variable to be empty so ApplyNamingPattern can remove any adjacent separators
                { "Series", string.IsNullOrWhiteSpace(metadata.Series) ? string.Empty : SanitizePathComponent(metadata.Series) },
                { "Title", SanitizePathComponent(FirstNonEmpty(metadata.Title, "Unknown Title")) },
                { "SeriesNumber", FirstNonEmpty(metadata.SeriesPosition?.ToString(), metadata.TrackNumber?.ToString()) },
                { "Year", FirstNonEmpty(metadata.Year?.ToString()) },
                { "Quality", FirstNonEmpty((metadata.Bitrate.HasValue ? metadata.Bitrate.ToString() + "kbps" : null), metadata.Format) },
                { "DiskNumber", FirstNonEmpty(diskNumber?.ToString(), metadata.DiscNumber?.ToString()) },
                { "ChapterNumber", FirstNonEmpty(chapterNumber?.ToString(), metadata.TrackNumber?.ToString()) }
            };

            // Diagnostic logging: record the variables used for pattern replacement
            try
            {
                var dbg = string.Join(", ", variables.Select(kv => $"{kv.Key}='{kv.Value}'"));
                _logger.LogInformation("FileNamingService variables: {Vars}", dbg);
            }
            catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) {
                // ignore logging errors
                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }

            string relativePath;
            if (string.IsNullOrWhiteSpace(folderPattern))
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

                var folderRelative = ApplyNamingPattern(folderPattern, variables, treatAsFilename: false);
                
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

            // Combine with output path if configured
            var fullPath = string.IsNullOrWhiteSpace(outputPath)
                ? relativePath
                : CombineWithOptionalBase(outputPath, relativePath);

            fullPath = EnsurePathWithinLimits(fullPath);

            _logger.LogInformation("Generated file path: {FilePath}", fullPath);
            return fullPath;
        }

        /// <summary>
        /// Apply the configured file naming pattern to generate the final file path with a specific output path
        /// </summary>
        public async Task<string> GenerateFilePathAsync(
            AudioMetadata metadata,
            string outputPath,
            int? diskNumber = null,
            int? chapterNumber = null,
            string originalExtension = ".m4b")
        {
            var settings = await _configService.GetApplicationSettingsAsync() ?? new ApplicationSettings();
            var folderPattern = settings.FolderNamingPattern;
            
            // Determine if this is a multi-file import (has disk or chapter number)
            bool isMultiFile = diskNumber.HasValue || chapterNumber.HasValue;
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
            catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException) {
                // If paths are invalid, fall back to configured folder pattern
                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }

            // Helper to pick the first non-empty value
            string FirstNonEmpty(params string?[] candidates)
            {
                foreach (var c in candidates)
                {
                    if (!string.IsNullOrWhiteSpace(c)) return c!;
                }
                return string.Empty;
            }

            // Build variable dictionary
            string ChooseAuthor2(AudioMetadata md)
            {
                var primary = FirstNonEmpty(md.Artist, md.AlbumArtist);
                var alternate = FirstNonEmpty(md.AlbumArtist, md.Artist);

                if (!string.IsNullOrWhiteSpace(primary) && !string.IsNullOrWhiteSpace(md.Title))
                {
                    if (primary.IndexOf(md.Title, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (!string.IsNullOrWhiteSpace(md.Series) && string.Equals(primary, md.Series, StringComparison.OrdinalIgnoreCase)) ||
                        string.Equals(primary, md.Title, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(alternate)) return alternate;
                        return primary;
                    }
                }

                return string.IsNullOrWhiteSpace(primary) ? alternate : primary;
            }

            var variables = new Dictionary<string, object>
            {
                { "Author", SanitizePathComponent(FirstNonEmpty(ChooseAuthor2(metadata), "Unknown Author")) },
                // Same behavior for overload with custom outputPath: do not fallback for Series
                { "Series", string.IsNullOrWhiteSpace(metadata.Series) ? string.Empty : SanitizePathComponent(metadata.Series) },
                { "Title", SanitizePathComponent(FirstNonEmpty(metadata.Title, "Unknown Title")) },
                { "SeriesNumber", FirstNonEmpty(metadata.SeriesPosition?.ToString(), metadata.TrackNumber?.ToString()) },
                { "Year", FirstNonEmpty(metadata.Year?.ToString()) },
                { "Quality", FirstNonEmpty((metadata.Bitrate.HasValue ? metadata.Bitrate.ToString() + "kbps" : null), metadata.Format) },
                { "DiskNumber", FirstNonEmpty(diskNumber?.ToString(), metadata.DiscNumber?.ToString()) },
                { "ChapterNumber", FirstNonEmpty(chapterNumber?.ToString(), metadata.TrackNumber?.ToString()) }
            };

            // Diagnostic logging: record the variables used for pattern replacement (custom outputPath overload)
            try
            {
                var dbg = string.Join(", ", variables.Select(kv => $"{kv.Key}='{kv.Value}'"));
                _logger.LogInformation("FileNamingService variables (custom outputPath): {Vars}", dbg);
            }
            catch (Exception caughtEx_3) when (caughtEx_3 is not OperationCanceledException && caughtEx_3 is not OutOfMemoryException && caughtEx_3 is not StackOverflowException) {
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

            _logger.LogInformation("Generated file path with custom output path: {FilePath}", fullPath);
            return fullPath;
        }

        /// <summary>
        /// Parse a naming pattern and replace variables with actual values
        /// </summary>
        public string ApplyNamingPattern(string pattern, Dictionary<string, object> variables, bool treatAsFilename = false)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return "Unknown";
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

                    // Apply formatting if specified
                    if (!string.IsNullOrEmpty(format))
                    {
                        // For numeric values with format (e.g., {DiskNumber:00})
                        if (value is int intValue)
                        {
                            return intValue.ToString(format);
                        }
                        else if (int.TryParse(value.ToString(), out var parsedInt))
                        {
                            return parsedInt.ToString(format);
                        }
                    }

                    return value.ToString() ?? string.Empty;
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

        /// <summary>
        /// Remove invalid characters from path components
        /// </summary>
        private string SanitizePathComponent(string pathComponent)
        {
            if (string.IsNullOrWhiteSpace(pathComponent))
            {
                return "Unknown";
            }

            // Get invalid filename characters
            var invalidChars = Path.GetInvalidFileNameChars();

            // Replace invalid characters with underscore
            var sanitized = new StringBuilder();
            foreach (var c in pathComponent)
            {
                if (invalidChars.Contains(c))
                {
                    sanitized.Append('_');
                }
                else
                {
                    sanitized.Append(c);
                }
            }

            // Trim and handle edge cases
            var result = sanitized.ToString().Trim();

            // Ensure it's not empty after sanitization
            if (string.IsNullOrWhiteSpace(result))
            {
                return "Unknown";
            }

            return result;
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
        internal string EnsurePathWithinLimits(string fullPath)
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

