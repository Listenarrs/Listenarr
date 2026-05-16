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
using Listenarr.Domain.Models;

namespace Listenarr.Domain.Common
{
    public static class FileUtils
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
            if (string.IsNullOrWhiteSpace(path))
            {
                path = string.Empty;
            }

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

        public static string NormalizeComparisonValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = Regex.Replace(value, @"[^\p{L}\p{Nd}]+", " ");
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
            return normalized.ToLowerInvariant();
        }

        public static bool ValuesOverlap(string? left, string? right)
        {
            var normalizedLeft = NormalizeComparisonValue(left);
            var normalizedRight = NormalizeComparisonValue(right);
            if (string.IsNullOrWhiteSpace(normalizedLeft) || string.IsNullOrWhiteSpace(normalizedRight))
            {
                return false;
            }

            return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase)
                || normalizedLeft.Contains(normalizedRight, StringComparison.OrdinalIgnoreCase)
                || normalizedRight.Contains(normalizedLeft, StringComparison.OrdinalIgnoreCase);
        }

        public static string ExtractComparableAudioStem(string filePath)
        {
            var name = Path.GetFileNameWithoutExtension(filePath) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            name = Regex.Replace(name, @"^(track|chapter|disc|cd|part|pt)\s*\d+[\s\-_\.]*", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"^\d+[\s\-_\.]*", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"[\s\-_]*(part|track|chapter|disc|cd|pt)\s*\d+$", "", RegexOptions.IgnoreCase);

            var normalized = NormalizeComparisonValue(name);
            return IsGenericTrackLabel(normalized) ? string.Empty : normalized;
        }

        public static AudioMatchProfile CreateAudioMatchProfile(string filePath, AudioMetadata? metadata)
        {
            var titleKey = NormalizeComparisonValue(metadata?.Title);
            if (IsGenericTrackLabel(titleKey))
            {
                titleKey = string.Empty;
            }

            var albumKey = NormalizeComparisonValue(metadata?.Album);
            var artistKey = NormalizeComparisonValue(FirstNonEmpty(metadata?.Artist, metadata?.AlbumArtist));
            var stemKey = ExtractComparableAudioStem(filePath);

            return new AudioMatchProfile(filePath, stemKey, titleKey, albumKey, artistKey);
        }

        public static bool LikelyMatchesAnyReference(AudioMatchProfile candidate, IReadOnlyCollection<AudioMatchProfile> references)
        {
            foreach (var reference in references)
            {
                var artistConflict = !string.IsNullOrWhiteSpace(candidate.ArtistKey)
                    && !string.IsNullOrWhiteSpace(reference.ArtistKey)
                    && !ValuesOverlap(candidate.ArtistKey, reference.ArtistKey);
                if (artistConflict)
                {
                    continue;
                }

                var identityMatch =
                    ValuesOverlap(candidate.AlbumKey, reference.AlbumKey)
                    || ValuesOverlap(candidate.AlbumKey, reference.TitleKey)
                    || ValuesOverlap(candidate.AlbumKey, reference.StemKey)
                    || ValuesOverlap(candidate.TitleKey, reference.AlbumKey)
                    || ValuesOverlap(candidate.TitleKey, reference.TitleKey)
                    || ValuesOverlap(candidate.TitleKey, reference.StemKey)
                    || ValuesOverlap(candidate.StemKey, reference.AlbumKey)
                    || ValuesOverlap(candidate.StemKey, reference.TitleKey)
                    || ValuesOverlap(candidate.StemKey, reference.StemKey);

                if (identityMatch)
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsLikelyRelatedCompanionFile(
            string filePath,
            IReadOnlyCollection<AudioMatchProfile> references,
            IReadOnlyCollection<string> referenceDirectories)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            if (references == null || references.Count == 0)
            {
                return true;
            }

            var candidate = CreateAudioMatchProfile(filePath, null);
            if (LikelyMatchesAnyReference(candidate, references))
            {
                return true;
            }

            var candidateDirectory = Path.GetDirectoryName(filePath) ?? string.Empty;
            var sharesReferenceDirectory = referenceDirectories.Any(directory =>
                !string.IsNullOrWhiteSpace(directory)
                && (string.Equals(
                        NormalizeStoredPath(candidateDirectory),
                        NormalizeStoredPath(directory),
                        StringComparison.OrdinalIgnoreCase)
                    || IsPathInsideOf(candidateDirectory, directory)
                    || IsPathInsideOf(directory, candidateDirectory)));

            if (!sharesReferenceDirectory)
            {
                return false;
            }

            var genericStem = NormalizeComparisonValue(Path.GetFileNameWithoutExtension(filePath));
            return GenericCompanionStemKeys.Contains(genericStem);
        }

        public static int ScoreAgainstTarget(AudioMatchProfile candidate, string? targetTitle, string? targetAlbum, string? targetArtist)
        {
            var score = 0;
            if (ValuesOverlap(candidate.AlbumKey, targetTitle)
                || ValuesOverlap(candidate.TitleKey, targetTitle)
                || ValuesOverlap(candidate.StemKey, targetTitle))
            {
                score += 3;
            }

            if (ValuesOverlap(candidate.AlbumKey, targetAlbum)
                || ValuesOverlap(candidate.TitleKey, targetAlbum)
                || ValuesOverlap(candidate.StemKey, targetAlbum))
            {
                score += 2;
            }

            if (ValuesOverlap(candidate.ArtistKey, targetArtist))
            {
                score += 1;
            }

            return score;
        }

        /// <summary>
        /// Generate a unique destination path by appending " (1)", " (2)", ... before the extension
        /// when the candidate already exists either on disk or in an in-memory set of used paths.
        /// </summary>
        public static string GetUniqueDestinationPath(string desiredPath, Func<string, bool>? existsPredicate = null, ISet<string>? inMemoryUsed = null)
        {
            try
            {
                existsPredicate ??= File.Exists;

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
                var normalizedRoot = NormalizeStoredPath(parentPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;

                return normalizedChild.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception caughtEx_3) when (caughtEx_3 is not OperationCanceledException && caughtEx_3 is not OutOfMemoryException && caughtEx_3 is not StackOverflowException)
            {
                return false;
            }
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

        public static void DeleteEmptyDirectories(string rootPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
                {
                    return;
                }

                foreach (var directory in Directory.GetDirectories(rootPath, "*", SearchOption.AllDirectories)
                    .OrderByDescending(path => path.Length))
                {
                    TryDeleteDirectoryIfEmpty(directory);
                }

                TryDeleteDirectoryIfEmpty(rootPath);
            }
            catch (Exception caughtEx_5) when (caughtEx_5 is not OperationCanceledException && caughtEx_5 is not OutOfMemoryException && caughtEx_5 is not StackOverflowException)
            {
                System.Diagnostics.Debug.WriteLine($"Suppressed empty-directory cleanup failure for '{rootPath}': {caughtEx_5.Message}");
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

            if (Directory.Exists(commonPath))
            {
                return commonPath;
            }

            return Directory.GetParent(commonPath)?.FullName ?? commonPath;
        }

        private static void TryDeleteDirectoryIfEmpty(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                if (Directory.EnumerateFileSystemEntries(path).Any())
                {
                    return;
                }

                Directory.Delete(path, recursive: false);
            }
            catch (Exception caughtEx_6) when (caughtEx_6 is not OperationCanceledException && caughtEx_6 is not OutOfMemoryException && caughtEx_6 is not StackOverflowException)
            {
                System.Diagnostics.Debug.WriteLine($"Suppressed empty-directory delete failure for '{path}': {caughtEx_6.Message}");
            }
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
            var forceResolve = longPathResolver != TryResolveLongWindowsPath;

            foreach (var normalizedSegment in segments.Select(NormalizeRelativePathSegment))
            {
                var candidatePath = Path.Join(currentPath, normalizedSegment);
                var canResolve = forceResolve || Directory.Exists(candidatePath) || File.Exists(candidatePath);
                if (!canResolve)
                {
                    currentPath = candidatePath;
                    continue;
                }

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
            string root = Path.GetPathRoot(Directory.GetCurrentDirectory()) ?? "/";
            return Path.Combine(root, Path.Combine(segments));
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

