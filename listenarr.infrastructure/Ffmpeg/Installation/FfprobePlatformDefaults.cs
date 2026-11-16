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
                if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
                {
                    return "https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-arm64-static.tar.xz";
                }

                return "https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return "https://evermeet.cx/ffmpeg/ffmpeg-6.0.zip";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
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
