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

namespace Listenarr.Domain.Audiobooks
{
    /// <summary>
    /// Quality profile for automatic download selection
    /// </summary>
    public class QualityProfile
    {
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        public string? Description { get; set; }

        /// <summary>
        /// Ordered list of quality definitions with cutoff point
        /// </summary>
        public List<QualityDefinition> Qualities { get; set; } = new();

        /// <summary>
        /// The quality level to stop upgrading at (cutoff)
        /// </summary>
        public string? CutoffQuality { get; set; }

        /// <summary>
        /// Minimum file size in MB (0 = no minimum)
        /// </summary>
        public int MinimumSize { get; set; } = 0;

        /// <summary>
        /// Maximum file size in MB (0 = no maximum)
        /// </summary>
        public int MaximumSize { get; set; } = 0;

        /// <summary>
        /// Preferred file formats in order of preference
        /// </summary>
        public List<string> PreferredFormats { get; set; } = new() { "m4b", "mp3", "m4a", "flac", "opus" };

        /// <summary>
        /// Words/phrases that increase score (e.g., "unabridged", "retail")
        /// </summary>
        public List<string> PreferredWords { get; set; } = new();

        /// <summary>
        /// Words/phrases that must NOT be in the title (e.g., "abridged", "sample")
        /// </summary>
        public List<string> MustNotContain { get; set; } = new();

        /// <summary>
        /// Words/phrases that must be in the title
        /// </summary>
        public List<string> MustContain { get; set; } = new();

        /// <summary>
        /// Preferred languages in order of preference (e.g., "English", "Spanish")
        /// </summary>
        public List<string> PreferredLanguages { get; set; } = new() { "English" };

        /// <summary>
        /// Minimum number of seeders for torrents (0 = no minimum)
        /// </summary>
        public int MinimumSeeders { get; set; } = 1;

        /// <summary>
        /// Minimum score threshold for automatic downloads
        /// Releases with TotalScore below this will be rejected. 0 = no minimum (allow any score)
        /// </summary>
        public int MinimumScore { get; set; } = 0;

        /// <summary>
        /// Whether this is the default profile for new audiobooks
        /// </summary>
        public bool IsDefault { get; set; } = false;

        /// <summary>
        /// Whether to prefer newer releases (higher age score)
        /// </summary>
        public bool PreferNewerReleases { get; set; } = true;

        /// <summary>
        /// Maximum age in days for releases (0 = no limit)
        /// </summary>
        public int MaximumAge { get; set; } = 0;

        /// <summary>
        /// Custom names for quality groups (codec -> custom name)
        /// e.g., { "MP3": "Lossy MP3", "FLAC": "High Quality" }
        /// </summary>
        public Dictionary<string, string>? CustomGroupNames { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Quality definition with priority - supports both flat (legacy) and grouped structure
    /// </summary>
    public sealed class QualityDefinition : IEquatable<QualityDefinition>
    {
        /// <summary>
        /// Quality identifier (e.g., "MP3 320kbps", "AAC 192kbps", "FLAC")
        /// For grouped qualities, this is the full quality ID (codec + bitrate)
        /// </summary>
        [Required]
        public string Quality { get; set; } = string.Empty;

        /// <summary>
        /// Whether this quality is allowed for downloads
        /// </summary>
        public bool Allowed { get; set; } = true;

        /// <summary>
        /// Priority order (lower number = higher priority, higher in list)
        /// </summary>
        public int Priority { get; set; } = 0;

        /// <summary>
        /// Codec group (e.g., "MP3", "AAC", "FLAC", "OPUS")
        /// Used for hierarchical organization in UI
        /// </summary>
        public string? Codec { get; set; }

        /// <summary>
        /// Bitrate in kbps (null for lossless or codecs without specific bitrate)
        /// </summary>
        public int? Bitrate { get; set; }

        /// <summary>
        /// Whether this is a lossless codec
        /// </summary>
        public bool IsLossless { get; set; } = false;

        public bool Equals(QualityDefinition? other)
        {
            if (other is null) return false;
            return Quality == other.Quality && Allowed == other.Allowed && Priority == other.Priority;
        }

        public override bool Equals(object? obj) => obj is QualityDefinition other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Quality, Allowed, Priority);
    }

    /// <summary>
    /// Scoring result for a search result
    /// </summary>
    public class QualityScore
    {
        public SearchResult SearchResult { get; set; } = new();
        public int TotalScore { get; set; }
        public Dictionary<string, int> ScoreBreakdown { get; set; } = new();
        public List<string> RejectionReasons { get; set; } = new();

        // Prowlarr-style composite smart scoring (optional)
        public double SmartScore { get; set; }
        public Dictionary<string, int> SmartScoreBreakdown { get; set; } = new();

        public bool IsRejected => RejectionReasons.Count > 0;
    }
}

