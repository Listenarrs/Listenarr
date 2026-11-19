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
    public interface IFfmpegService
    {
        /// <summary>
        /// Return the full path to ffprobe: the binary shipped with the application for this
        /// platform, or failing that one placed in the configured ffmpeg directory. Null when
        /// neither exists, or when the one that would be used fails its <c>-version</c> check; a
        /// failing packaged binary does not fall back to the configured one. Never downloads
        /// anything.
        /// </summary>
        Task<string?> GetFfprobePathAsync();

        /// <summary>
        /// Resolve ffprobe as <see cref="GetFfprobePathAsync"/> does and, on Unix, restore its
        /// execute permission where this process can. Intended to be called once at startup.
        /// Never downloads anything. Returns the path, or null when no binary exists or the one
        /// resolved fails its <c>-version</c> check.
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
        /// Executes ffprobe through a stable read path while validating and
        /// mapping the separate public media identity.
        /// </summary>
        Task<AudioMetadata> RunFfprobeAsync(MetadataFileSource fileSource);

        /// <summary>
        /// Give license notice content from FFprobe
        /// </summary>
        /// <returns>Content of the license file if any or empty string</returns>
        Task<string> GetLicenseAsync();
    }
}
