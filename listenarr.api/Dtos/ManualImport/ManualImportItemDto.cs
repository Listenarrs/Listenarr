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

namespace Listenarr.Api.Dtos.ManualImport
{
    public class ManualImportItemDto
    {
        [JsonPropertyName("relativePath")]
        public string RelativePath { get; set; } = string.Empty;

        [JsonPropertyName("fullPath")]
        [Required]
        public string? FullPath
        {
            get;
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    field = null;
                    return;
                }

                if (value.IndexOfAny(Path.GetInvalidPathChars()) != -1)
                {
                    throw new ArgumentException("Path is empty or contains invalid characters.", nameof(value));
                }

                if (value.Contains("..") || value.Contains("./") || value.Contains(".\\"))
                {
                    throw new ArgumentException("Path traversal attempts are not allowed.", nameof(value));
                }

                field = Path.GetFullPath(value);
            }
        }

        [JsonPropertyName("matchedAudiobookId")]
        public int MatchedAudiobookId { get; set; }

        [JsonPropertyName("releaseGroup")]
        public string? ReleaseGroup { get; set; }

        [JsonPropertyName("qualityProfileId")]
        public int? QualityProfileId { get; set; }

        [JsonPropertyName("language")]
        public string? Language { get; set; }

        [JsonPropertyName("size")]
        public string? Size { get; set; }

        [JsonIgnore]
        public int? SequenceNumberHint { get; set; }

        [JsonIgnore]
        public int? DiskNumberHint { get; set; }

        [JsonIgnore]
        public int? ChapterNumberHint { get; set; }
    }
}
