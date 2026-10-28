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

namespace Listenarr.Domain.Common
{
    public static partial class FileUtils
    {
        private static readonly Regex WindowsDriveRootPattern = new("^[A-Za-z]:[\\\\/]", RegexOptions.Compiled);
        private static readonly Regex WindowsReservedDeviceNamePattern = new(
            "^(CON|PRN|AUX|NUL|COM(?:[1-9]|[¹²³])|LPT(?:[1-9]|[¹²³]))$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Validates and normalizes a user-provided directory path that Listenarr will store or create.
        /// This must not be used for externally reported download-client source paths, where whitespace
        /// and other path identity details must be preserved exactly as reported by the client.
        /// </summary>
        public static bool TryNormalizeUserProvidedDirectoryPathForCurrentOs(
            string? path,
            out string normalizedPath,
            out string reason,
            bool allowFileSystemRoot = false,
            bool rejectParentTraversal = false) =>
            TryNormalizeUserProvidedDirectoryPathForOs(
                path,
                OperatingSystem.IsWindows(),
                out normalizedPath,
                out reason,
                allowFileSystemRoot,
                rejectParentTraversal);

        public static string NormalizeRootFolderPathForStorage(string? path)
        {
            if (!TryNormalizeUserProvidedDirectoryPathForCurrentOs(
                path,
                out var normalizedPath,
                out var validationReason,
                allowFileSystemRoot: true,
                rejectParentTraversal: true))
            {
                throw new ArgumentException($"Path is not valid for this operating system: {validationReason}");
            }

            return normalizedPath;
        }

        // The explicit OS parameter lets tests verify Windows and Unix validation rules
        // from any host. Production callers should use TryNormalizeUserProvidedDirectoryPathForCurrentOs.
        public static bool TryNormalizeUserProvidedDirectoryPathForOs(
            string? path,
            bool isWindows,
            out string normalizedPath,
            out string reason,
            bool allowFileSystemRoot = false,
            bool rejectParentTraversal = false)
        {
            normalizedPath = string.Empty;
            reason = string.Empty;

            if (string.IsNullOrWhiteSpace(path))
            {
                reason = "Path is required.";
                return false;
            }

            var candidate = path;
            if (candidate.IndexOf('\0') >= 0)
            {
                reason = "Path contains invalid characters.";
                return false;
            }

            if (isWindows)
            {
                return TryNormalizeWindowsUserProvidedDirectoryPath(
                    candidate,
                    out normalizedPath,
                    out reason,
                    allowFileSystemRoot,
                    rejectParentTraversal);
            }

            return TryNormalizeUnixUserProvidedDirectoryPath(
                candidate,
                out normalizedPath,
                out reason,
                allowFileSystemRoot,
                rejectParentTraversal);
        }

        private static bool TryNormalizeWindowsUserProvidedDirectoryPath(
            string path,
            out string normalizedPath,
            out string reason,
            bool allowFileSystemRoot,
            bool rejectParentTraversal)
        {
            normalizedPath = string.Empty;
            reason = string.Empty;

            // Windows accepts \ or / as the current drive root. Root-folder configuration may
            // intentionally use that boundary, but concrete destinations must still reject it.
            if (IsWindowsCurrentDriveRoot(path))
            {
                if (!allowFileSystemRoot)
                {
                    reason = "Path cannot be the filesystem root.";
                    return false;
                }

                try
                {
                    normalizedPath = OperatingSystem.IsWindows()
                        ? Path.GetFullPath(path)
                        : path.Replace('/', '\\');
                    return true;
                }
                catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                {
                    normalizedPath = string.Empty;
                    reason = "Path is not valid for this operating system.";
                    return false;
                }
            }

            var isUncPath = HasWindowsUncPrefix(path);
            int rootLength;
            if (isUncPath)
            {
                if (!TryParseWindowsUncRoot(
                    path,
                    rejectRepeatedSeparators: true,
                    out rootLength,
                    out _,
                    out reason))
                {
                    return false;
                }
            }
            else
            {
                rootLength = GetWindowsRootLength(path);
                if (rootLength <= 0)
                {
                    reason = "Path must be an absolute directory path.";
                    return false;
                }
            }

            var pathWithoutRoot = path[rootLength..];
            if (string.IsNullOrWhiteSpace(pathWithoutRoot.Trim('/', '\\')) && !allowFileSystemRoot)
            {
                reason = "Path cannot be the filesystem root.";
                return false;
            }

            if (!ValidateWindowsUserProvidedDirectoryStructure(pathWithoutRoot, out reason)
                || !ValidateWindowsDirectorySegments(pathWithoutRoot, rejectParentTraversal, out reason))
            {
                return false;
            }

            try
            {
                normalizedPath = isUncPath || !OperatingSystem.IsWindows()
                    ? NormalizeWindowsDirectoryPathSyntax(path)
                    : Path.GetFullPath(path);

                if (IsWindowsRootOnly(normalizedPath) && !allowFileSystemRoot)
                {
                    normalizedPath = string.Empty;
                    reason = "Path cannot be the filesystem root.";
                    return false;
                }

                return true;
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                normalizedPath = string.Empty;
                reason = "Path is not valid for this operating system.";
                return false;
            }
        }

        private static bool TryNormalizeUnixUserProvidedDirectoryPath(
            string path,
            out string normalizedPath,
            out string reason,
            bool allowFileSystemRoot,
            bool rejectParentTraversal)
        {
            normalizedPath = string.Empty;
            reason = string.Empty;

            if (!path.StartsWith("/", StringComparison.Ordinal))
            {
                reason = "Path must be an absolute directory path.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(path.Trim('/')) && !allowFileSystemRoot)
            {
                reason = "Path cannot be the filesystem root.";
                return false;
            }

            if (ContainsCurrentDirectorySegment(path, '/'))
            {
                reason = "Path cannot contain current directory segments.";
                return false;
            }

            if (rejectParentTraversal && ContainsParentDirectorySegment(path, '/'))
            {
                reason = "Path cannot traverse to a parent directory.";
                return false;
            }

            try
            {
                normalizedPath = OperatingSystem.IsWindows()
                    ? NormalizeUnixDirectoryPathSyntax(path)
                    : Path.GetFullPath(path);

                if (IsUnixRootOnly(normalizedPath) && !allowFileSystemRoot)
                {
                    normalizedPath = string.Empty;
                    reason = "Path cannot be the filesystem root.";
                    return false;
                }

                return true;
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                normalizedPath = string.Empty;
                reason = "Path is not valid for this operating system.";
                return false;
            }
        }

        /// <summary>
        /// Detects values that visually look like absolute paths after accidental leading whitespace.
        /// Do not trim user-provided paths before validation because Unix path-segment whitespace is valid.
        /// </summary>
        public static bool HasLeadingWhitespaceBeforeRootedPath(string? path)
        {
            if (string.IsNullOrEmpty(path) || !char.IsWhiteSpace(path[0]))
            {
                return false;
            }

            var trimmedStart = path.TrimStart();
            return Path.IsPathRooted(trimmedStart)
                || IsWindowsCurrentDriveRoot(trimmedStart)
                || HasWindowsUncPrefix(trimmedStart)
                || GetWindowsRootLength(trimmedStart) > 0;
        }

        private static bool IsWindowsCurrentDriveRoot(string path)
        {
            return path.Length == 1 && (path[0] is '\\' or '/');
        }

        private static int GetWindowsRootLength(string path)
        {
            if (WindowsDriveRootPattern.IsMatch(path))
            {
                return 3;
            }

            return TryParseWindowsUncRoot(
                path,
                rejectRepeatedSeparators: false,
                out var rootLength,
                out _,
                out _)
                ? rootLength
                : 0;
        }

        public static bool ContainsParentDirectorySegment(string path, params char[] separators)
        {
            if (string.IsNullOrEmpty(path) || separators.Length == 0)
            {
                return false;
            }

            return path.Split(separators, StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment == "..");
        }

        private static bool ContainsCurrentDirectorySegment(string path, params char[] separators)
        {
            if (string.IsNullOrEmpty(path) || separators.Length == 0)
            {
                return false;
            }

            return path.Split(separators, StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment == ".");
        }

        private static string NormalizeWindowsDirectoryPathSyntax(string path)
        {
            var normalizedPath = path.Replace('/', '\\');
            string normalizedRoot;
            int rootLength;
            if (HasWindowsUncPrefix(normalizedPath))
            {
                if (!TryParseWindowsUncRoot(
                    normalizedPath,
                    rejectRepeatedSeparators: false,
                    out rootLength,
                    out normalizedRoot,
                    out var reason))
                {
                    throw new ArgumentException(reason, nameof(path));
                }
            }
            else
            {
                rootLength = GetWindowsRootLength(normalizedPath);
                if (rootLength <= 0)
                {
                    throw new ArgumentException("Windows path must be absolute.", nameof(path));
                }

                normalizedRoot = NormalizeWindowsRootForStorage(normalizedPath[..rootLength]);
            }

            var pathWithoutRoot = normalizedPath[rootLength..];
            var segments = new List<string>();

            foreach (var segment in pathWithoutRoot.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment == ".")
                {
                    continue;
                }

                if (segment == "..")
                {
                    if (segments.Count > 0)
                    {
                        segments.RemoveAt(segments.Count - 1);
                    }

                    continue;
                }

                segments.Add(segment);
            }

            return segments.Count == 0
                ? normalizedRoot
                : normalizedRoot.TrimEnd('\\') + "\\" + string.Join("\\", segments);
        }

        private static string NormalizeWindowsRootForStorage(string root)
        {
            if (TryParseWindowsUncRoot(
                root,
                rejectRepeatedSeparators: false,
                out _,
                out var normalizedUncRoot,
                out _))
            {
                return normalizedUncRoot;
            }

            var normalizedRoot = root.Replace('/', '\\');
            if (Regex.IsMatch(normalizedRoot, "^[A-Za-z]:$"))
            {
                return normalizedRoot + "\\";
            }

            if (Regex.IsMatch(normalizedRoot, "^[A-Za-z]:\\\\$"))
            {
                return normalizedRoot;
            }

            return normalizedRoot.TrimEnd('\\');
        }

        private static bool IsWindowsRootOnly(string path)
        {
            if (HasWindowsUncPrefix(path))
            {
                return TryParseWindowsUncRoot(
                    path,
                    rejectRepeatedSeparators: false,
                    out var rootLength,
                    out _,
                    out _)
                    && path[rootLength..].All(IsWindowsDirectorySeparator);
            }

            var pathWithWindowsSeparators = path.Replace('/', '\\');
            var normalizedPath = pathWithWindowsSeparators.TrimEnd('\\');
            return Regex.IsMatch(normalizedPath, "^[A-Za-z]:$");
        }

        private static string NormalizeUnixDirectoryPathSyntax(string path)
        {
            var segments = new List<string>();
            foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment == ".")
                {
                    continue;
                }

                if (segment == "..")
                {
                    if (segments.Count > 0)
                    {
                        segments.RemoveAt(segments.Count - 1);
                    }

                    continue;
                }

                segments.Add(segment);
            }

            return segments.Count == 0 ? "/" : "/" + string.Join("/", segments);
        }

        private static bool IsUnixRootOnly(string path)
        {
            return string.Equals(path.TrimEnd('/'), string.Empty, StringComparison.Ordinal)
                || string.Equals(path.TrimEnd('/'), "/", StringComparison.Ordinal);
        }
    }
}
