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

namespace Listenarr.Domain.Configuration
{
    public class FfmpegConfig
    {
        // Provider key: e.g., "johnvansickle", "gyan", "evermeet", or "github:<owner>/<repo>"
        public string? Provider { get; set; }

        // Optional explicit asset name or tag to pin a release, e.g., "ffmpeg-6.0.zip" or "6.0"
        public string? ReleaseOverride { get; set; }

        // Optional URL template for checksum file discovery (e.g., GitHub releases assets or a SHA file)
        public string? ChecksumUrl { get; set; }

        // Optional architecture hint, e.g., "x86_64", "arm64"
        public string? Arch { get; set; }
    }
}
