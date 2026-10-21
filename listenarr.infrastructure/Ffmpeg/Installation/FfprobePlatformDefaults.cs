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

namespace Listenarr.Infrastructure.Ffmpeg.Installation
{
    internal static class FfprobePlatformDefaults
    {
        public static string? GetDownloadUrl()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return GetDownloadUrl(OSPlatform.Linux, RuntimeInformation.OSArchitecture);
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return GetDownloadUrl(OSPlatform.OSX, RuntimeInformation.OSArchitecture);
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return GetDownloadUrl(OSPlatform.Windows, RuntimeInformation.OSArchitecture);
            }

            return null;
        }

        /// <summary>
        /// Resolves the archive URL for an explicit platform, so the mapping for every platform can
        /// be asserted from a test host running on any one of them.
        /// </summary>
        /// <remarks>
        /// Two constraints apply to anything returned here. The archive has to contain ffprobe:
        /// the Linux and Windows builds ship a bundle carrying both binaries, but evermeet
        /// publishes one binary per archive, so macOS needs the ffprobe archive and not the ffmpeg
        /// one. The URL also has to end in a suffix <see cref="FfprobeArchiveExtractor"/>
        /// recognises, which rules out endpoints that redirect to the current release without
        /// naming a file.
        /// </remarks>
        internal static string? GetDownloadUrl(OSPlatform platform, Architecture architecture)
        {
            if (platform == OSPlatform.Linux)
            {
                return architecture == Architecture.Arm64
                    ? "https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-arm64-static.tar.xz"
                    : "https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz";
            }

            if (platform == OSPlatform.OSX)
            {
                return "https://evermeet.cx/ffmpeg/ffprobe-6.0.zip";
            }

            if (platform == OSPlatform.Windows)
            {
                return "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
            }

            return null;
        }

        public static string? GetChecksum()
        {
            return null;
        }
    }
}
