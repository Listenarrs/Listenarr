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
using System.Text.Json;
using Listenarr.Domain.Common;

namespace Listenarr.Application.Downloads.Common
{
    /// <summary>
    /// The files a torrent's client listed under its content path, recorded while the client still
    /// lists the torrent, so the payload of a torrent it later removes can be recovered exactly.
    /// </summary>
    /// <remarks>
    /// A content path folder can also hold files the torrent never listed, so the folder alone
    /// does not say which files belong to the torrent. The names are kept relative to the recorded
    /// content path and are replaced whenever that path is.
    /// </remarks>
    internal static class RecordedTorrentContent
    {
        internal const string FilesMetadataKey = "ClientContentFiles";

        /// <summary>
        /// Records the files the client listed for the polled item, or forgets the previous list
        /// when this poll replaces the content path without one.
        /// </summary>
        public static void Record(Download download, QueueItem item, bool clientListedFiles)
        {
            // The recorded content path changes only when the item reports one, and the list
            // belongs to that path.
            if (string.IsNullOrEmpty(item.ContentPath) || string.IsNullOrEmpty(download.GetMetadataString("TorrentHash")))
            {
                return;
            }

            var names = clientListedFiles && item.SourceFiles is { } sourceFiles
                ? GetRelativeNames(item.ContentPath, sourceFiles)
                : null;
            if (names is { Count: > 0 })
            {
                download.SetMetadata(FilesMetadataKey, JsonSerializer.Serialize(names));
            }
            else
            {
                download.Metadata.Remove(FilesMetadataKey);
            }
        }

        /// <summary>
        /// Returns the recorded files under the content path, or null unless every recorded name
        /// stays inside it and still exists.
        /// </summary>
        public static List<string>? ResolveFiles(Download download, string contentPath, IFileSystem fileSystem)
        {
            var names = ReadNames(download);
            if (names is not { Count: > 0 })
            {
                return null;
            }

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentPath));
            var files = new List<string>(names.Count);
            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name) || Path.IsPathRooted(name)
                    || FileUtils.ContainsParentDirectorySegment(name, '/', '\\'))
                {
                    return null;
                }

                var file = Path.GetFullPath(Path.Join(root, name));
                if (!IsUnder(file, root) || !fileSystem.FileExists(file))
                {
                    return null;
                }

                files.Add(file);
            }

            return files;
        }

        // A multi-file torrent without a common root reports its save folder as its content
        // path, which also holds other torrents' files, so such a path identifies no payload.
        public static bool ContainsSaveFolder(string contentPath, string? saveFolder)
        {
            if (string.IsNullOrWhiteSpace(saveFolder))
            {
                return false;
            }

            var content = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentPath));
            var save = Path.TrimEndingDirectorySeparator(Path.GetFullPath(saveFolder));
            return string.Equals(content, save, StringComparison.OrdinalIgnoreCase)
                || save.StartsWith(content + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        // Null when any listed file lies outside the content path, as it does for a single-file
        // torrent whose content path is the file itself.
        private static List<string>? GetRelativeNames(string contentPath, IEnumerable<string> sourceFiles)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentPath));
            var names = new List<string>();
            foreach (var sourceFile in sourceFiles)
            {
                var file = Path.GetFullPath(sourceFile);
                if (!IsUnder(file, root))
                {
                    return null;
                }

                names.Add(Path.GetRelativePath(root, file));
            }

            return names;
        }

        private static List<string>? ReadNames(Download download)
        {
            var json = download.GetMetadataString(FilesMetadataKey);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<List<string>>(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static bool IsUnder(string file, string root)
            => file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
