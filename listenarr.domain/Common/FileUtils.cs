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
        public sealed record AudioMatchProfile(
            string FilePath,
            string StemKey,
            string TitleKey,
            string AlbumKey,
            string ArtistKey)
        {
            public string GroupKey =>
                !string.IsNullOrWhiteSpace(AlbumKey) ? AlbumKey :
                !string.IsNullOrWhiteSpace(TitleKey) ? TitleKey :
                !string.IsNullOrWhiteSpace(StemKey) ? StemKey :
                Path.GetFileName(FilePath);
        }

        /// <summary>
        /// Audio file extensions recognized by the import and scan pipelines.
        /// Centralized here so that the scan service, import service, and completed download
        /// processor all use the same set – preventing non-audio files (cover images, NFOs, etc.)
        /// from being registered as AudiobookFile records only to be removed on the next scan.
        /// </summary>
        public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".m4b", ".mp3", ".flac", ".ogg", ".opus", ".m4a", ".aac", ".wav",
            ".wv", ".wma", ".ape", ".alac", ".aif", ".aiff"
        };

        /// <summary>
        /// Returns true when the file path has a recognized audio extension.
        /// </summary>
        public static bool IsAudioFile(string filePath)
        {
            var ext = Path.GetExtension(filePath);
            return !string.IsNullOrEmpty(ext) && AudioExtensions.Contains(ext);
        }

        public static bool IsPathInvalidForCurrentOs(string? path)
        {
            return IsPathInvalidForOs(path, OperatingSystem.IsWindows());
        }

        public static bool IsPathInvalidForOs(string? path, bool isWindows)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            return isWindows && HasInvalidWindowsPathWhitespace(path);
        }

        public static HashSet<string> NormalizeExtensions(IEnumerable<string>? extensions)
        {
            var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (extensions == null)
            {
                return normalized;
            }

            foreach (var rawValue in extensions)
            {
                if (string.IsNullOrWhiteSpace(rawValue))
                {
                    continue;
                }

                var tokens = rawValue.Split(new[] { ',', ';', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var extension in tokens.Select(t => t.Trim()).Where(e => !string.IsNullOrWhiteSpace(e)))
                {
                    normalized.Add(extension.StartsWith(".", StringComparison.Ordinal) ? extension : "." + extension);
                }
            }

            return normalized;
        }

        public static string NormalizeStoredPath(string? path, Func<string, string?>? longPathResolver = null)
        {
            path ??= string.Empty;

            try
            {
                path = Path.GetFullPath(path);
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
            }

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    var resolver = longPathResolver;
                    resolver ??= TryResolveLongWindowsPath;
                    path = ExpandKnownWindowsPathSegments(path, resolver);
                }
                catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                {
                }
            }

            return path;
        }

        public static string EnsureTrailingSeparator(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }

        public static bool IsBlacklistedFile(string filePath, IEnumerable<string>? blacklistExtensions)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return true;
            }

            var extension = Path.GetExtension(filePath);
            if (string.IsNullOrWhiteSpace(extension))
            {
                return false;
            }

            blacklistExtensions ??= [];
            blacklistExtensions = blacklistExtensions.Append(".tmp");

            return blacklistExtensions != null && blacklistExtensions.ToHashSet().Contains(extension);
        }

        /// <summary>
        /// Generates a unique destination path by appending a numeric suffix.
        /// </summary>
        public static string GetUniqueDestinationPath(string desiredPath, Func<string, bool> existsPredicate, ISet<string>? inMemoryUsed = null)
        {
            try
            {
                if (!existsPredicate(desiredPath) && (inMemoryUsed == null || !inMemoryUsed.Contains(desiredPath)))
                    return desiredPath;

                var dir = Path.GetDirectoryName(desiredPath) ?? string.Empty;
                var name = Path.GetFileNameWithoutExtension(desiredPath);
                var ext = Path.GetExtension(desiredPath);
                var idx = 1;
                string candidate;
                do
                {
                    candidate = Path.Join(dir, $"{name} ({idx}){ext}");
                    idx++;
                }
                while (existsPredicate(candidate) || (inMemoryUsed != null && inMemoryUsed.Contains(candidate)));

                return candidate;
            }
            catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException)
            {
                return desiredPath;
            }
        }

        /// <summary>
        /// Appends a stable sequence suffix (e.g. "-01") to the final filename segment.
        /// Keeps any parent directories intact.
        /// </summary>
        public static string AppendSequenceSuffix(string desiredPath, int sequenceNumber)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(desiredPath))
                    return desiredPath;

                var directory = Path.GetDirectoryName(desiredPath);
                var filename = Path.GetFileNameWithoutExtension(desiredPath);
                var extension = Path.GetExtension(desiredPath);

                if (string.IsNullOrWhiteSpace(filename))
                    return desiredPath;

                var suffixed = $"{filename}-{sequenceNumber:00}{extension}";
                return string.IsNullOrWhiteSpace(directory)
                    ? suffixed
                    : Path.Join(directory, suffixed);
            }
            catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException)
            {
                return desiredPath;
            }
        }

        /// <summary>
        /// Returns true if the given childPath (either file or directory) is inside of the parentPath
        /// </summary>
        /// <param name="childPath">Path to test</param>
        /// <param name="parentPath">Supposed parent path</param>
        /// <returns>True when childPath is inside parentPath</returns>
        public static bool IsPathInsideOf(string childPath, string parentPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(childPath) || string.IsNullOrWhiteSpace(parentPath))
                    return false;

                var normalizedChild = NormalizeStoredPath(childPath);
                var normalizedRoot = NormalizeStoredPath(parentPath);

                return IsPathSameOrInside(normalizedChild, normalizedRoot)
                    && !string.Equals(
                        TrimTrailingPathSeparators(normalizedChild),
                        TrimTrailingPathSeparators(normalizedRoot),
                        GetPathComparison());
            }
            catch (Exception caughtEx_3) when (caughtEx_3 is not OperationCanceledException && caughtEx_3 is not OutOfMemoryException && caughtEx_3 is not StackOverflowException)
            {
                return false;
            }
        }

        public static bool TryResolveRelativePathWithinBase(string basePath, string relativePath, out string resolvedPath)
        {
            resolvedPath = string.Empty;

            try
            {
                if (string.IsNullOrWhiteSpace(basePath) || string.IsNullOrWhiteSpace(relativePath))
                {
                    return false;
                }

                if (ContainsRootedPathSegment(relativePath))
                {
                    return false;
                }

                var normalizedBase = Path.GetFullPath(basePath);
                var candidate = Path.GetFullPath(Path.Join(normalizedBase, NormalizePathSegmentForCombine(relativePath)));
                if (!IsPathSameOrInside(candidate, normalizedBase))
                {
                    return false;
                }

                resolvedPath = candidate;
                return true;
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                resolvedPath = string.Empty;
                return false;
            }
        }

        public static bool IsPathSameOrInside(string candidatePath, string basePath)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(basePath))
            {
                return false;
            }

            var comparison = GetPathComparison();
            var normalizedCandidate = NormalizeFullPathForBoundary(candidatePath);
            var normalizedBase = NormalizeFullPathForBoundary(basePath);

            if (string.Equals(normalizedCandidate, normalizedBase, comparison))
            {
                return true;
            }

            var baseWithSeparator = normalizedBase.EndsWith(Path.DirectorySeparatorChar)
                || normalizedBase.EndsWith(Path.AltDirectorySeparatorChar)
                    ? normalizedBase
                    : normalizedBase + Path.DirectorySeparatorChar;

            return normalizedCandidate.StartsWith(baseWithSeparator, comparison);
        }

        public static string? GetCommonDirectory(IEnumerable<string> paths)
        {
            try
            {
                var directories = paths
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path =>
                    {
                        var fullPath = NormalizeStoredPath(path);
                        return Path.GetDirectoryName(fullPath) ?? fullPath;
                    })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (directories.Count == 0)
                {
                    return null;
                }

                if (directories.Count == 1)
                {
                    return directories[0];
                }

                var commonPath = directories[0];
                foreach (var directory in directories.Skip(1))
                {
                    commonPath = GetCommonPath(commonPath, directory);
                    if (string.IsNullOrWhiteSpace(commonPath))
                    {
                        break;
                    }
                }

                return string.IsNullOrWhiteSpace(commonPath) ? directories[0] : commonPath;
            }
            catch (Exception caughtEx_4) when (caughtEx_4 is not OperationCanceledException && caughtEx_4 is not OutOfMemoryException && caughtEx_4 is not StackOverflowException)
            {
                return null;
            }
        }

        private static string GetCommonPath(string firstPath, string secondPath)
        {
            var normalizedFirst = NormalizeStoredPath(firstPath);
            var normalizedSecond = NormalizeStoredPath(secondPath);
            var minLength = Math.Min(normalizedFirst.Length, normalizedSecond.Length);
            var commonLength = 0;

            for (var i = 0; i < minLength; i++)
            {
                if (normalizedFirst[i] != normalizedSecond[i])
                {
                    break;
                }

                commonLength++;
            }

            if (commonLength == 0)
            {
                return string.Empty;
            }

            if (commonLength < normalizedFirst.Length)
            {
                var lastSeparator = normalizedFirst.LastIndexOfAny(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    Math.Max(0, commonLength - 1));
                commonLength = lastSeparator >= 0 ? lastSeparator + 1 : 0;
            }

            var commonPath = normalizedFirst.Substring(0, commonLength)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return commonPath;
        }

        private static bool IsGenericTrackLabel(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            return Regex.IsMatch(
                value,
                @"^(track|chapter|disc|cd|part|pt|foreword|afterword|preface|prologue|epilogue|introduction|intro)\b(?:\s+\d+)?$",
                RegexOptions.IgnoreCase);
        }

        private static readonly HashSet<string> GenericCompanionStemKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "art",
            "artwork",
            "back",
            "book",
            "booklet",
            "cover",
            "description",
            "folder",
            "front",
            "info",
            "metadata",
            "notes",
            "poster",
            "summary",
            "thumb",
            "thumbnail"
        };

        private static string FirstNonEmpty(params string?[] values)
        {
            return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
        }

    }
}
