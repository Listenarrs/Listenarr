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
using System.Text.Json.Serialization;

namespace Listenarr.Domain.Audiobooks
{
    public class AudiobookFile
    {
        [Key]
        public int Id { get; set; }

        public int AudiobookId { get; set; }
        [JsonIgnore]
        public Audiobook? Audiobook { get; set; }

        // Full path to the file on disk
        public string? Path { get; set; }

        // Size in bytes
        public long? Size { get; set; }

        // Duration in seconds
        public double? DurationSeconds { get; set; }

        // Format name (e.g., m4b, mp3, flac)
        public string? Format { get; set; }
        // Extracted container (e.g., M4B, MP4)
        public string? Container { get; set; }
        // Audio codec (e.g., aac, mp3, opus)
        public string? Codec { get; set; }

        // Bitrate in bits per second
        public int? Bitrate { get; set; }

        // Sample rate in Hz
        public int? SampleRate { get; set; }

        // Number of audio channels
        public int? Channels { get; set; }

        // When this file record was created
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Optional source or notes (e.g., DDL, qBittorrent, NZB)
        public string? Source { get; set; }
    }
}

