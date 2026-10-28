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
namespace Listenarr.Domain.Common
{
    public static partial class FileUtils
    {
        private static bool HasWindowsUncPrefix(string path)
        {
            return path.Length >= 2
                && IsWindowsDirectorySeparator(path[0])
                && IsWindowsDirectorySeparator(path[1]);
        }

        private static bool TryParseWindowsUncRoot(
            string path,
            bool rejectRepeatedSeparators,
            out int rootLength,
            out string normalizedRoot,
            out string reason)
        {
            rootLength = 0;
            normalizedRoot = string.Empty;
            reason = string.Empty;

            if (!HasWindowsUncPrefix(path))
            {
                reason = "Path is not a UNC path.";
                return false;
            }

            var position = 2;
            var leadingSeparatorStart = position;
            while (position < path.Length && IsWindowsDirectorySeparator(path[position]))
            {
                position++;
            }

            if (rejectRepeatedSeparators && position > leadingSeparatorStart)
            {
                reason = "Path cannot contain empty path segments.";
                return false;
            }

            var serverStart = position;
            while (position < path.Length && !IsWindowsDirectorySeparator(path[position]))
            {
                position++;
            }

            if (position == serverStart || position >= path.Length)
            {
                reason = "UNC paths require a server and share.";
                return false;
            }

            var server = path[serverStart..position];
            var serverSeparatorStart = position;
            while (position < path.Length && IsWindowsDirectorySeparator(path[position]))
            {
                position++;
            }

            if (rejectRepeatedSeparators && position - serverSeparatorStart > 1)
            {
                reason = "Path cannot contain empty path segments.";
                return false;
            }

            if (position >= path.Length)
            {
                reason = "UNC paths require a server and share.";
                return false;
            }

            var shareStart = position;
            while (position < path.Length && !IsWindowsDirectorySeparator(path[position]))
            {
                position++;
            }

            if (position == shareStart)
            {
                reason = "UNC paths require a server and share.";
                return false;
            }

            var share = path[shareStart..position];
            if (!TryValidateWindowsDirectorySegment(server, rejectParentTraversal: true, out reason)
                || !TryValidateWindowsDirectorySegment(share, rejectParentTraversal: true, out reason))
            {
                return false;
            }

            var childSeparatorStart = position;
            while (position < path.Length && IsWindowsDirectorySeparator(path[position]))
            {
                position++;
            }

            if (rejectRepeatedSeparators
                && position < path.Length
                && position - childSeparatorStart > 1)
            {
                reason = "Path cannot contain empty path segments.";
                return false;
            }

            normalizedRoot = $"\\\\{server}\\{share}";
            rootLength = position;
            return true;
        }

        private static bool ValidateWindowsUserProvidedDirectoryStructure(
            string pathWithoutRoot,
            out string reason)
        {
            reason = string.Empty;
            var pathWithoutTrailingSeparators = pathWithoutRoot.TrimEnd('\\', '/');
            if (pathWithoutTrailingSeparators.Length == 0)
            {
                return true;
            }

            var segments = pathWithoutTrailingSeparators.Split(
                new[] { '\\', '/' },
                StringSplitOptions.None);
            if (segments.Any(string.IsNullOrEmpty))
            {
                reason = "Path cannot contain empty path segments.";
                return false;
            }

            return true;
        }

        private static bool ValidateWindowsDirectorySegments(
            string pathWithoutRoot,
            bool rejectParentTraversal,
            out string reason)
        {
            reason = string.Empty;
            var segments = pathWithoutRoot.Split(
                new[] { '\\', '/' },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
            {
                if (!TryValidateWindowsDirectorySegment(segment, rejectParentTraversal, out reason))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryValidateWindowsDirectorySegment(
            string segment,
            bool rejectParentTraversal,
            out string reason)
        {
            reason = string.Empty;
            if (segment == ".")
            {
                reason = "Path cannot contain current directory segments.";
                return false;
            }

            if (segment == ".." && rejectParentTraversal)
            {
                reason = "Path cannot traverse to a parent directory.";
                return false;
            }

            if (segment == "..")
            {
                return true;
            }

            if (segment.Any(IsInvalidWindowsDirectorySegmentCharacter))
            {
                reason = "Path contains invalid characters.";
                return false;
            }

            if (segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                reason = "Path segments cannot end with a space or period on Windows.";
                return false;
            }

            var stem = segment.Split('.', 2)[0];
            if (WindowsReservedDeviceNamePattern.IsMatch(stem))
            {
                reason = "Path contains a reserved Windows device name.";
                return false;
            }

            return true;
        }

        private static bool IsInvalidWindowsDirectorySegmentCharacter(char character)
        {
            return character < 32 || character is '<' or '>' or ':' or '"' or '|' or '?' or '*';
        }

        private static bool IsWindowsDirectorySeparator(char character)
        {
            return character is '\\' or '/';
        }
    }
}
