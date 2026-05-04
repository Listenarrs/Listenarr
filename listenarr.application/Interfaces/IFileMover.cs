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
using Listenarr.Domain.Models.Enumerations;

namespace Listenarr.Application.Interfaces
{
    /// <summary>
    /// This class responsability is to handle all file manipulation operations
    /// </summary>
    public interface IFileMover
    {
        Task<bool> MoveDirectoryAsync(string source, string destination);

        Task<bool> CopyDirectoryAsync(string source, string destination);

        /// <summary>
        /// Perform the given action on the given file
        /// </summary>
        /// <param name="action">What we want to do with the file</param>
        /// <param name="source">File</param>
        /// <param name="destination">Optional destination of the action</param>
        /// <returns>True in case of success, false otherwise</returns>
        Task<bool> PerformActionOn(FileAction action, string source, string? destination = null);
    }
}
