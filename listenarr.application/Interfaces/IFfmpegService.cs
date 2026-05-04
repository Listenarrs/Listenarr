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
using Listenarr.Domain.Models;

namespace Listenarr.Application.Interfaces
{
    public interface IFfmpegService
    {
        /// <summary>
        /// Return the full path to ffprobe if present in the application's configured directory.
        /// This method will NOT attempt to download or install ffprobe when called. It only
        /// checks for an existing bundled binary and returns the path or null.
        /// </summary>
        Task<string?> GetFfprobePathAsync();

        /// <summary>
        /// Ensure that ffprobe is installed into the application's bundled directory. This
        /// performs the download/extract/install flow when a binary is not already present.
        /// Intended to be called once at program startup
        /// Returns the installed path or null if not available.
        /// </summary>
        Task<string?> EnsureFfprobeInstalledAsync();

        /// <summary>
        /// Execute the utility ffprobe against the given file
        /// </summary>
        /// <param name="filePath">File to execute ffprobe on</param>
        /// <returns>Parsed result of ffprobe execution</returns>
        /// <exception cref="FfmpegException">Raised when we are unable to run ffprobe on the given file</exception>
        Task<AudioMetadata> RunFfprobeAsync(string filePath);

        /// <summary>
        /// Give license notice content from FFprobe
        /// </summary>
        /// <returns>Content of the license file if any or empty string</returns>
        Task<string> GetLicenseAsync();
    }
}
