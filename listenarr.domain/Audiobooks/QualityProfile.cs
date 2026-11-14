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
        /// Whether an already-acquired audiobook may be replaced by a better release.
        /// </summary>
        /// <remarks>
        /// The same flag Readarr and Sonarr carry (<c>public bool UpgradeAllowed</c>, at
        /// src/NzbDrone.Core/Profiles/Qualities/QualityProfile.cs:17 in both). Listenarr used to
        /// encode "upgrades off" as a blank <see cref="CutoffQuality"/>. That left no way to say
        /// "upgrades off, and here is the cutoff I had picked", and no way for a profile with
        /// upgrades off to satisfy a cutoff rule at all.
        ///
        /// Defaults to true so a caller that omits the field gets what a profile with a cutoff has
        /// always done. The migration that adds the column derives the value from the stored
        /// cutoff instead, so no existing profile changes meaning.
        ///
        /// That default is also what a PUT which omits the field will store, because the controller
        /// binds this entity straight from the request body and a whole-document replace has
        /// nothing to distinguish "absent" from "false". Every other field on this profile has
        /// always behaved the same way, and the UI sends back the whole object it read
        /// (fe/src/views/settings/QualityProfilesTab.vue:535 on save, :563 for the set-default
        /// button), so nothing in the application hits it. Pinned, with MinimumSeeders as the
        /// control that shows it is the endpoint's contract rather than this field's, by
        /// Update_OmittingAField_ResetsItToItsDefault_ForTheFlagAndForItsNeighbour.
        ///
        /// Worth saying that the family does not solve this either: their API resource is a
        /// separate type but declares the same non-nullable
        /// <c>public bool UpgradeAllowed</c> (src/Readarr.Api.V1/Profiles/Quality/QualityProfileResource.cs:13,
        /// src/Sonarr.Api.V3/Profiles/Quality/QualityProfileResource.cs:13), so a PUT that omits
        /// it lands on false there for the same reason it lands on true here. Fixing it properly
        /// means nullable fields on an inbound resource, which is a change to every field at once
        /// and does not belong on this branch.
        /// </remarks>
        public bool UpgradeAllowed { get; set; } = true;

        /// <summary>
        /// The quality level to stop upgrading at (cutoff)
        /// </summary>
        /// <remarks>
        /// Validated on save only, and only while <see cref="UpgradeAllowed"/> is true. A profile
        /// stored before this rule existed keeps whatever it holds and is still returned by the
        /// API unchanged.
        /// </remarks>
        [ValidCutoff]
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

