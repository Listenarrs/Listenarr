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

using System.ComponentModel.DataAnnotations;
using Listenarr.Domain.Common;

namespace Listenarr.Domain.Downloads
{
    /// <summary>
    /// Represents a path mapping between a download client's path and Listenarr's path.
    /// Used when download clients run in different containers/systems with different mount points.
    /// </summary>
    public class RemotePathMapping
    {
        [Key]
        public int Id { get; set; }

        /// <summary>
        /// The download client this mapping applies to
        /// </summary>
        [Required]
        public string DownloadClientId { get; set; } = string.Empty;

        /// <summary>
        /// User-friendly name for this mapping
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// The path as seen by the download client (e.g., "/downloads/listenarr/")
        /// </summary>
        [Required]
        public string RemotePath
        {
            get
            {
                return NormalizeRemotePath(field);
            }
            set
            {
                field = NormalizeRemotePath(value);
            }
        } = string.Empty;

        /// <summary>
        /// The path as seen by Listenarr (e.g., "/server/downloads/complete/listenarr/")
        /// </summary>
        [Required]
        public string LocalPath { get; set; } = string.Empty;

        /// <summary>
        /// When this mapping was created
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When this mapping was last updated
        /// </summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // FIXME: Not OOP, remove me
        public RemotePathMapping()
        {
        }

        public RemotePathMapping(
            string downloadClientId,
            string remotePath,
            string localPath,
            string name)
        {
            DownloadClientId = downloadClientId;
            LocalPath = localPath;
            RemotePath = remotePath;
            Name = name;
        }

        /// <summary>
        /// Normalize path separators for consistency
        /// </summary>
        public void NormalizePaths()
        {
            RemotePath = NormalizeRemotePath(RemotePath);
            LocalPath = NormalizeLocalPathForHost(LocalPath);
        }

        private static string NormalizeLocalPathForHost(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            if (!FileSystemPathIdentity.TryCanonicalizeStoredAbsolutePathForHost(
                    path,
                    out var canonicalPath,
                    out var reason))
            {
                throw new ArgumentException(reason, nameof(path));
            }

            return FileUtils.EnsureTrailingSeparator(canonicalPath);
        }

        private static string NormalizeRemotePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            if (path.StartsWith("\\\\", StringComparison.Ordinal))
            {
                var uncPath = path.Replace('/', '\\');
                return uncPath.EndsWith('\\') ? uncPath : uncPath + "\\";
            }

            if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
            {
                var drivePath = path.Replace('\\', '/');
                return drivePath.EndsWith('/') ? drivePath : drivePath + '/';
            }

            return path.EndsWith('/') ? path : path + '/';
        }
    }
}
