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
using Listenarr.Application.Common;

namespace Listenarr.Application.Interfaces
{
    public interface IArchiveExtractor
    {
        /// <summary>
        /// Extracts an archive to a temporary directory and returns the path of the temp directory, or null on failure.
        /// The caller is responsible for deleting the temporary directory when done.
        /// </summary>
        Task<TempDirectory?> ExtractArchiveToTempDirAsync(string archivePath);

        /// <summary>
        /// Returns true when the provided path appears to be a supported archive type.
        /// </summary>
        bool IsArchive(string filePath);
    }
}
