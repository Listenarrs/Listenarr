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

namespace Listenarr.Application.SystemDiagnostics.Contracts
{
    /// <summary>
    /// Measures free and total bytes for a filesystem path, abstracting the
    /// platform-specific mechanism (Windows native call vs. <c>DriveInfo</c>) so the
    /// caller does not branch on operating system and the behavior is unit-testable.
    /// </summary>
    public interface IDiskSpaceProbe
    {
        /// <summary>
        /// Attempts to measure the filesystem that contains <paramref name="path"/>.
        /// </summary>
        /// <param name="path">An existing directory, drive path, or UNC/NAS share.</param>
        /// <param name="totalBytes">The total size of the volume in bytes, or 0 when unmeasurable.</param>
        /// <param name="freeBytes">The bytes available to the caller, or 0 when unmeasurable.</param>
        /// <returns><see langword="true"/> when the path was measured; otherwise <see langword="false"/>.</returns>
        bool TryGetDiskSpace(string path, out long totalBytes, out long freeBytes);
    }
}
