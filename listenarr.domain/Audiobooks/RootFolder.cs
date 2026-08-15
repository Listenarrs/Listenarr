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

namespace Listenarr.Domain.Audiobooks
{
    public class RootFolder
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [MaxLength(200)]
        public string Name { get; set; } = string.Empty;

        [Required]
        [MaxLength(1000)]
        public string Path { get; set; } = string.Empty;

        public bool IsDefault { get; set; } = false;

        public FileSystemCaseSensitivityMode CaseSensitivityMode { get; set; } = FileSystemCaseSensitivityMode.Auto;

        public FileSystemCaseSensitivity ResolvedCaseSensitivity { get; set; } = FileSystemCaseSensitivity.Unknown;

        [MaxLength(128)]
        public string? PathIdentityKey { get; set; }

        public PathIdentityState PathIdentityState { get; set; } = PathIdentityState.Unavailable;

        public int? DirectoryObjectIdentityVersion { get; set; }

        [MaxLength(256)]
        public string? DirectoryObjectIdentity { get; set; }

        [MaxLength(1024)]
        public string? DirectoryObjectIdentityUnavailableReason { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAt { get; set; }

        public ICollection<RootFolderRelocation> Relocations { get; set; } = new List<RootFolderRelocation>();
    }
}
