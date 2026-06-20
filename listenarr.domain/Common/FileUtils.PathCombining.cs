/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Listenarr.Domain.Common
{
    public static partial class FileUtils
    {
        private static string ExpandKnownWindowsPathSegments(string path, Func<string, string?> longPathResolver)
        {
            var pathRoot = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(pathRoot))
            {
                return path;
            }

            var remainingPath = path[pathRoot.Length..];
            if (string.IsNullOrEmpty(remainingPath))
            {
                return path;
            }

            var hasTrailingSeparator = path.EndsWith(Path.DirectorySeparatorChar)
                || path.EndsWith(Path.AltDirectorySeparatorChar);

            var segments = remainingPath
                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

            var currentPath = pathRoot;
            foreach (var normalizedSegment in segments.Select(NormalizeRelativePathSegment))
            {
                var candidatePath = Path.Join(currentPath, normalizedSegment);
                var resolvedPath = longPathResolver(candidatePath);
                currentPath = string.IsNullOrWhiteSpace(resolvedPath) ? candidatePath : resolvedPath!;
            }

            if (hasTrailingSeparator && !currentPath.EndsWith(Path.DirectorySeparatorChar) && !currentPath.EndsWith(Path.AltDirectorySeparatorChar))
            {
                currentPath += Path.DirectorySeparatorChar;
            }

            return currentPath;
        }

        private static string NormalizeRelativePathSegment(string segment)
        {
            if (string.IsNullOrEmpty(segment))
            {
                return string.Empty;
            }

            var normalizedSegment = segment.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Path.IsPathRooted(normalizedSegment))
            {
                return normalizedSegment;
            }

            var root = Path.GetPathRoot(normalizedSegment);
            if (!string.IsNullOrEmpty(root) && normalizedSegment.Length >= root.Length)
            {
                normalizedSegment = normalizedSegment[root.Length..];
            }

            return normalizedSegment.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string? TryResolveLongWindowsPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            if (!OperatingSystem.IsWindows())
            {
                return path;
            }

            var buffer = new StringBuilder(Math.Max(260, path.Length + 16));
            var result = GetLongPathName(path, buffer, buffer.Capacity);
            if (result == 0)
            {
                return null;
            }

            if (result > buffer.Capacity)
            {
                buffer = new StringBuilder((int)result);
                result = GetLongPathName(path, buffer, buffer.Capacity);
                if (result == 0)
                {
                    return null;
                }
            }

            return buffer.ToString();
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetLongPathName(string shortPath, StringBuilder longPathBuffer, int bufferLength);

        /// <summary>
        /// Returns a path composed of all the given segments. The returned path is OS agnostic (C:\ or /)
        /// </summary>
        public static string GetAbsolutePath(params string[] segments)
        {
            string root = Path.GetPathRoot(Environment.CurrentDirectory) ?? "/";
            return Path.Combine(root, Path.Combine(segments));
        }

        /// <summary>
        /// Joins relative path segments onto a base path without allowing rooted child segments.
        /// Leading separators on child segments are treated as relative separators.
        /// </summary>
        public static string CombineRelativePath(string basePath, params string[] segments)
        {
            if (string.IsNullOrWhiteSpace(basePath))
            {
                throw new ArgumentException("Base path is required.", nameof(basePath));
            }

            var combined = TrimTrailingPathSeparators(basePath);
            foreach (var segment in segments)
            {
                if (string.IsNullOrWhiteSpace(segment))
                {
                    continue;
                }

                var relativeSegment = NormalizePathSegmentForCombine(segment);
                if (Path.IsPathRooted(relativeSegment))
                {
                    throw new ArgumentException("Path segments must be relative.", nameof(segments));
                }

                combined = string.IsNullOrEmpty(combined)
                    ? relativeSegment
                    : combined + Path.DirectorySeparatorChar + relativeSegment;
            }

            return combined;
        }

        private static string NormalizePathSegmentForCombine(string segment)
        {
            var normalized = TrimLeadingPathSeparators(segment);
            return normalized
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
        }

        private static bool ContainsRootedPathSegment(string path)
        {
            if (Path.IsPathRooted(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return true;
            }

            var segments = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            return segments.Any(segment => Regex.IsMatch(segment, @"^[A-Za-z]:$"));
        }

        private static string TrimLeadingPathSeparators(string path)
            => path.TrimStart('/', '\\');

        private static string TrimTrailingPathSeparators(string path)
            => path.TrimEnd('/', '\\');

        private static StringComparison GetPathComparison()
            => OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        private static StringComparer GetPathStringComparer()
            => OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

        private static string NormalizeFullPathForBoundary(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrEmpty(root)
                && string.Equals(
                    fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    GetPathComparison()))
            {
                return root;
            }

            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool HasInvalidWindowsPathWhitespace(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return true;
            }

            var root = Path.GetPathRoot(path);
            var pathWithoutRoot = !string.IsNullOrEmpty(root) && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? path[root.Length..]
                : path;

            return pathWithoutRoot
                .Split(new[] { '\\', '/' }, StringSplitOptions.None)
                .Any(IsInvalidWindowsPathSegmentWhitespace);
        }

        private static bool IsInvalidWindowsPathSegmentWhitespace(string segment)
        {
            if (string.IsNullOrEmpty(segment) || segment == "." || segment == "..")
            {
                return false;
            }

            return segment.EndsWith(' ') || segment.EndsWith('.');
        }

        /// <summary>
        /// Create a filesystem-safe name from arbitrary text by removing invalid path characters
        /// and normalizing whitespace. Keeps it conservative to avoid unexpected folder creation.
        /// </summary>
        public static string SafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "unknown";
            // Remove invalid path chars
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(name.Where(c => !invalid.Contains(c)).ToArray());
            // Replace sequences of non-alphanumeric characters with single space
            var normalized = System.Text.RegularExpressions.Regex.Replace(cleaned, "[^A-Za-z0-9]+", " ");
            normalized = normalized.Trim();
            return normalized.Length == 0 ? "unknown" : normalized;
        }

        public static string CombineWithOptionalBase(string? basePath, string candidatePath)
        {
            if (string.IsNullOrEmpty(candidatePath))
            {
                return candidatePath;
            }

            if (Path.IsPathRooted(candidatePath) || string.IsNullOrWhiteSpace(basePath))
            {
                return candidatePath;
            }

            var relativePath = candidatePath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Path.IsPathRooted(relativePath))
            {
                return relativePath;
            }

            var normalizedBasePath = basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrEmpty(normalizedBasePath)
                ? relativePath
                : Path.Join(normalizedBasePath, relativePath);
        }
    }
}
